#nullable enable
using AutopilotMonitor.SummaryDialog.Models;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.SummaryDialog.Tests
{
    /// <summary>
    /// Wire-format compat — the dialog must continue reading V1 JSON produced by older
    /// agent installs after a Schema 2 upgrade. These tests pin the deserialiser
    /// behaviour for both versions, so future schema bumps don't accidentally break
    /// reading older final-status.json files.
    /// </summary>
    public sealed class FinalStatusDeserializationTests
    {
        [Fact]
        public void V1_json_without_schema_version_deserialises_with_default_zero()
        {
            // Real-world V1 final-status.json (trimmed). Notably has NO schemaVersion field.
            var json = @"{
                ""timestamp"": ""2026-04-24T17:51:12Z"",
                ""outcome"": ""completed"",
                ""completionSource"": ""ime_pattern_canonical"",
                ""helloOutcome"": ""completed"",
                ""enrollmentType"": ""hybrid"",
                ""agentUptimeSeconds"": 623.4,
                ""signalsSeen"": [""hello_resolved"", ""desktop_arrived""],
                ""appSummary"": { ""totalApps"": 11, ""completedApps"": 11, ""errorCount"": 0,
                                  ""deviceErrors"": 0, ""userErrors"": 0,
                                  ""appsByPhase"": { ""DeviceSetup"": 8, ""AccountSetup"": 3 } },
                ""packageStatesByPhase"": {}
            }";

            var status = JsonConvert.DeserializeObject<FinalStatus>(json)!;

            Assert.Equal(0, status.SchemaVersion);
            Assert.False(OutcomeMapper.IsV2Schema(status));
            Assert.Equal(OutcomeKind.Success, OutcomeMapper.Map(status));
        }

        [Fact]
        public void V2_json_with_schema_two_uses_v2_render_path()
        {
            var json = @"{
                ""schemaVersion"": 2,
                ""timestamp"": ""2026-04-25T10:00:00Z"",
                ""outcome"": ""succeeded"",
                ""failureReason"": null,
                ""agentUptimeSeconds"": 480.0,
                ""appSummary"": { ""totalApps"": 0, ""completedApps"": 0, ""errorCount"": 0 },
                ""packageStatesByPhase"": {}
            }";

            var status = JsonConvert.DeserializeObject<FinalStatus>(json)!;

            Assert.Equal(2, status.SchemaVersion);
            Assert.True(OutcomeMapper.IsV2Schema(status));
            Assert.Equal(OutcomeKind.Success, OutcomeMapper.Map(status));
        }

        [Fact]
        public void V2_failed_outcome_carries_failure_reason_for_banner()
        {
            // The whole point of schema 2: actionable failure detail flows from agent to
            // dialog so the user sees *why* enrollment failed, not just that it did.
            var json = @"{
                ""schemaVersion"": 2,
                ""outcome"": ""failed"",
                ""failureReason"": ""Windows Hello provisioning timed out.""
            }";

            var status = JsonConvert.DeserializeObject<FinalStatus>(json)!;

            Assert.Equal(OutcomeKind.Failure, OutcomeMapper.Map(status));
            Assert.Equal("Windows Hello provisioning timed out.", status.FailureReason);
        }

        [Fact]
        public void V2_per_app_error_detail_round_trips_through_PackageInfo()
        {
            var json = @"{
                ""schemaVersion"": 2,
                ""outcome"": ""failed"",
                ""packageStatesByPhase"": {
                    ""Device"": [
                        {
                            ""appName"": ""Acme VPN Client"",
                            ""state"": ""Error"",
                            ""isError"": true,
                            ""isCompleted"": true,
                            ""targeted"": ""Device"",
                            ""errorPatternId"": ""IME-MSI-1603"",
                            ""errorDetail"": ""MSI exited with 1603 — install path locked."",
                            ""errorCode"": ""1603""
                        }
                    ]
                }
            }";

            var status = JsonConvert.DeserializeObject<FinalStatus>(json)!;
            var pkg = status.PackageStatesByPhase["Device"][0];

            Assert.True(pkg.IsError);
            Assert.Equal("IME-MSI-1603", pkg.ErrorPatternId);
            Assert.Equal("MSI exited with 1603 — install path locked.", pkg.ErrorDetail);
            Assert.Equal("1603", pkg.ErrorCode);
        }

        [Fact]
        public void V1_per_app_without_error_fields_deserialises_with_nulls()
        {
            var json = @"{
                ""packageStatesByPhase"": {
                    ""DeviceSetup"": [
                        {
                            ""appName"": ""Office"",
                            ""state"": ""Installed"",
                            ""isError"": false,
                            ""isCompleted"": true,
                            ""targeted"": ""Device""
                        }
                    ]
                }
            }";

            var status = JsonConvert.DeserializeObject<FinalStatus>(json)!;
            var pkg = status.PackageStatesByPhase["DeviceSetup"][0];

            Assert.Null(pkg.ErrorPatternId);
            Assert.Null(pkg.ErrorDetail);
            Assert.Null(pkg.ErrorCode);
            Assert.Null(pkg.DurationSeconds);
        }
    }
}
