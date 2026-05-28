#nullable enable
using System;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Diagnostics;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Diagnostics
{
    /// <summary>
    /// Event-shape tests for the previous_crash_detected builder. The dump-writing path is
    /// integration-only (P/Invoke into dbghelp.dll, hard to unit-test) and covered by the
    /// V2-agent test-VM smoke run.
    /// </summary>
    public sealed class PendingCrashReporterTests
    {
        private static AgentConfiguration Config() => new AgentConfiguration
        {
            SessionId = "S1",
            TenantId = "T1",
        };

        [Fact]
        public void BuildEvent_emits_warning_with_full_metadata()
        {
            var record = new CrashRecord
            {
                CrashedAt = new DateTime(2026, 5, 28, 10, 15, 30, DateTimeKind.Utc),
                SessionId = "previous-session-id",
                TenantId = "previous-tenant",
                AgentVersion = "2.0.999",
                Trigger = "AppDomain.UnhandledException",
                ExceptionType = "System.NullReferenceException",
                ExceptionMessage = "Object reference not set",
                StackTrace = "at FooBar()",
                DumpFilePath = "20260528_101530_abc.dmp",
                DumpFileSizeBytes = 12345,
                DumpWriteSucceeded = true,
            };

            var evt = PendingCrashReporter.BuildEvent(Config(), record);

            Assert.Equal("previous_crash_detected", evt.EventType);
            Assert.Equal(EventSeverity.Warning, evt.Severity);
            Assert.Equal("PendingCrashReporter", evt.Source);
            Assert.Equal(EnrollmentPhase.Unknown, evt.Phase);
            Assert.True(evt.ImmediateUpload);
            Assert.Contains("System.NullReferenceException", evt.Message);
            Assert.Contains("dump captured", evt.Message);

            Assert.Equal("previous-session-id", evt.Data["previousSessionId"]);
            Assert.Equal("previous-tenant", evt.Data["previousTenantId"]);
            Assert.Equal("2.0.999", evt.Data["previousAgentVersion"]);
            Assert.Equal("AppDomain.UnhandledException", evt.Data["trigger"]);
            Assert.Equal("System.NullReferenceException", evt.Data["exceptionType"]);
            Assert.Equal("20260528_101530_abc.dmp", evt.Data["dumpFilePath"]);
            Assert.Equal((long)12345, evt.Data["dumpFileSizeBytes"]);
            Assert.Equal(true, evt.Data["dumpWriteSucceeded"]);
        }

        [Fact]
        public void BuildEvent_handles_missing_dump_gracefully()
        {
            var record = new CrashRecord
            {
                CrashedAt = DateTime.UtcNow,
                Trigger = "TaskScheduler.UnobservedTaskException",
                ExceptionType = "System.AggregateException",
                ExceptionMessage = "outer",
                DumpWriteSucceeded = false,
            };

            var evt = PendingCrashReporter.BuildEvent(Config(), record);

            Assert.Equal(EventSeverity.Warning, evt.Severity);
            Assert.Contains("no dump", evt.Message);
            Assert.Equal("(no dump)", evt.Data["dumpFilePath"]);
            Assert.Equal(false, evt.Data["dumpWriteSucceeded"]);
        }

        [Fact]
        public void BuildEvent_handles_null_optional_fields()
        {
            var record = new CrashRecord
            {
                CrashedAt = DateTime.UtcNow,
                Trigger = "AppDomain.UnhandledException",
                ExceptionType = null,
                ExceptionMessage = null,
                StackTrace = null,
                SessionId = null,
                TenantId = null,
                AgentVersion = null,
            };

            var evt = PendingCrashReporter.BuildEvent(Config(), record);

            Assert.Equal("unknown", evt.Data["previousSessionId"]);
            Assert.Equal("unknown", evt.Data["previousTenantId"]);
            Assert.Equal("unknown", evt.Data["previousAgentVersion"]);
            Assert.Equal("unknown", evt.Data["exceptionType"]);
            Assert.Equal("", evt.Data["exceptionMessage"]);
            Assert.Equal("", evt.Data["stackTrace"]);
        }

        [Fact]
        public void CrashRecord_roundtrips_through_newtonsoft()
        {
            var record = new CrashRecord
            {
                CrashedAt = new DateTime(2026, 5, 28, 9, 0, 0, DateTimeKind.Utc),
                SessionId = "sid",
                Trigger = "AppDomain.UnhandledException",
                ExceptionType = "X",
                ExceptionMessage = "m",
                DumpWriteSucceeded = true,
            };

            var json = JsonConvert.SerializeObject(record);
            var back = JsonConvert.DeserializeObject<CrashRecord>(json);

            Assert.NotNull(back);
            Assert.Equal(record.SessionId, back!.SessionId);
            Assert.Equal(record.Trigger, back.Trigger);
            Assert.Equal(record.ExceptionType, back.ExceptionType);
            Assert.True(back.DumpWriteSucceeded);
        }

        // ---------------------------------------------------------------- Retention

        [Fact]
        public void ApplyRetention_deletes_files_older_than_max_age()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "PCR-Retention-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try
            {
                var oldFile = Path.Combine(tmp, "old.dmp");
                File.WriteAllText(oldFile, "x");
                File.SetCreationTimeUtc(oldFile, DateTime.UtcNow - TimeSpan.FromDays(30));

                var newFile = Path.Combine(tmp, "new.dmp");
                File.WriteAllText(newFile, "y");

                CrashDumpCapture.ApplyRetention(tmp);

                Assert.False(File.Exists(oldFile));
                Assert.True(File.Exists(newFile));
            }
            finally
            {
                try { Directory.Delete(tmp, recursive: true); } catch { }
            }
        }

        [Fact]
        public void ApplyRetention_keeps_only_max_retained_dumps()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "PCR-Retention-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try
            {
                // Create MaxRetainedCrashes + 3 dumps, each older than the next
                var baseTime = DateTime.UtcNow - TimeSpan.FromHours(8);
                for (int i = 0; i < CrashDumpCapture.MaxRetainedCrashes + 3; i++)
                {
                    var p = Path.Combine(tmp, $"dump-{i}.dmp");
                    File.WriteAllText(p, "x");
                    File.SetCreationTimeUtc(p, baseTime.AddMinutes(i));
                }

                CrashDumpCapture.ApplyRetention(tmp);

                var remaining = Directory.GetFiles(tmp, "*.dmp");
                Assert.Equal(CrashDumpCapture.MaxRetainedCrashes, remaining.Length);
            }
            finally
            {
                try { Directory.Delete(tmp, recursive: true); } catch { }
            }
        }
    }
}
