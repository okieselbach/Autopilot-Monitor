#nullable enable
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
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
                // never touches it — construct with a throwaway HttpClient. Tests only
                // exercise BuildArchiveBytes, which returns before any HTTP traffic.
                var apiClient = new BackendApiClient(
                    httpClient: new System.Net.Http.HttpClient(),
                    baseUrl: "http://localhost",
                    manufacturer: string.Empty,
                    model: string.Empty,
                    serialNumber: string.Empty,
                    useBootstrapTokenAuth: false,
                    bootstrapToken: null,
                    agentVersion: "0.0.0",
                    logger: Logger);
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

        private static string ReadEntry(byte[] zipBytes, string entryName)
        {
            using var ms = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = archive.Entries.First(e => e.FullName == entryName);
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        // Isolated rig for budget/cap tests: keeps the agent logger output OUT of the
        // archive scope so cap assertions are not contaminated by logger noise.
        // Test files are written to ContentDir (mapped to AgentLogs in the archive);
        // every other source folder override points at an empty dir.
        private sealed class BudgetRig : System.IDisposable
        {
            public TempDirectory LoggerDir { get; } = new TempDirectory();   // logger writes here, not enumerated
            public TempDirectory ContentDir { get; } = new TempDirectory();  // test writes go here → AgentLogs
            public TempDirectory EmptyDir { get; } = new TempDirectory();    // unused source folders
            public AgentLogger Logger { get; }

            public BudgetRig()
            {
                Logger = new AgentLogger(LoggerDir.Path);
            }

            public DiagnosticsPackageService Build(DiagnosticsBudget? budget = null)
            {
                var apiClient = new BackendApiClient(
                    httpClient: new System.Net.Http.HttpClient(),
                    baseUrl: "http://localhost",
                    manufacturer: string.Empty,
                    model: string.Empty,
                    serialNumber: string.Empty,
                    useBootstrapTokenAuth: false,
                    bootstrapToken: null,
                    agentVersion: "0.0.0",
                    logger: Logger);

                var svc = new DiagnosticsPackageService(
                    Cfg(),
                    Logger,
                    apiClient,
                    agentLogFolderOverride: ContentDir.Path,
                    imeLogFolderOverride: EmptyDir.Path,
                    agentStateFolderOverride: EmptyDir.Path,
                    agentSpoolFolderOverride: EmptyDir.Path,
                    agentDataFolderOverride: EmptyDir.Path);

                if (budget != null) svc.Budget = budget;
                return svc;
            }

            public void Dispose()
            {
                LoggerDir.Dispose();
                ContentDir.Dispose();
                EmptyDir.Dispose();
            }
        }

        [Fact]
        public void BuildArchiveBytes_skips_file_exceeding_per_file_cap()
        {
            using var rig = new BudgetRig();
            File.WriteAllBytes(Path.Combine(rig.ContentDir.Path, "huge.log"), new byte[5000]);
            File.WriteAllBytes(Path.Combine(rig.ContentDir.Path, "small.log"), new byte[500]);

            var svc = rig.Build(new DiagnosticsBudget
            {
                MaxSingleFileBytes = 1024,
                MaxTotalUncompressedBytes = 1024L * 1024 * 1024,
                MaxFileCount = 1000,
            });

            var bytes = svc.BuildArchiveBytes(enrollmentSucceeded: true);
            var entries = ZipEntryNames(bytes);

            Assert.DoesNotContain("AgentLogs/huge.log", entries);
            Assert.Contains("AgentLogs/small.log", entries);
            Assert.Contains("_TRUNCATED.txt", entries);

            var truncated = ReadEntry(bytes, "_TRUNCATED.txt");
            Assert.Contains("huge.log", truncated);
            Assert.Contains("size", truncated);
        }

        [Fact]
        public void BuildArchiveBytes_stops_at_total_cap()
        {
            using var rig = new BudgetRig();
            for (int i = 0; i < 10; i++)
                File.WriteAllBytes(Path.Combine(rig.ContentDir.Path, $"f{i:D2}.log"), new byte[1024]);

            var svc = rig.Build(new DiagnosticsBudget
            {
                MaxSingleFileBytes = 100L * 1024 * 1024,
                MaxTotalUncompressedBytes = 4096,
                MaxFileCount = 1000,
            });

            var bytes = svc.BuildArchiveBytes(enrollmentSucceeded: true);
            var entries = ZipEntryNames(bytes);

            var fileEntries = entries.Where(e => e.StartsWith("AgentLogs/")).ToArray();
            // 4096 / 1024 = 4 max files fit; subsequent files are skipped.
            Assert.Equal(4, fileEntries.Length);
            Assert.Contains("_TRUNCATED.txt", entries);
            var truncated = ReadEntry(bytes, "_TRUNCATED.txt");
            Assert.Contains("total", truncated);
        }

        [Fact]
        public void BuildArchiveBytes_stops_at_file_count_cap()
        {
            using var rig = new BudgetRig();
            for (int i = 0; i < 10; i++)
                File.WriteAllBytes(Path.Combine(rig.ContentDir.Path, $"f{i:D2}.log"), new byte[100]);

            var svc = rig.Build(new DiagnosticsBudget
            {
                MaxSingleFileBytes = 100L * 1024 * 1024,
                MaxTotalUncompressedBytes = 100L * 1024 * 1024,
                MaxFileCount = 3,
            });

            var bytes = svc.BuildArchiveBytes(enrollmentSucceeded: true);
            var entries = ZipEntryNames(bytes);

            var fileEntries = entries.Where(e => e.StartsWith("AgentLogs/")).ToArray();
            Assert.Equal(3, fileEntries.Length);
            Assert.Contains("_TRUNCATED.txt", entries);
            var truncated = ReadEntry(bytes, "_TRUNCATED.txt");
            Assert.Contains("count", truncated);
        }

        [Fact]
        public void BuildArchiveBytes_omits_truncated_marker_when_no_skips()
        {
            using var rig = new BudgetRig();
            File.WriteAllBytes(Path.Combine(rig.ContentDir.Path, "tiny.log"), new byte[100]);

            var svc = rig.Build();   // default budget: 100 MB / 500 MB / 5000

            var bytes = svc.BuildArchiveBytes(enrollmentSucceeded: true);
            var entries = ZipEntryNames(bytes);

            Assert.Contains("AgentLogs/tiny.log", entries);
            Assert.DoesNotContain("_TRUNCATED.txt", entries);
        }

        [Fact]
        public void IsReparsePoint_returns_true_for_reparse_attribute()
        {
            Assert.True(DiagnosticsPackageService.IsReparsePoint(FileAttributes.ReparsePoint));
            Assert.True(DiagnosticsPackageService.IsReparsePoint(FileAttributes.ReparsePoint | FileAttributes.Directory));
            Assert.True(DiagnosticsPackageService.IsReparsePoint(FileAttributes.ReparsePoint | FileAttributes.Hidden));
        }

        [Fact]
        public void IsReparsePoint_returns_false_for_normal_files()
        {
            Assert.False(DiagnosticsPackageService.IsReparsePoint(FileAttributes.Normal));
            Assert.False(DiagnosticsPackageService.IsReparsePoint(FileAttributes.Directory));
            Assert.False(DiagnosticsPackageService.IsReparsePoint(FileAttributes.ReadOnly | FileAttributes.Hidden));
            Assert.False(DiagnosticsPackageService.IsReparsePoint(FileAttributes.Archive));
        }
    }
}
