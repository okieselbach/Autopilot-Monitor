using System.Diagnostics;
using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="DeviceAssociationValidator"/> — focused on pure-function pieces:
/// JSON-to-DTO mapping (incl. exact-match guard), cache key shape, and Activity-tag
/// enrichment used by SecurityValidator. The HTTP/cache/retry resilience is mirrored
/// 1:1 from <c>AutopilotDeviceValidator</c>; behavioural drift would surface here.
/// </summary>
public class DeviceAssociationValidatorTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string Serial = "1730-2406-2605-6305-0260-8436-93";

    // -- BuildCacheKey --

    [Fact]
    public void BuildCacheKey_StableShape()
    {
        var key = DeviceAssociationValidator.BuildCacheKey(TenantId, Serial);
        Assert.Equal($"device-association:{TenantId}:{Serial}", key);
    }

    [Fact]
    public void BuildCacheKey_DistinctFromAutopilotValidatorKey()
    {
        // The two validators must NOT share cache keys; otherwise a positive Autopilot
        // hit would incorrectly satisfy a DevPrep lookup.
        var devPrepKey = DeviceAssociationValidator.BuildCacheKey(TenantId, Serial);
        Assert.StartsWith("device-association:", devPrepKey);
        Assert.DoesNotContain("autopilot", devPrepKey);
    }

    // -- ParseTenantAssociatedDevicesResponse: empty / missing / malformed --

    [Fact]
    public void Parse_EmptyValueArray_NotFound()
    {
        var body = "{\"value\":[]}";
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, Serial);
        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Equal(Serial, result.SerialNumber);
        Assert.Contains("not associated", result.ErrorMessage);
    }

    [Fact]
    public void Parse_MissingValueProperty_NotFound()
    {
        var body = "{}";
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, Serial);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Parse_MalformedJson_NotFound_NotTransient()
    {
        // Defensive: bad payload is treated as "not associated", not as a transient
        // — transient handling is only triggered by HTTP-level failures.
        var body = "this is not json";
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, Serial);
        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
    }

    // -- ParseTenantAssociatedDevicesResponse: exact-match guard --

    [Fact]
    public void Parse_OnlySimilarSerial_NotFound()
    {
        // Guards against widened filter semantics: even if Graph returns devices whose serial
        // merely contains the query, we must require an exact match before declaring success.
        var body = $@"{{""value"":[{{""serialNumber"":""{Serial}-OTHER"",""associationState"":""preassociated""}}]}}";
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, Serial);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Parse_ExactSerialMatch_PopulatesAllFields()
    {
        var body = $@"{{
            ""value"":[{{
                ""id"":""f8226ccb-e464-1842-a341-325b2a4fd906"",
                ""serialNumber"":""{Serial}"",
                ""associationState"":""preassociated"",
                ""devicePreparationPolicyId"":""00000000-0000-0000-0000-000000000000"",
                ""preassociationDateTime"":""2026-04-14T12:00:21.5029229Z"",
                ""associationDateTime"":""0001-01-01T00:00:00Z"",
                ""preassociatedByUserPrincipalName"":""admin@contoso.com"",
                ""assignedToUserPrincipalName"":null,
                ""managedDeviceId"":""00000000-0000-0000-0000-000000000000""
            }}]
        }}";

        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, Serial);

        Assert.True(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Equal(Serial, result.SerialNumber);
        Assert.Equal("preassociated", result.AssociationState);
        Assert.Equal("00000000-0000-0000-0000-000000000000", result.DevicePreparationPolicyId);
        Assert.Equal("admin@contoso.com", result.PreAssociatedByUserPrincipalName);
        // Newtonsoft.Json yields "" for JSON null when calling .ToString() on the JToken;
        // downstream telemetry uses IsNullOrEmpty so both null and "" surface as "absent".
        Assert.True(string.IsNullOrEmpty(result.AssignedToUserPrincipalName));
        Assert.NotNull(result.PreAssociationDateTime);
        Assert.Null(result.AssociationDateTime); // Graph "0001-01-01" → null (unset DateTimeOffset sentinel)
    }

    [Fact]
    public void Parse_TrimsWhitespace_OnGraphSerialBeforeMatching()
    {
        // Defensive: protect against Graph stragglers with stray whitespace.
        var body = $@"{{""value"":[{{""serialNumber"":""  {Serial}  "",""associationState"":""preassociated""}}]}}";
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, Serial);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_CaseInsensitiveSerialMatch()
    {
        var lower = Serial.ToLowerInvariant();
        var upper = Serial.ToUpperInvariant();
        var body = $@"{{""value"":[{{""serialNumber"":""{upper}"",""associationState"":""preassociated""}}]}}";
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, lower);
        Assert.True(result.IsValid);
    }

    // -- EnrichRequestTelemetryWithDeviceAssociation --

    [Fact]
    public void Enrich_PopulatesActivityTags_WhenMatched()
    {
        using var activity = new Activity("test").Start();
        var result = new DeviceAssociationResult
        {
            IsValid = true,
            IsTransient = false,
            AssociationState = "preassociated",
            DevicePreparationPolicyId = "policy-1",
            PreAssociationDateTime = new DateTime(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc)
        };

        SecurityValidator.EnrichRequestTelemetryWithDeviceAssociation(result);

        var tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("true", tags["devprep.association.matched"]);
        Assert.Equal("false", tags["devprep.association.transient"]);
        Assert.Equal("preassociated", tags["devprep.association.state"]);
        Assert.Equal("policy-1", tags["devprep.association.policyId"]);
        Assert.Contains("devprep.association.preAssociationUtc", tags.Keys);
    }

    [Fact]
    public void Enrich_OnlyMatchedAndTransient_WhenNoOtherFields()
    {
        using var activity = new Activity("test").Start();
        var result = new DeviceAssociationResult { IsValid = false, IsTransient = true };

        SecurityValidator.EnrichRequestTelemetryWithDeviceAssociation(result);

        var tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("false", tags["devprep.association.matched"]);
        Assert.Equal("true", tags["devprep.association.transient"]);
        Assert.DoesNotContain("devprep.association.state", tags.Keys);
        Assert.DoesNotContain("devprep.association.policyId", tags.Keys);
    }

    [Fact]
    public void Enrich_NoActivity_DoesNotThrow()
    {
        // Activity.Current is null in environments where no listener registered the activity source.
        // Must be a safe no-op.
        Activity.Current = null;
        var result = new DeviceAssociationResult { IsValid = true };
        var ex = Record.Exception(() => SecurityValidator.EnrichRequestTelemetryWithDeviceAssociation(result));
        Assert.Null(ex);
    }
}
