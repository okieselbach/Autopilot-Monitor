using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the PR4 cascade-deletion progress-blob Download + CAS-Update helpers on
/// <see cref="BlobStorageService"/>. Same fake-subclass pattern as
/// <c>BlobStorageServiceDeletionManifestTests</c>: override the blob-IO seams, capture bytes
/// + options, replay on download. No Azurite.
/// </summary>
public class BlobStorageServiceDeletionProgressTests
{
    private const string TenantId  = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";
    private const string ManifestId = "0123456789ABCDEF_FEDCBA9876543210";
    private const string Sha256 = "1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public async Task Download_round_trips_progress_with_etag()
    {
        var sut = new FakeBlobStorageService();
        // Seed the fake with an initial progress blob written via the existing helper.
        var initialEtag = await sut.UploadInitialDeletionProgressAsync(TenantId, SessionId, ManifestId, Sha256);
        Assert.False(string.IsNullOrEmpty(initialEtag));

        var (progress, etag) = await sut.DownloadDeletionProgressAsync(TenantId, SessionId, ManifestId);

        Assert.Equal(Sha256, progress.SnapshotSha256);
        Assert.Empty(progress.CompletedSteps);
        Assert.False(progress.VerificationDone);
        Assert.Null(progress.CompletedAt);
        Assert.False(string.IsNullOrEmpty(etag));
        // ETag from a fresh download must match what UploadInitial returned.
        Assert.Equal(initialEtag, etag);
    }

    [Fact]
    public async Task Update_with_matching_etag_writes_new_state_and_returns_fresh_etag()
    {
        var sut = new FakeBlobStorageService();
        var initialEtag = await sut.UploadInitialDeletionProgressAsync(TenantId, SessionId, ManifestId, Sha256);
        var (progress, etag) = await sut.DownloadDeletionProgressAsync(TenantId, SessionId, ManifestId);

        progress.CompletedSteps.Add(1);
        progress.CompletedSteps.Add(2);
        var newEtag = await sut.UpdateDeletionProgressAsync(TenantId, SessionId, ManifestId, progress, etag);

        Assert.False(string.IsNullOrEmpty(newEtag));
        Assert.NotEqual(initialEtag, newEtag);

        var (refetched, _) = await sut.DownloadDeletionProgressAsync(TenantId, SessionId, ManifestId);
        Assert.Equal(new HashSet<int> { 1, 2 }, refetched.CompletedSteps);
    }

    [Fact]
    public async Task Update_with_mismatched_etag_throws_412_RequestFailedException()
    {
        var sut = new FakeBlobStorageService();
        await sut.UploadInitialDeletionProgressAsync(TenantId, SessionId, ManifestId, Sha256);
        var (progress, _) = await sut.DownloadDeletionProgressAsync(TenantId, SessionId, ManifestId);

        progress.CompletedSteps.Add(1);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(
            () => sut.UpdateDeletionProgressAsync(TenantId, SessionId, ManifestId, progress, ifMatchEtag: "\"0xSTALE_ETAG\""));
        Assert.Equal(412, ex.Status);
    }

    [Fact]
    public async Task Update_writes_IfMatch_condition_on_the_upload_options()
    {
        var sut = new FakeBlobStorageService();
        await sut.UploadInitialDeletionProgressAsync(TenantId, SessionId, ManifestId, Sha256);
        var (progress, etag) = await sut.DownloadDeletionProgressAsync(TenantId, SessionId, ManifestId);

        await sut.UpdateDeletionProgressAsync(TenantId, SessionId, ManifestId, progress, etag);

        Assert.NotNull(sut.LastWrittenProgressOptions);
        Assert.NotNull(sut.LastWrittenProgressOptions!.Conditions);
        // PR4 §12-Q10 contract: every update is an ETag-CAS via IfMatch (not IfNoneMatch).
        Assert.NotNull(sut.LastWrittenProgressOptions.Conditions!.IfMatch);
        Assert.Equal(new ETag(etag), sut.LastWrittenProgressOptions.Conditions.IfMatch);
        // Content type stays application/json — no gzip on the small progress blob.
        Assert.Equal("application/json", sut.LastWrittenProgressOptions.HttpHeaders!.ContentType);
    }

    [Fact]
    public async Task Update_throws_on_missing_required_fields()
    {
        var sut = new FakeBlobStorageService();

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.UpdateDeletionProgressAsync("", SessionId, ManifestId, new DeletionProgress(), "etag"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.UpdateDeletionProgressAsync(TenantId, "", ManifestId, new DeletionProgress(), "etag"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.UpdateDeletionProgressAsync(TenantId, SessionId, "", new DeletionProgress(), "etag"));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => sut.UpdateDeletionProgressAsync(TenantId, SessionId, ManifestId, null!, "etag"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.UpdateDeletionProgressAsync(TenantId, SessionId, ManifestId, new DeletionProgress(), ""));
    }

    [Fact]
    public async Task Download_throws_on_missing_required_fields()
    {
        var sut = new FakeBlobStorageService();

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.DownloadDeletionProgressAsync("", SessionId, ManifestId));
        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.DownloadDeletionProgressAsync(TenantId, "", ManifestId));
        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.DownloadDeletionProgressAsync(TenantId, SessionId, ""));
    }

    // ---------------------------------------------------------------- Test fixture ----

    /// <summary>
    /// Captures the progress-blob bytes + options written by Upload/Update and replays them on
    /// Download. Each write returns a fresh ETag; an Update with a stale ETag throws 412 so the
    /// Handler's bounded-retry loop can be exercised against this fake.
    /// </summary>
    private sealed class FakeBlobStorageService : BlobStorageService
    {
        private byte[]? _lastBytes;
        private BlobUploadOptions? _lastOptions;
        private ETag _currentEtag;
        private int _etagCounter;

        public BlobUploadOptions? LastWrittenProgressOptions => _lastOptions;

        public FakeBlobStorageService()
            : base(new BlobServiceClient("UseDevelopmentStorage=true"), NullLogger<BlobStorageService>.Instance, usesManagedIdentity: false)
        {
        }

        protected internal override Task<ETag> WriteDeletionProgressBlobAsync(
            string blobName, byte[] payload, BlobUploadOptions options, CancellationToken cancellationToken)
        {
            if (options.Conditions?.IfMatch != null
                && _currentEtag != default
                && options.Conditions.IfMatch != _currentEtag)
            {
                throw new RequestFailedException(412, "ConditionNotMet", "ConditionNotMet", innerException: null);
            }
            _lastBytes = payload;
            _lastOptions = options;
            _currentEtag = new ETag($"\"0xFAKE_ETAG_{++_etagCounter}\"");
            return Task.FromResult(_currentEtag);
        }

        protected internal override Task<(byte[] Payload, ETag ETag)> ReadDeletionProgressBlobAsync(
            string blobName, CancellationToken cancellationToken)
        {
            if (_lastBytes == null)
            {
                throw new InvalidOperationException(
                    "Test must call UploadInitialDeletionProgressAsync before DownloadDeletionProgressAsync.");
            }
            return Task.FromResult((_lastBytes, _currentEtag));
        }
    }
}
