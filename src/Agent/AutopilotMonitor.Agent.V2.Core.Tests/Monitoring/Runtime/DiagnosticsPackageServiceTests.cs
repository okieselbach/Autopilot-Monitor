#nullable enable
using System.IO;
using System.IO.Compression;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Runtime
{
    /// <summary>
    /// PR1-B: diagnostics archive must include AgentState/, AgentSpool/, and top-level
    /// completion markers (whiteglove.complete, clean-exit) — V1 sessions had only AgentLogs/
    /// and ImeLogs/, which left forensics blind to decision-engine state and pending uploads.
    /// </summary>
    public sealed class DiagnosticsPackageServiceTests
    {
        private static AgentConfiguration Cfg(string sessionId = "S1") => new AgentConfiguration
        {
            SessionId = sessionId,
            TenantId = "T1",
            ApiBaseUrl = "http://localhost",
        };

        private sealed class Rig : System.IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public TempDirectory LogsTmp { get; } = new TempDirectory();
            public TempDirectory ImeTmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }

            public string DataFolder => Tmp.Path;
            public string StateFolder { get; }
            public string SpoolFolder { get; }

            public Rig()
            {
                Logger = new AgentLogger(LogsTmp.Path);
                StateFolder = Path.Combine(Tmp.Path, "State");
                SpoolFolder = Path.Combine(Tmp.Path, "Spool");
                Directory.CreateDirectory(StateFolder);
                Directory.CreateDirectory(SpoolFolder);
            }

            public DiagnosticsPackageService Build()
            {
                // BackendApiClient is required by the public ctor but BuildArchiveBytes
                // never touches it — pass a real instance configured against an unreachable
                // host, since BuildArchiveBytes returns before any HTTP traffic happens.
                var apiClient = new BackendApiClient("http://localhost", Cfg(), Logger);
                return new DiagnosticsPackageService(
                    Cfg(),
                    Logger,
                    apiClient,
                    agentLogFolderOverride: LogsTmp.Path,
                    imeLogFolderOverride: ImeTmp.Path,
                    agentStateFolderOverride: StateFolder,
                    agentSpoolFolderOverride: SpoolFolder,
                    agentDataFolderOverride: DataFolder);
            }

            public void Dispose()
            {
                Tmp.Dispose();
                LogsTmp.Dispose();
                ImeTmp.Dispose();
            }
        }

        [Fact]
        public void BuildArchiveBytes_includes_state_files_under_AgentState_prefix()
        {
            using var rig = new Rig();
            File.WriteAllText(Path.Combine(rig.StateFolder, "snapshot.json"), "{\"stage\":\"Completed\"}");
            File.WriteAllText(Path.Combine(rig.StateFolder, "journal.jsonl"), "{\"ord\":1}\n");
            File.WriteAllText(Path.Combine(rig.StateFolder, "signal-log.jsonl"), "{\"ord\":1}\n");
            File.WriteAllText(Path.Combine(rig.StateFolder, "ime-tracker-state.json"), "{}");
            File.WriteAllText(Path.Combine(rig.StateFolder, "enrollment-complete.marker"), "");
            File.WriteAllText(Path.Combine(rig.StateFolder, "final-status.json"), "{\"outcome\":\"Succeeded\"}");

            var bytes = rig.Build().BuildArchiveBytes(enrollmentSucceeded: true);

            var entries = ZipEntryNames(bytes);
            Assert.Contains("AgentState/snapshot.json", entries);
            Assert.Contains("AgentState/journal.jsonl", entries);
            Assert.Contains("AgentState/signal-log.jsonl", entries);
            Assert.Contains("AgentState/ime-tracker-state.json", entries);
            Assert.Contains("AgentState/enrollment-complete.marker", entries);
            Assert.Contains("AgentState/final-status.json", entries);
        }

        [Fact]
        public void BuildArchiveBytes_includes_spool_files_under_AgentSpool_prefix()
        {
            using var rig = new Rig();
            File.WriteAllText(Path.Combine(rig.SpoolFolder, "spool.jsonl"), "{\"itemId\":\"a\"}\n");
            File.WriteAllText(Path.Combine(rig.SpoolFolder, "upload-cursor.json"), "{\"lastItemId\":\"a\"}");

            var bytes = rig.Build().BuildArchiveBytes(enrollmentSucceeded: true);

            var entries = ZipEntryNames(bytes);
            Assert.Contains("AgentSpool/spool.jsonl", entries);
            Assert.Contains("AgentSpool/upload-cursor.json", entries);
        }

        [Fact]
        public void BuildArchiveBytes_includes_top_level_markers_under_AgentMarkers_prefix()
        {
            using var rig = new Rig();
            // Top-level markers in the data folder (NOT under State/).
            File.WriteAllText(Path.Combine(rig.DataFolder, "whiteglove.complete"), "");
            File.WriteAllText(Path.Combine(rig.DataFolder, "agent_clean_exit.marker"), "");

            var bytes = rig.Build().BuildArchiveBytes(enrollmentSucceeded: true);

            var entries = ZipEntryNames(bytes);
            Assert.Contains("AgentMarkers/whiteglove.complete", entries);
            Assert.Contains("AgentMarkers/agent_clean_exit.marker", entries);
        }

        [Fact]
        public void BuildArchiveBytes_excludes_top_level_session_id_and_bootstrap_config()
        {
            // session.id / bootstrap.json / await-enrollment.json live in the same data folder
            // but are NOT forensic state — keep them out of the archive.
            using var rig = new Rig();
            File.WriteAllText(Path.Combine(rig.DataFolder, "session.id"), "S1");
            File.WriteAllText(Path.Combine(rig.DataFolder, "bootstrap.json"), "{}");

            var bytes = rig.Build().BuildArchiveBytes(enrollmentSucceeded: true);

            var entries = ZipEntryNames(bytes);
            Assert.DoesNotContain(entries, e => e.EndsWith("session.id"));
            Assert.DoesNotContain(entries, e => e.EndsWith("bootstrap.json"));
        }

        [Fact]
        public void BuildArchiveBytes_includes_state_subfolders_when_quarantine_present()
        {
            using var rig = new Rig();
            var quarantine = Path.Combine(rig.StateFolder, ".quarantine");
            Directory.CreateDirectory(quarantine);
            File.WriteAllText(Path.Combine(quarantine, "corrupt.jsonl"), "garbage");

            var bytes = rig.Build().BuildArchiveBytes(enrollmentSucceeded: false);

            var entries = ZipEntryNames(bytes);
            Assert.Contains(entries, e => e.StartsWith("AgentState/.quarantine/") && e.EndsWith("corrupt.jsonl"));
        }

        [Fact]
        public void BuildArchiveBytes_excludes_spool_subfolders()
        {
            // Spool may grow auxiliary subfolders later — current archive policy is to keep
            // the spool section flat (only what is pending upload right now).
            using var rig = new Rig();
            var sub = Path.Combine(rig.SpoolFolder, "archive");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "old.jsonl"), "{}");

            var bytes = rig.Build().BuildArchiveBytes(enrollmentSucceeded: false);

            var entries = ZipEntryNames(bytes);
            Assert.DoesNotContain(entries, e => e.StartsWith("AgentSpool/archive/"));
        }

        [Fact]
        public void BuildArchiveBytes_handles_missing_state_folder_gracefully()
        {
            using var rig = new Rig();
            Directory.Delete(rig.StateFolder, recursive: true);
            Directory.Delete(rig.SpoolFolder, recursive: true);

            // Should not throw; archive simply has no AgentState/AgentSpool entries.
            var bytes = rig.Build().BuildArchiveBytes(enrollmentSucceeded: true);
            Assert.NotEmpty(bytes);

            var entries = ZipEntryNames(bytes);
            Assert.Contains("sessioninfo.txt", entries);
            Assert.DoesNotContain(entries, e => e.StartsWith("AgentState/"));
            Assert.DoesNotContain(entries, e => e.StartsWith("AgentSpool/"));
        }

        private static string[] ZipEntryNames(byte[] zipBytes)
        {
            using var ms = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
            return archive.Entries.Select(e => e.FullName).ToArray();
        }
    }
}
