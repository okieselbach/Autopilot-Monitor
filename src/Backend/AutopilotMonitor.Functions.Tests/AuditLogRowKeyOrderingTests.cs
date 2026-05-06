using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Audit-log RowKey ordering invariants. Replaces the prior bare-GUID RowKey,
/// which made paged audit reads return random-order pages — Azure Tables sorts
/// by RowKey ASC within a partition, so a random GUID gave random ordering, and
/// the in-page Timestamp re-sort only fixed ordering inside that random slice
/// (page 1 was not necessarily the newest 50 audits).
/// </summary>
public class AuditLogRowKeyOrderingTests
{
    [Fact]
    public void Newer_audit_rowkey_sorts_before_older_audit_rowkey()
    {
        // Azure Tables returns rows in (PK asc, RK asc); we want ASC = newest-first.
        // Reverse-tick: newer timestamp → smaller revtick → smaller RowKey string.
        var now = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        var oneHourAgo = now.AddHours(-1);

        var rkNow = TableStorageService.BuildAuditLogRowKey(now, Guid.Empty);
        var rkOld = TableStorageService.BuildAuditLogRowKey(oneHourAgo, Guid.Empty);

        Assert.True(string.Compare(rkNow, rkOld, StringComparison.Ordinal) < 0,
            $"Expected newer rowkey '{rkNow}' to sort before older rowkey '{rkOld}'");
    }

    [Fact]
    public void New_audit_rowkey_sorts_before_legacy_bare_guid_rowkey_starting_with_digit()
    {
        // The biggest migration trap: legacy GUIDs starting with '0'-'9' would
        // otherwise interleave with — or precede — current-era revtick values
        // ("2…" or "3…"). The '!' prefix (0x21) sorts before all hex digits so
        // every new entry lands ahead of every legacy entry.
        var now = DateTime.UtcNow;
        var newRk = TableStorageService.BuildAuditLogRowKey(now, Guid.NewGuid());

        // Legacy: bare GUID, all 16 hex first chars are possible.
        var legacyDigitGuid = "0a1b2c3d-4e5f-6789-abcd-ef0123456789";
        var legacyHexGuid   = "ffffffff-ffff-ffff-ffff-ffffffffffff";
        var legacyZeroGuid  = "00000000-0000-0000-0000-000000000000";

        Assert.True(string.Compare(newRk, legacyDigitGuid, StringComparison.Ordinal) < 0,
            $"New rowkey '{newRk}' should sort before legacy digit-GUID rowkey '{legacyDigitGuid}'");
        Assert.True(string.Compare(newRk, legacyHexGuid, StringComparison.Ordinal) < 0,
            $"New rowkey '{newRk}' should sort before legacy hex-GUID rowkey '{legacyHexGuid}'");
        Assert.True(string.Compare(newRk, legacyZeroGuid, StringComparison.Ordinal) < 0,
            $"New rowkey '{newRk}' should sort before legacy all-zero-GUID rowkey '{legacyZeroGuid}'");
    }

    [Fact]
    public void Same_tick_writes_have_unique_rowkeys()
    {
        // Reverse-tick alone collides for two writes within the same 100-ns tick —
        // the GUID suffix prevents one of them from silently overwriting the other.
        var ts = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);

        var rk1 = TableStorageService.BuildAuditLogRowKey(ts, Guid.NewGuid());
        var rk2 = TableStorageService.BuildAuditLogRowKey(ts, Guid.NewGuid());

        Assert.NotEqual(rk1, rk2);
        // Both share the same revtick prefix — only the suffix differs.
        var prefix1 = rk1.Substring(0, 20); // "!" + 19 digits
        var prefix2 = rk2.Substring(0, 20);
        Assert.Equal(prefix1, prefix2);
    }

    [Fact]
    public void RowKey_starts_with_marker_prefix()
    {
        // Sanity check on the wire format — the prefix is what guarantees
        // separation from legacy rows; if it ever drifts the migration window
        // promise breaks silently.
        var rk = TableStorageService.BuildAuditLogRowKey(DateTime.UtcNow, Guid.NewGuid());
        Assert.StartsWith("!", rk);
    }

    [Fact]
    public void Audit_global_tenant_id_is_a_well_formed_guid()
    {
        // The synthetic global-tenant partition is added to the audit fan-out
        // alongside real tenants from TenantConfiguration. It must parse as a
        // GUID since BuildAuditLogFilterWithRowKeyBound interpolates it into an
        // OData PartitionKey filter — a malformed value would fail the table
        // query rather than silently skipping the partition.
        Assert.True(Guid.TryParse(Constants.AuditGlobalTenantId, out _),
            $"Constants.AuditGlobalTenantId='{Constants.AuditGlobalTenantId}' must be a valid GUID");
    }
}
