using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Backup;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Backup;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Validator covers the five reject paths from the plan:
/// <list type="number">
///   <item>tableName not in catalog (deferred — handled by the preflight, not this validator)</item>
///   <item>manifest missing → BackupNotFound</item>
///   <item>manifest entry missing → TableNotInBackup</item>
///   <item>BlobName not canonical → ManifestCorrupt</item>
///   <item>SHA mismatch → IntegrityCheckFailed</item>
/// </list>
/// Plus the entry-Status check (Failed/Skipped entries refused) and the
/// ETag-pin propagation invariant.
/// </summary>
public class BackupRestoreInputValidatorTests
{
    private const string BackupId = "20260522T040000Z_a1b2c3d4";
    private const string Table = Constants.TableNames.AnalyzeRules;

    [Fact]
    public async Task ValidateAsync_returns_pinned_etag_and_entry_on_happy_path()
    {
        var bytes = BuildNdjsonLine("p", "r", new { Setting = "x" });
        var sha = ComputeSha(bytes);
        var manifest = BuildManifest(new CriticalTableBackupTableEntry
        {
            TableName = Table,
            Status = TableBackupStatus.Ok,
            RowCount = 1,
            ByteSize = bytes.Length,
            Sha256Hex = sha,
            BlobName = $"{BackupId}/{Table}.ndjson",
        });

        var fakeStore = new FakeStore(manifest, bytes, new ETag("\"0xABC\""));
        var sut = new BackupRestoreInputValidator(fakeStore, NullLogger<BackupRestoreInputValidator>.Instance);

        var result = await sut.ValidateAsync(BackupId, Table, CancellationToken.None);

        Assert.Equal(BackupId, result.Manifest.BackupId);
        Assert.Equal(Table, result.Entry.TableName);
        Assert.Equal(new ETag("\"0xABC\""), result.BlobETag);
    }

    [Fact]
    public async Task ValidateAsync_throws_BackupNotFound_when_manifest_missing()
    {
        var fakeStore = new FakeStore(manifest: null, ndjson: Array.Empty<byte>(), new ETag("\"e\""));
        var sut = new BackupRestoreInputValidator(fakeStore, NullLogger<BackupRestoreInputValidator>.Instance);

        var ex = await Assert.ThrowsAsync<BackupTerminalException>(() => sut.ValidateAsync(BackupId, Table, CancellationToken.None));
        Assert.Equal("BackupNotFound", ex.Code);
    }

    [Fact]
    public async Task ValidateAsync_throws_ManifestCorrupt_when_payload_is_garbage()
    {
        var fakeStore = new FakeStore(rawManifestBytes: Encoding.UTF8.GetBytes("not json"), ndjson: Array.Empty<byte>(), new ETag("\"e\""));
        var sut = new BackupRestoreInputValidator(fakeStore, NullLogger<BackupRestoreInputValidator>.Instance);

        var ex = await Assert.ThrowsAsync<BackupTerminalException>(() => sut.ValidateAsync(BackupId, Table, CancellationToken.None));
        Assert.Equal("ManifestCorrupt", ex.Code);
    }

    [Fact]
    public async Task ValidateAsync_throws_TableNotInBackup_when_table_not_in_manifest()
    {
        var manifest = BuildManifest(new CriticalTableBackupTableEntry
        {
            TableName = Constants.TableNames.Feedback,    // different table
            Status = TableBackupStatus.Ok,
            BlobName = $"{BackupId}/{Constants.TableNames.Feedback}.ndjson",
        });
        var fakeStore = new FakeStore(manifest, Array.Empty<byte>(), new ETag("\"e\""));
        var sut = new BackupRestoreInputValidator(fakeStore, NullLogger<BackupRestoreInputValidator>.Instance);

        var ex = await Assert.ThrowsAsync<BackupTerminalException>(() => sut.ValidateAsync(BackupId, Table, CancellationToken.None));
        Assert.Equal("TableNotInBackup", ex.Code);
    }

    [Theory]
    [InlineData(TableBackupStatus.Failed)]
    [InlineData(TableBackupStatus.Skipped)]
    public async Task ValidateAsync_refuses_restore_from_failed_or_skipped_entry(TableBackupStatus status)
    {
        var manifest = BuildManifest(new CriticalTableBackupTableEntry
        {
            TableName = Table,
            Status = status,
            BlobName = $"{BackupId}/{Table}.ndjson",
        });
        var fakeStore = new FakeStore(manifest, Array.Empty<byte>(), new ETag("\"e\""));
        var sut = new BackupRestoreInputValidator(fakeStore, NullLogger<BackupRestoreInputValidator>.Instance);

        var ex = await Assert.ThrowsAsync<BackupTerminalException>(() => sut.ValidateAsync(BackupId, Table, CancellationToken.None));
        Assert.Equal("TableNotInBackup", ex.Code);
    }

    [Fact]
    public async Task ValidateAsync_throws_ManifestCorrupt_when_BlobName_is_not_canonical()
    {
        var bytes = BuildNdjsonLine("p", "r", new { Setting = "x" });
        var sha = ComputeSha(bytes);
        var manifest = BuildManifest(new CriticalTableBackupTableEntry
        {
            TableName = Table,
            Status = TableBackupStatus.Ok,
            Sha256Hex = sha,
            BlobName = $"{BackupId}/some-other-name.ndjson",   // wrong
        });
        var fakeStore = new FakeStore(manifest, bytes, new ETag("\"e\""));
        var sut = new BackupRestoreInputValidator(fakeStore, NullLogger<BackupRestoreInputValidator>.Instance);

        var ex = await Assert.ThrowsAsync<BackupTerminalException>(() => sut.ValidateAsync(BackupId, Table, CancellationToken.None));
        Assert.Equal("ManifestCorrupt", ex.Code);
    }

