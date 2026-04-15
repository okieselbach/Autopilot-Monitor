using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion;
using AutopilotMonitor.Agent.Core.Monitoring.Collectors;

namespace AutopilotMonitor.Agent
{
    partial class Program
    {
        // Same patterns ImeLogTracker uses for file discovery
        private static readonly string[] ImeLogFilePatterns = new[]
        {
            "IntuneManagementExtension.log",
            "_IntuneManagementExtension.log",
            "IntuneManagementExtension-????????-??????.log",
            "AppWorkload.log",
            "AppWorkload-????????-??????.log",
            "AgentExecutor.log",
            "AgentExecutor-????????-??????.log",
            "HealthScripts.log",
            "HealthScripts-????????-??????.log"
        };

        private const string GuidPattern = @"(?<id>[a-z0-9]{8}-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{12})";
        private const string PatternsGitHubUrl = "https://raw.githubusercontent.com/okieselbach/Autopilot-Monitor/refs/heads/main/rules/dist/ime-log-patterns.json";

        /// <summary>
        /// Standalone IME log pattern matching mode. Parses IME log files from the given path
        /// and writes all pattern matches to ime_pattern_matching.log.
        /// </summary>
        static void RunImeMatchingMode(string[] args)
        {
            Console.WriteLine("Autopilot Monitor Agent — IME Pattern Matching");
            Console.WriteLine("===============================================");
            Console.WriteLine();

            // Parse the path argument
            var pathIndex = Array.IndexOf(args, "--run-ime-matching");
            if (pathIndex < 0 || pathIndex + 1 >= args.Length)
            {
                Console.WriteLine("ERROR: --run-ime-matching requires a path argument (file or directory).");
                Console.WriteLine("Usage: AutopilotMonitor.Agent.exe --run-ime-matching <path> [--patterns <file>]");
                return;
            }

            var inputPath = args[pathIndex + 1];

            // Optional: local patterns file override
            string patternsFile = null;
            var patternsIndex = Array.IndexOf(args, "--patterns");
            if (patternsIndex >= 0 && patternsIndex + 1 < args.Length)
                patternsFile = args[patternsIndex + 1];

            // Determine input files
            var logFiles = new List<string>();
            string outputDir;

            if (File.Exists(inputPath))
            {
                // Single file mode
                logFiles.Add(Path.GetFullPath(inputPath));
                outputDir = Path.GetDirectoryName(Path.GetFullPath(inputPath));
                Console.WriteLine($"Mode:       Single file");
                Console.WriteLine($"Input:      {Path.GetFullPath(inputPath)}");
            }
            else if (Directory.Exists(inputPath))
            {
                // Directory mode: find all IME log files
                var fullDir = Path.GetFullPath(inputPath);
                foreach (var pattern in ImeLogFilePatterns)
                {
                    try
                    {
                        logFiles.AddRange(Directory.GetFiles(fullDir, pattern));
                    }
                    catch (DirectoryNotFoundException) { }
                }

                logFiles.Sort(StringComparer.OrdinalIgnoreCase);
                outputDir = fullDir;

                Console.WriteLine($"Mode:       Directory scan");
                Console.WriteLine($"Input:      {fullDir}");
                Console.WriteLine($"Log files:  {logFiles.Count}");

                if (logFiles.Count == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("No IME log files found in the specified directory.");
                    Console.WriteLine("Expected files: IntuneManagementExtension*.log, AppWorkload*.log, AgentExecutor*.log, HealthScripts*.log");
                    return;
                }
            }
            else
            {
                Console.WriteLine($"ERROR: Path not found: {inputPath}");
                return;
            }

            // Load patterns
            List<ImeLogPattern> patterns;
            if (!string.IsNullOrEmpty(patternsFile))
            {
                Console.WriteLine($"Patterns:   {patternsFile} (local file)");
                patterns = LoadPatternsFromFile(patternsFile);
            }
            else
            {
                Console.WriteLine($"Patterns:   GitHub (latest)");
                patterns = LoadPatternsFromGitHub();
            }

            if (patterns == null || patterns.Count == 0)
            {
                Console.WriteLine("ERROR: No patterns loaded. Cannot proceed.");
                return;
            }

            Console.WriteLine($"Loaded:     {patterns.Count} patterns ({patterns.Count(p => p.Enabled)} enabled)");

            // Compile patterns
            var compiledPatterns = CompileMatchingPatterns(patterns);
            Console.WriteLine($"Compiled:   {compiledPatterns.Count} patterns");

            // Output file
            var outputPath = Path.Combine(outputDir, "ime_pattern_matching.log");
            Console.WriteLine($"Output:     {outputPath}");
            Console.WriteLine();

            // Clear existing output
            if (File.Exists(outputPath))
                File.Delete(outputPath);

            // Scan all files
            int totalLines = 0;
            int totalMatches = 0;
            var matchCountByPattern = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var filePath in logFiles)
            {
                var fileName = Path.GetFileName(filePath);
                Console.Write($"  Scanning {fileName}...");

                int fileLines = 0;
                int fileMatches = 0;

                try
                {
                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            fileLines++;

                            // Parse CMTrace format to get message content
                            CmTraceLogEntry entry;
                            string messageToMatch;
                            if (CmTraceLogParser.TryParseLine(line, out entry))
                            {
                                messageToMatch = entry.Message;
                            }
                            else
                            {
                                messageToMatch = line;
                            }

                            if (string.IsNullOrEmpty(messageToMatch)) continue;

                            // Match against all compiled patterns
                            foreach (var pattern in compiledPatterns)
                            {
                                try
                                {
                                    var match = pattern.Regex.Match(messageToMatch);
                                    if (match.Success)
                                    {
                                        fileMatches++;
                                        var logEntry = $"[{fileName}] [{pattern.PatternId}] {line}";
                                        File.AppendAllText(outputPath, logEntry + Environment.NewLine);

                                        if (!matchCountByPattern.ContainsKey(pattern.PatternId))
                                            matchCountByPattern[pattern.PatternId] = 0;
                                        matchCountByPattern[pattern.PatternId]++;
                                    }
                                }
                                catch (RegexMatchTimeoutException) { }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" ERROR: {ex.Message}");
                    continue;
                }

                totalLines += fileLines;
                totalMatches += fileMatches;
                Console.WriteLine($" {fileLines} lines, {fileMatches} matches");
            }

