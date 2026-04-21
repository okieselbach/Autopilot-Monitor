using AutopilotMonitor.Functions.DataAccess.TableStorage;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pure PK/RK format tests for <see cref="IndexRowKeys"/> (Plan §2.8 query matrix, §M5.d).
/// These verify the string shape contract the queue-driven index writer + the reconciliation
/// timer both depend on — any drift here silently breaks cross-session queries.
/// </summary>
public class IndexRowKeysTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static readonly DateTime Occurred =
        new(2026, 4, 21, 14, 7, 33, DateTimeKind.Utc);

    // ============================================================ SessionsByTerminal

    [Fact]
    public void SessionsByTerminal_pk_is_tenant_underscore_terminalStage()
    {
        Assert.Equal(
            $"{TenantId}_WhiteGloveSealed",
            IndexRowKeys.BuildSessionsByTerminalPk(TenantId, "WhiteGloveSealed"));
    }

    [Fact]
    public void SessionsByTerminal_rk_is_yyyyMMddHHmmss_underscore_sessionId()
    {
        Assert.Equal(
            $"20260421140733_{SessionId}",
            IndexRowKeys.BuildSessionsByTerminalRk(Occurred, SessionId));
    }

    // ============================================================ SessionsByStage

    [Fact]
    public void SessionsByStage_pk_is_tenant_underscore_stage()
    {
        Assert.Equal(
            $"{TenantId}_EspInProgress",
            IndexRowKeys.BuildSessionsByStagePk(TenantId, "EspInProgress"));
    }

    [Fact]
    public void SessionsByStage_rk_pads_ticks_to_19_digits_for_lex_ordering()
    {
        var rk = IndexRowKeys.BuildSessionsByStageRk(Occurred, SessionId);

        // 19-digit zero-padding is the contract — string comparison then matches numeric time order.
        var parts = rk.Split('_');
        Assert.Equal(19, parts[0].Length);
        Assert.Equal(Occurred.Ticks.ToString("D19"), parts[0]);
        Assert.Equal(SessionId, parts[1]);
    }

    [Fact]
    public void SessionsByStage_rk_orders_lexicographically_matching_chronology()
    {
        var earlier = IndexRowKeys.BuildSessionsByStageRk(Occurred, SessionId);
        var later   = IndexRowKeys.BuildSessionsByStageRk(Occurred.AddSeconds(1), SessionId);

        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    // ============================================================ DeadEndsByReason

    [Fact]
    public void DeadEndsByReason_pk_is_tenant_underscore_reason()
    {
        Assert.Equal(
            $"{TenantId}_hybrid_reboot_gate_blocking",
            IndexRowKeys.BuildDeadEndsByReasonPk(TenantId, "hybrid_reboot_gate_blocking"));
    }

    [Fact]
    public void DeadEndsByReason_rk_combines_timestamp_session_stepIndex_padded_to_6()
    {
        Assert.Equal(
            $"20260421140733_{SessionId}_000042",
            IndexRowKeys.BuildDeadEndsByReasonRk(Occurred, SessionId, 42));
    }

    // ============================================================ ClassifierVerdictsByIdLevel

    [Fact]
    public void ClassifierVerdictsByIdLevel_pk_is_tenant_underscore_classifierId_underscore_level()
    {
        Assert.Equal(
            $"{TenantId}_whiteglove-sealing_Weak",
            IndexRowKeys.BuildClassifierVerdictsByIdLevelPk(TenantId, "whiteglove-sealing", "Weak"));
    }

    [Fact]
    public void ClassifierVerdictsByIdLevel_rk_combines_timestamp_session_stepIndex_padded_to_6()
    {
        Assert.Equal(
            $"20260421140733_{SessionId}_000007",
            IndexRowKeys.BuildClassifierVerdictsByIdLevelRk(Occurred, SessionId, 7));
    }

    // ============================================================ SignalsByKind

    [Fact]
    public void SignalsByKind_pk_is_tenant_underscore_signalKind()
    {
        Assert.Equal(
            $"{TenantId}_EspTerminalFailure",
            IndexRowKeys.BuildSignalsByKindPk(TenantId, "EspTerminalFailure"));
    }

    [Fact]
    public void SignalsByKind_rk_combines_timestamp_session_ordinal_padded_to_10()
    {
        Assert.Equal(
            $"20260421140733_{SessionId}_0000000123",
            IndexRowKeys.BuildSignalsByKindRk(Occurred, SessionId, 123));
    }

    // ============================================================ UTC coercion + sanitizing

    [Fact]
    public void Timestamp_always_normalizes_to_UTC()
    {
        // If a caller passes Local-kind DateTime, conversion must still produce the UTC wall clock.
        var localKind = DateTime.SpecifyKind(Occurred, DateTimeKind.Local);
        var rk        = IndexRowKeys.BuildSessionsByTerminalRk(localKind, SessionId);

        Assert.Equal($"{localKind.ToUniversalTime():yyyyMMddHHmmss}_{SessionId}", rk);
    }

    [Theory]
    [InlineData("foo/bar",   "foo_bar")]
    [InlineData("foo\\bar",  "foo_bar")]
    [InlineData("foo#bar",   "foo_bar")]
    [InlineData("foo?bar",   "foo_bar")]
    [InlineData("clean_name", "clean_name")]
    [InlineData("",          "")]
    public void Sanitize_replaces_forbidden_key_chars_with_underscore(string input, string expected)
    {
        Assert.Equal(expected, IndexRowKeys.Sanitize(input));
    }

    [Fact]
    public void Sanitize_preserves_enum_style_identifiers_unchanged()
    {
        // Discriminators are overwhelmingly enum names — sanitize must not mangle them.
        Assert.Equal("WhiteGloveSealed", IndexRowKeys.Sanitize("WhiteGloveSealed"));
        Assert.Equal("EspTerminalFailure", IndexRowKeys.Sanitize("EspTerminalFailure"));
        Assert.Equal("hybrid_reboot_gate_blocking", IndexRowKeys.Sanitize("hybrid_reboot_gate_blocking"));
    }
}