    [Fact]
    public async Task ValidateAsync_throws_IntegrityCheckFailed_when_sha_does_not_match()
    {
        var bytes = BuildNdjsonLine("p", "r", new { Setting = "x" });
        var wrongSha = ComputeSha(Encoding.UTF8.GetBytes("different bytes")); // doesn't match actual ndjson
        var manifest = BuildManifest(new CriticalTableBackupTableEntry
        {
            TableName = Table,
            Status = TableBackupStatus.Ok,
            Sha256Hex = wrongSha,
            BlobName = $"{BackupId}/{Table}.ndjson",
        });
        var fakeStore = new FakeStore(manifest, bytes, new ETag("\"e\""));
        var sut = new BackupRestoreInputValidator(fakeStore, NullLogger<BackupRestoreInputValidator>.Instance);

        var ex = await Assert.ThrowsAsync<BackupTerminalException>(() => sut.ValidateAsync(BackupId, Table, CancellationToken.None));
        Assert.Equal("IntegrityCheckFailed", ex.Code);
    }

    // ── Test helpers ────────────────────────────────────────────────────────

    private static CriticalTableBackupManifest BuildManifest(CriticalTableBackupTableEntry entry)
    {
        return new CriticalTableBackupManifest
        {
            SchemaVersion = 1,
            BackupId = BackupId,
            StartedAtUtc = new DateTime(2026, 5, 22, 4, 0, 0, DateTimeKind.Utc),
            CompletedAtUtc = new DateTime(2026, 5, 22, 4, 0, 1, DateTimeKind.Utc),
            TriggeredBy = "Timer",
            Outcome = BackupOutcome.Success,
            Tables = { entry },
        };
    }

    private static byte[] BuildNdjsonLine(string pk, string rk, object props)
    {
        // Round-trip via DeletionRowDump so the test bytes mirror what the
        // production backup writer produces.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(props, BackupManifestJson.SerializerOptions);
        using var doc = JsonDocument.Parse(bytes);

        var dump = new DeletionRowDump
        {
            Pk = pk,
            Rk = rk,
            Etag = null,
            Props = new System.Collections.Generic.Dictionary<string, DeletionPropValue>(),
        };
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dump.Props[prop.Name] = new DeletionPropValue
            {
                EdmType = DeletionPropEdmType.String,
                Value = prop.Value.Clone(),
            };
        }
        return JsonSerializer.SerializeToUtf8Bytes(dump, BackupManifestJson.SerializerOptions);
    }

    private static string ComputeSha(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// Fake <see cref="BlobBackupStore"/> that returns canned manifest bytes +
    /// NDJSON bytes from in-memory state. Only the two read paths the validator
    /// uses are overridden; everything else throws so a test cannot silently
    /// hit the real Azure SDK.
    /// </summary>
    private sealed class FakeStore : BlobBackupStore
    {
        private readonly byte[]? _manifestBytes;
        private readonly byte[] _ndjsonBytes;
        private readonly ETag _etag;

        public FakeStore(CriticalTableBackupManifest? manifest, byte[] ndjson, ETag etag)
            : base(new FakeBlobStorageService(), NullLogger<BlobBackupStore>.Instance)
        {
            _manifestBytes = manifest is null ? null : JsonSerializer.SerializeToUtf8Bytes(manifest, BackupManifestJson.SerializerOptions);
            _ndjsonBytes = ndjson;
            _etag = etag;
        }

        public FakeStore(byte[] rawManifestBytes, byte[] ndjson, ETag etag)
            : base(new FakeBlobStorageService(), NullLogger<BlobBackupStore>.Instance)
        {
            _manifestBytes = rawManifestBytes;
            _ndjsonBytes = ndjson;
            _etag = etag;
        }

        public override Task EnsureContainerAsync(CancellationToken ct = default) => Task.CompletedTask;

        public override Task<(byte[]? Payload, ETag? ETag)> ReadManifestAsync(string backupId, CancellationToken ct = default)
            => Task.FromResult<(byte[]?, ETag?)>((_manifestBytes, _manifestBytes is null ? null : _etag));

        public override Task<(Stream Stream, ETag ETag)?> OpenNdjsonReadAsync(string backupId, string tableName, CancellationToken ct = default)
        {
            // The validator first calls ReadManifestAsync, then OpenNdjsonReadAsync.
            // We always return the same ETag here so the round-trip is consistent.
            Stream stream = new MemoryStream(_ndjsonBytes, writable: false);
            return Task.FromResult<(Stream, ETag)?>((stream, _etag));
        }
    }

    private sealed class FakeBlobStorageService : BlobStorageService
    {
        public FakeBlobStorageService()
            : base(new Azure.Storage.Blobs.BlobServiceClient(new Uri("https://example.invalid"), new Azure.Storage.StorageSharedKeyCredential("x", Convert.ToBase64String(new byte[32]))), NullLogger<BlobStorageService>.Instance) { }
    }
}