            // Summary
            Console.WriteLine();
            Console.WriteLine("=== Summary ===");
            Console.WriteLine($"Files scanned:  {logFiles.Count}");
            Console.WriteLine($"Lines parsed:   {totalLines}");
            Console.WriteLine($"Total matches:  {totalMatches}");
            Console.WriteLine($"Output:         {outputPath}");

            if (matchCountByPattern.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Matches by pattern:");
                foreach (var kvp in matchCountByPattern.OrderByDescending(x => x.Value))
                {
                    Console.WriteLine($"  {kvp.Key,-40} {kvp.Value,5}");
                }
            }
        }

        private static List<ImeLogPattern> LoadPatternsFromGitHub()
        {
            try
            {
                Console.Write("  Downloading patterns from GitHub...");
                using (var handler = new HttpClientHandler())
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
                {
                    var json = client.GetStringAsync(PatternsGitHubUrl).GetAwaiter().GetResult();
                    var wrapper = JsonConvert.DeserializeObject<PatternsFileWrapper>(json);
                    var patterns = wrapper?.Rules ?? new List<ImeLogPattern>();
                    Console.WriteLine(" OK");
                    return patterns;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" FAILED: {ex.Message}");

                // Fallback: try cached remote-config.json
                try
                {
                    var cachePath = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\Config\remote-config.json");
                    if (File.Exists(cachePath))
                    {
                        Console.Write("  Falling back to cached remote-config.json...");
                        var json = File.ReadAllText(cachePath);
                        var config = JsonConvert.DeserializeObject<CachedConfigWrapper>(json);
                        if (config?.ImeLogPatterns != null && config.ImeLogPatterns.Count > 0)
                        {
                            Console.WriteLine(" OK");
                            return config.ImeLogPatterns;
                        }
                    }
                }
                catch { }

                Console.WriteLine("  No fallback available.");
                return null;
            }
        }

        private static List<ImeLogPattern> LoadPatternsFromFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    Console.WriteLine($"ERROR: Patterns file not found: {filePath}");
                    return null;
                }

                var json = File.ReadAllText(filePath);
                var wrapper = JsonConvert.DeserializeObject<PatternsFileWrapper>(json);
                return wrapper?.Rules ?? new List<ImeLogPattern>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to load patterns file: {ex.Message}");
                return null;
            }
        }

        private static List<MatchPattern> CompileMatchingPatterns(List<ImeLogPattern> patterns)
        {
            var compiled = new List<MatchPattern>();

            foreach (var pattern in patterns.Where(p => p.Enabled))
            {
                try
                {
                    var regexStr = pattern.Pattern.Replace("{GUID}", GuidPattern);
                    var regex = new Regex(regexStr, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
                    compiled.Add(new MatchPattern
                    {
                        PatternId = pattern.PatternId,
                        Regex = regex
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  WARNING: Failed to compile pattern {pattern.PatternId}: {ex.Message}");
                }
            }

            return compiled;
        }

        private class MatchPattern
        {
            public string PatternId { get; set; }
            public Regex Regex { get; set; }
        }

        private class PatternsFileWrapper
        {
            [JsonProperty("rules")]
            public List<ImeLogPattern> Rules { get; set; } = new List<ImeLogPattern>();
        }

        private class CachedConfigWrapper
        {
            [JsonProperty("imeLogPatterns")]
            public List<ImeLogPattern> ImeLogPatterns { get; set; }
        }
    }
}
