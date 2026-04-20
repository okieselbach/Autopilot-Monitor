using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers
{
    /// <summary>
    /// Collects installed software inventory from the Windows registry, normalizes
    /// vendor/product/version strings, and emits structured events for vulnerability correlation.
    ///
    /// Lifecycle:
    ///   AnalyzeAtStartup()  — baseline inventory snapshot (before enrollment installs)
    ///   AnalyzeAtShutdown() — final inventory + delta (what was installed during enrollment)
    ///
    /// The actual CVE/KEV correlation happens server-side after the events are ingested.
    /// </summary>
    public class SoftwareInventoryAnalyzer : IAgentAnalyzer
    {
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _emitEvent;
        private readonly AgentLogger _logger;

        // Captured at startup for delta detection at shutdown
        private List<SoftwareEntry> _startupInventory;

        public string Name => "SoftwareInventoryAnalyzer";

        // Maximum items per event chunk to stay within Table Storage property size limits (~64 KB)
        private const int ChunkSize = 75;

        // -----------------------------------------------------------------------
        // Registry paths for installed software
        // -----------------------------------------------------------------------

        private static readonly string[] UninstallRegistryPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        private const string UninstallRegistryPathHkcu =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        private const string ProfileListPath =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

        private const string AppxAllUserStorePath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications";

        // -----------------------------------------------------------------------
        // Publisher normalization map
        // -----------------------------------------------------------------------

        private static readonly Dictionary<string, string> PublisherMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "microsoft corporation", "microsoft" },
            { "microsoft", "microsoft" },
            { "google llc", "google" },
            { "google inc", "google" },
            { "google inc.", "google" },
            { "adobe inc.", "adobe" },
            { "adobe systems incorporated", "adobe" },
            { "adobe systems, incorporated", "adobe" },
            { "mozilla corporation", "mozilla" },
            { "mozilla", "mozilla" },
            { "oracle corporation", "oracle" },
            { "oracle", "oracle" },
            { "apple inc.", "apple" },
            { "apple inc", "apple" },
            { "dell inc.", "dell" },
            { "dell inc", "dell" },
            { "dell technologies", "dell" },
            { "hewlett-packard", "hp" },
            { "hewlett packard enterprise", "hp" },
            { "hp inc.", "hp" },
            { "hp inc", "hp" },
            { "lenovo", "lenovo" },
            { "lenovo group limited", "lenovo" },
            { "cisco systems, inc.", "cisco" },
            { "cisco systems", "cisco" },
            { "vmware, inc.", "vmware" },
            { "vmware", "vmware" },
            { "citrix systems, inc.", "citrix" },
            { "citrix", "citrix" },
            { "zoom video communications, inc.", "zoom" },
            { "slack technologies, inc.", "slack" },
            { "slack technologies, llc", "slack" },
            { "7-zip", "7-zip" },
            { "igor pavlov", "7-zip" },
            { "the 7-zip developers", "7-zip" },
            { "notepad++ team", "notepad++" },
            { "don ho", "notepad++" },
            { "putty", "putty" },
            { "simon tatham", "putty" },
            { "winscp", "winscp" },
            { "martin prikryl", "winscp" },
            { "teamviewer", "teamviewer" },
            { "teamviewer germany gmbh", "teamviewer" },
            { "fortinet technologies (canada) inc.", "fortinet" },
            { "fortinet inc", "fortinet" },
            { "palo alto networks", "paloaltonetworks" },
            { "crowdstrike, inc.", "crowdstrike" },
            { "crowdstrike inc.", "crowdstrike" },
            { "git", "git" },
            { "the git development community", "git" },
            { "python software foundation", "python" },
            { "node.js", "nodejs" },
            { "node.js foundation", "nodejs" },
            { "openjs foundation", "nodejs" },

            // Endpoint security & AV
            { "sophos", "sophos" },
            { "sophos limited", "sophos" },
            { "eset, spol. s r.o.", "eset" },
            { "eset", "eset" },
            { "malwarebytes", "malwarebytes" },
            { "kaspersky", "kaspersky" },
            { "kaspersky lab", "kaspersky" },
            { "trend micro", "trendmicro" },
            { "trend micro inc.", "trendmicro" },
            { "mcafee, inc.", "mcafee" },
            { "mcafee, llc", "mcafee" },
            { "symantec corporation", "symantec" },
            { "broadcom inc.", "broadcom" },
            { "carbon black, inc.", "carbonblack" },
            { "sentinelone", "sentinelone" },

            // VPN & network security
            { "zscaler, inc.", "zscaler" },
            { "zscaler", "zscaler" },
            { "netskope", "netskope" },
            { "netskope, inc.", "netskope" },
            { "pulse secure, llc", "pulsesecure" },
            { "juniper networks", "juniper" },
            { "openvpn inc.", "openvpn" },
            { "openvpn technologies", "openvpn" },
            { "wireguard", "wireguard" },

            // Device management & enterprise
            { "glueckkanja ag", "glueckkanja" },
            { "glueckkanja-gab ag", "glueckkanja" },
            { "beyond trust", "beyondtrust" },
            { "beyondtrust software", "beyondtrust" },
            { "beyondtrust corporation", "beyondtrust" },
            { "ivanti", "ivanti" },
            { "ivanti, inc.", "ivanti" },
            { "1e", "1e" },
            { "tanium", "tanium" },
            { "tanium inc.", "tanium" },
            { "bigfix", "bigfix" },
            { "hcl technologies limited", "hcl" },
            { "sap se", "sap" },
            { "sap", "sap" },
            { "servicenow", "servicenow" },

            // Communication & collaboration
            { "webex communications", "webex" },
            { "discord inc.", "discord" },
            { "signal messenger, llc", "signal" },
            { "signal", "signal" },

            // Multimedia & documents
            { "videolan", "videolan" },
            { "videolan team", "videolan" },
            { "the document foundation", "libreoffice" },
            { "foxit software inc.", "foxit" },
            { "foxit", "foxit" },
            { "tracker software products", "pdfxchange" },
            { "audacity team", "audacity" },
            { "gimp", "gimp" },
            { "the gimp team", "gimp" },
            { "irfanview", "irfanview" },
            { "irfan skiljan", "irfanview" },

            // Remote access
            { "anydesk software gmbh", "anydesk" },
            { "anydesk", "anydesk" },
            { "rustdesk", "rustdesk" },
            { "realvnc", "realvnc" },
            { "realvnc ltd", "realvnc" },
            { "devolutions", "devolutions" },
            { "devolutions inc.", "devolutions" },
            { "splashtop", "splashtop" },
            { "splashtop inc.", "splashtop" },
            { "bomgar", "bomgar" },

            // Development tools
            { "jetbrains s.r.o.", "jetbrains" },
            { "jetbrains", "jetbrains" },
            { "postman, inc.", "postman" },
            { "postman", "postman" },
            { "docker inc.", "docker" },
            { "docker", "docker" },
            { "github, inc.", "github" },
            { "github", "github" },
            { "atlassian", "atlassian" },
            { "atlassian pty ltd", "atlassian" },
            { "hashicorp", "hashicorp" },
            { "hashicorp, inc.", "hashicorp" },
            { "sublime hq pty ltd", "sublimetext" },

            // Utilities
            { "filezilla project", "filezilla" },
            { "tim kosse", "filezilla" },
            { "keepass", "keepass" },
            { "dominik reichl", "keepass" },
            { "greenshot", "greenshot" },
            { "paint.net", "paintnet" },
            { "dotpdn llc", "paintnet" },
            { "win32diskimager", "win32diskimager" },
            { "angusj", "7-zip" },
            { "rarlab", "winrar" },
            { "alexander roshal", "winrar" },
            { "winrar", "winrar" },
            { "wireshark foundation", "wireshark" },
            { "the wireshark team", "wireshark" },
            { "nmap project", "nmap" },
            { "insecure.com", "nmap" },
            { "voidtools", "everything" },

            // Hardware vendors
            { "intel corporation", "intel" },
            { "intel", "intel" },
            { "nvidia corporation", "nvidia" },
            { "nvidia", "nvidia" },
            { "amd", "amd" },
            { "advanced micro devices, inc.", "amd" },
            { "realtek semiconductor", "realtek" },
            { "realtek", "realtek" },
            { "logitech", "logitech" },
            { "logitech inc.", "logitech" },

            // Database & data tools
            { "dbeaver corp", "dbeaver" },
            { "mysql ab", "mysql" },
            { "mariadb", "mariadb" },
            { "the postgresql global development group", "postgresql" },
            { "pgadmin development team", "pgadmin" },

            // Backup & imaging
            { "veeam software", "veeam" },
            { "veeam", "veeam" },
            { "acronis", "acronis" },
            { "acronis international gmbh", "acronis" },
            { "macrium software", "macrium" },
            { "corel corporation", "corel" },
        };

        // -----------------------------------------------------------------------
        // Publisher suffix stripping
        // -----------------------------------------------------------------------

        private static readonly string[] PublisherSuffixes = new[]
        {
            ", inc.", " inc.", " inc", ", llc", " llc", ", ltd", " ltd", " ltd.",
            " corporation", " corp.", " corp", " gmbh", " ag", " se",
            " co.", " co", " group", " limited"
        };

        // -----------------------------------------------------------------------
        // Exclude patterns for filtering noise
        // -----------------------------------------------------------------------

        private static readonly Regex KbUpdatePattern = new Regex(@"^KB\d{6,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] ExcludeContains = new[]
        {
            "Language Pack",
            "MUI Pack",
            "Spell Checking",
            "Proofing Tools",
            "Microsoft .NET Host",
            "Microsoft .NET Runtime",
            "Microsoft .NET AppHost",
            "Microsoft ASP.NET",
            "Microsoft Windows Desktop Runtime",
            "Additional Runtime",
            "Minimum Runtime",
            "Microsoft Update Health Tools",
            "Update for Windows",
            "Security Update for",
            "Hotfix for",
        };

        private static readonly string[] ExcludeStartsWith = new[]
        {
            "vs_",          // Visual Studio installer bootstrapper components
            "MSVC",         // MSVC redistributable sub-components (the parent is kept)
        };

        // -----------------------------------------------------------------------
        // AppX package filtering — strict whitelist for all publishers
        // Sandboxed AppX/MSIX apps rarely have CVEs and the sandbox limits
        // blast radius, so we only surface packages with known security or
        // enterprise relevance to keep the report clean.
        // -----------------------------------------------------------------------

        private static readonly HashSet<string> AppxWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Intune / device management
            "Microsoft.CompanyPortal",
            "Microsoft.ManagementApp",

            // Communication & productivity (CVE-relevant, enterprise-deployed)
            "Microsoft.Teams",
            "MSTeams",
            "MicrosoftTeams",
            "Microsoft.Office.Desktop",
            "Microsoft.OutlookForWindows",

            // Remote access (security surface)
            "MicrosoftCorporationII.WindowsApp",
            "MicrosoftCorporationII.WindowsSubsystemForLinux",

            // Developer tools (CVE-relevant)
            "Microsoft.WindowsTerminal",
            "Microsoft.VisualStudioCode",

            // Power Platform (enterprise)
            "Microsoft.PowerAutomateDesktop",
        };

        // Pattern: {Publisher.PackageName}_{Version}_{Arch}__{PublisherHash}
        private static readonly Regex AppxPackagePattern = new Regex(
            @"^(.+?)_(\d+(?:\.\d+)*)_([^_]*)__([a-z0-9]+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // -----------------------------------------------------------------------
        // Regex patterns for normalization
        // -----------------------------------------------------------------------

        private static readonly Regex ArchitecturePattern = new Regex(
            @"\s*[\(\-]\s*(?:x64|x86|64-bit|32-bit|amd64|arm64)\s*[\)]?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TrailingVersionPattern = new Regex(
            @"\s+v?\d+(?:\.\d+)*\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex VersionExtractPattern = new Regex(
            @"(\d+(?:\.\d+){0,3})",
            RegexOptions.Compiled);

        public SoftwareInventoryAnalyzer(
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> emitEvent,
            AgentLogger logger)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId  = tenantId  ?? throw new ArgumentNullException(nameof(tenantId));
            _emitEvent = emitEvent ?? throw new ArgumentNullException(nameof(emitEvent));
            _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
        }

        public void AnalyzeAtStartup()
        {
            _logger.Info($"{Name}: Running startup analysis (baseline inventory)");
            try
            {
                _startupInventory = CollectAndNormalize();
                EmitInventoryEvents("startup", EnrollmentPhase.Unknown, _startupInventory, null);
            }
            catch (Exception ex)
            {
                _logger.Error($"{Name}: Startup analysis failed", ex);
            }
        }

        public void AnalyzeAtShutdown()
        {
            AnalyzeAtShutdown(whiteGlovePart: null);
        }

        /// <summary>
        /// Shutdown analysis with optional WhiteGlove part tag.
        /// Phase is always Unknown — analyzer events are NOT phase-declaration events.
        /// Only explicit phase-transition events (esp_phase_changed, agent_started) may carry
        /// a non-Unknown phase. Context is conveyed via DataJson (triggered_at, whiteglove_part).
        /// </summary>
        public void AnalyzeAtShutdown(int? whiteGlovePart)
        {
            _logger.Info($"{Name}: Running shutdown analysis (delta detection, whiteGlovePart={whiteGlovePart?.ToString() ?? "none"})");
            try
            {
                var currentInventory = CollectAndNormalize();
                var newInstalls = ComputeDelta(_startupInventory ?? new List<SoftwareEntry>(), currentInventory);
                EmitInventoryEvents("shutdown", EnrollmentPhase.Unknown, currentInventory, newInstalls, whiteGlovePart);
            }
            catch (Exception ex)
            {
                _logger.Error($"{Name}: Shutdown analysis failed", ex);
            }
        }

        // -----------------------------------------------------------------------
        // Collection
        // -----------------------------------------------------------------------

        private List<SoftwareEntry> CollectAndNormalize()
        {
            var entries = new List<SoftwareEntry>();

            // HKLM 64-bit and WOW6432Node (32-bit)
            foreach (var path in UninstallRegistryPaths)
            {
                var source = path.Contains("WOW6432Node") ? "HKLM_32" : "HKLM_64";
                CollectFromKey(Registry.LocalMachine, path, source, entries);
            }

            // HKCU (may be empty during OOBE when running as SYSTEM)
            try
            {
                CollectFromKey(Registry.CurrentUser, UninstallRegistryPathHkcu, "HKCU", entries);
            }
            catch (Exception ex)
            {
                _logger.Debug($"{Name}: HKCU read skipped (expected during OOBE): {ex.Message}");
            }

            // HKU per-user Uninstall keys — catches per-user installs (VS Code user, Teams Classic, Spotify, etc.)
            CollectFromAllUserProfiles(entries);

            // AppX/MSIX packages — catches modern packaged apps (Company Portal, new Teams, etc.)
            CollectAppxPackages(entries);

            // Normalize all entries
            foreach (var entry in entries)
            {
                NormalizeEntry(entry);
            }

            _logger.Info($"{Name}: Collected {entries.Count} software entries");
            return entries;
        }

        private void CollectFromKey(RegistryKey rootKey, string subKeyPath, string source, List<SoftwareEntry> results)
        {
            try
            {
                using (var key = rootKey.OpenSubKey(subKeyPath, writable: false))
                {
                    if (key == null)
                    {
                        _logger.Debug($"{Name}: Registry key not found: {source}\\{subKeyPath}");
                        return;
                    }

                    var subKeyNames = key.GetSubKeyNames();
                    foreach (var subKeyName in subKeyNames)
                    {
                        try
                        {
                            using (var subKey = key.OpenSubKey(subKeyName, writable: false))
                            {
                                if (subKey == null)
                                    continue;

                                var displayName = subKey.GetValue("DisplayName")?.ToString();
                                var systemComponent = subKey.GetValue("SystemComponent");
                                var parentKeyName = subKey.GetValue("ParentKeyName")?.ToString();

                                if (ShouldExclude(displayName, systemComponent, parentKeyName))
                                    continue;

                                results.Add(new SoftwareEntry
                                {
                                    DisplayName = displayName,
                                    DisplayVersion = subKey.GetValue("DisplayVersion")?.ToString(),
                                    Publisher = subKey.GetValue("Publisher")?.ToString(),
                                    InstallDate = subKey.GetValue("InstallDate")?.ToString(),
                                    InstallLocation = subKey.GetValue("InstallLocation")?.ToString(),
                                    UninstallString = subKey.GetValue("UninstallString")?.ToString(),
                                    IsWindowsInstaller = Convert.ToInt32(subKey.GetValue("WindowsInstaller") ?? 0) == 1,
                                    RegistrySource = source
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug($"{Name}: Error reading subkey {subKeyName}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"{Name}: Failed to read {source}\\{subKeyPath}: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // HKU per-user profile enumeration
        // -----------------------------------------------------------------------

        private void CollectFromAllUserProfiles(List<SoftwareEntry> results)
        {
            try
            {
                using (var profileList = Registry.LocalMachine.OpenSubKey(ProfileListPath, writable: false))
                {
                    if (profileList == null)
                    {
                        _logger.Debug($"{Name}: ProfileList key not found");
                        return;
                    }

                    // Track SIDs we already read via HKCU to avoid duplicates
                    var currentUserSid = GetCurrentUserSid();

                    foreach (var sid in profileList.GetSubKeyNames())
                    {
                        // Only real user profiles (S-1-5-21-*), skip well-known SIDs
                        if (!sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Skip current user SID — already covered by HKCU read above
                        if (string.Equals(sid, currentUserSid, StringComparison.OrdinalIgnoreCase))
                            continue;

                        try
                        {
                            // HKU\<SID> is only available if the user's hive is loaded
                            var hkuPath = $@"{sid}\{UninstallRegistryPathHkcu}";
                            CollectFromKey(Registry.Users, hkuPath, $"HKU_{sid.Substring(sid.LastIndexOf('-') + 1)}", results);
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug($"{Name}: HKU read skipped for {sid}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"{Name}: Per-user profile enumeration failed: {ex.Message}");
            }
        }

        private static string GetCurrentUserSid()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    return identity.User?.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        // -----------------------------------------------------------------------
        // AppX/MSIX package enumeration
        // -----------------------------------------------------------------------

        private void CollectAppxPackages(List<SoftwareEntry> results)
        {
            try
            {
                using (var appxKey = Registry.LocalMachine.OpenSubKey(AppxAllUserStorePath, writable: false))
                {
                    if (appxKey == null)
                    {
                        _logger.Debug($"{Name}: AppX AllUserStore key not found");
                        return;
                    }

                    int appxCount = 0;

                    foreach (var subKeyName in appxKey.GetSubKeyNames())
                    {
                        try
                        {
                            if (ShouldExcludeAppx(subKeyName))
                                continue;

                            var parsed = ParseAppxPackageName(subKeyName);
                            if (parsed == null)
                                continue;

                            results.Add(parsed);
                            appxCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug($"{Name}: Error parsing AppX entry {subKeyName}: {ex.Message}");
                        }
                    }

                    _logger.Info($"{Name}: Collected {appxCount} AppX packages");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"{Name}: AppX enumeration failed: {ex.Message}");
            }
        }

        private static bool ShouldExcludeAppx(string packageFullName)
        {
            // Extract the package identity (everything before the first underscore)
            var underscoreIndex = packageFullName.IndexOf('_');
            var packageId = underscoreIndex > 0
                ? packageFullName.Substring(0, underscoreIndex)
                : packageFullName;

            // Strict whitelist — only explicitly listed packages pass through
            return !AppxWhitelist.Contains(packageId);
        }

        private static SoftwareEntry ParseAppxPackageName(string packageFullName)
        {
            // Format: {Publisher.PackageName}_{Version}_{Arch}__{PublisherHash}
            // Example: Microsoft.CompanyPortal_5.0.6155.0_x64__8wekyb3d8bbwe
            var match = AppxPackagePattern.Match(packageFullName);
            if (!match.Success)
                return null;

            var packageId = match.Groups[1].Value;   // e.g. "Microsoft.CompanyPortal"
            var version = match.Groups[2].Value;     // e.g. "5.0.6155.0"

            // Split publisher from product: "Microsoft.CompanyPortal" → ("Microsoft", "CompanyPortal")
            var dotIndex = packageId.IndexOf('.');
            string publisher;
            string productName;
            if (dotIndex > 0)
            {
                publisher = packageId.Substring(0, dotIndex);
                productName = packageId.Substring(dotIndex + 1);
            }
            else
            {
                publisher = packageId;
                productName = packageId;
            }

            // Make display name human-readable: "CompanyPortal" → "Company Portal"
            var displayName = SpacePascalCase(productName);

            return new SoftwareEntry
            {
                DisplayName = displayName,
                DisplayVersion = version,
                Publisher = publisher,
                RegistrySource = "AppX",
                IsWindowsInstaller = false
            };
        }

        private static string SpacePascalCase(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // Insert space before uppercase letters that follow lowercase letters
            // "CompanyPortal" → "Company Portal", but "MSTeams" → "MS Teams"
            var result = new System.Text.StringBuilder(input.Length + 4);
            result.Append(input[0]);
            for (int i = 1; i < input.Length; i++)
            {
                if (char.IsUpper(input[i]) && char.IsLower(input[i - 1]))
                    result.Append(' ');
                result.Append(input[i]);
            }
            return result.ToString();
        }

        // -----------------------------------------------------------------------
        // Filtering
        // -----------------------------------------------------------------------

        private static bool ShouldExclude(string displayName, object systemComponent, string parentKeyName)
        {
            // No display name = not a user-visible application
            if (string.IsNullOrWhiteSpace(displayName))
                return true;

            // System component flag
            if (systemComponent != null)
            {
                try
                {
                    if (Convert.ToInt32(systemComponent) == 1)
                        return true;
                }
                catch { /* non-integer value, ignore */ }
            }

            // Child component / update (has a parent)
            if (!string.IsNullOrEmpty(parentKeyName))
                return true;

            // KB updates (Windows Updates)
            if (KbUpdatePattern.IsMatch(displayName))
                return true;

            // Known noise patterns (contains)
            foreach (var pattern in ExcludeContains)
            {
                if (displayName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            // Known noise patterns (starts with)
            foreach (var pattern in ExcludeStartsWith)
            {
                if (displayName.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        // -----------------------------------------------------------------------
        // Normalization
        // -----------------------------------------------------------------------

        private void NormalizeEntry(SoftwareEntry entry)
        {
            entry.NormalizedPublisher = NormalizePublisher(entry.Publisher);
            entry.NormalizedName = NormalizeName(entry.DisplayName);
            entry.NormalizedVersion = NormalizeVersion(entry.DisplayVersion);
            AssessConfidence(entry);
        }

        private static string NormalizePublisher(string publisher)
        {
            if (string.IsNullOrWhiteSpace(publisher))
                return string.Empty;

            var trimmed = publisher.Trim();

            // Check known publisher map
            if (PublisherMap.TryGetValue(trimmed, out var mapped))
                return mapped;

            // Fallback: lowercase + strip common suffixes
            var lower = trimmed.ToLowerInvariant();
            foreach (var suffix in PublisherSuffixes)
            {
                if (lower.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    lower = lower.Substring(0, lower.Length - suffix.Length).TrimEnd();
                    break; // strip only one suffix
                }
            }

            return lower;
        }

        private static string NormalizeName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                return string.Empty;

            var name = displayName.Trim().ToLowerInvariant();

            // Strip architecture markers: (x64), (x86), (64-bit), - x64, etc.
            name = ArchitecturePattern.Replace(name, "");

            // Strip trailing version-like suffix: "chrome 134.0.6998.89" → "chrome"
            name = TrailingVersionPattern.Replace(name, "");

            return name.Trim();
        }

        private static string NormalizeVersion(string displayVersion)
        {
            if (string.IsNullOrWhiteSpace(displayVersion))
                return string.Empty;

            var match = VersionExtractPattern.Match(displayVersion.Trim());
            return match.Success ? match.Groups[1].Value : displayVersion.Trim();
        }

        private void AssessConfidence(SoftwareEntry entry)
        {
            bool publisherKnown = !string.IsNullOrEmpty(entry.Publisher) &&
                                  PublisherMap.ContainsKey(entry.Publisher.Trim());
            bool versionClean = !string.IsNullOrEmpty(entry.DisplayVersion) &&
                                VersionExtractPattern.IsMatch(entry.DisplayVersion);

            if (publisherKnown && versionClean)
                entry.NormalizationConfidence = "high";
            else if (publisherKnown || versionClean)
                entry.NormalizationConfidence = "medium";
            else
                entry.NormalizationConfidence = "low";
        }

        // -----------------------------------------------------------------------
        // Delta computation
        // -----------------------------------------------------------------------

        private static List<SoftwareEntry> ComputeDelta(List<SoftwareEntry> baseline, List<SoftwareEntry> current)
        {
            var baselineKeys = new HashSet<string>(
                baseline.Select(e => MakeKey(e)),
                StringComparer.OrdinalIgnoreCase);

            return current.Where(e => !baselineKeys.Contains(MakeKey(e))).ToList();
        }

        private static string MakeKey(SoftwareEntry e)
        {
            return $"{e.RegistrySource}|{e.DisplayName}|{e.DisplayVersion ?? ""}";
        }

        // -----------------------------------------------------------------------
        // Event emission (with chunking for large inventories)
        // -----------------------------------------------------------------------

        private void EmitInventoryEvents(
            string trigger,
            EnrollmentPhase phase,
            List<SoftwareEntry> inventory,
            List<SoftwareEntry> newInstalls,
            int? whiteGlovePart = null)
        {
            int totalChunks = Math.Max(1, (int)Math.Ceiling(inventory.Count / (double)ChunkSize));

            var highCount = inventory.Count(e => e.NormalizationConfidence == "high");
            var medCount = inventory.Count(e => e.NormalizationConfidence == "medium");
            var lowCount = inventory.Count(e => e.NormalizationConfidence == "low");

            for (int i = 0; i < totalChunks; i++)
            {
                var chunk = inventory.Skip(i * ChunkSize).Take(ChunkSize).ToList();

                var data = new Dictionary<string, object>
                {
                    { "triggered_at", trigger },
                    { "total_count", inventory.Count },
                    { "chunk_index", i },
                    { "chunk_count", totalChunks },
                    { "inventory", chunk.Select(SerializeEntry).ToList() }
                };

                // White Glove part tag (1 = pre-provisioning, 2 = user enrollment)
                if (whiteGlovePart.HasValue)
                {
                    data["whiteglove_part"] = whiteGlovePart.Value;
                }

                // Confidence summary only in first chunk
                if (i == 0)
                {
                    data["confidence_summary"] = new Dictionary<string, object>
                    {
                        { "high", highCount },
                        { "medium", medCount },
                        { "low", lowCount }
                    };
                }

                // New installs only in last chunk
                if (i == totalChunks - 1 && newInstalls != null)
                {
                    data["new_installs_during_enrollment"] = newInstalls.Select(SerializeEntry).ToList();
                    data["new_installs_count"] = newInstalls.Count;
                }

                var message = i == 0
                    ? (trigger == "startup"
                        ? $"{Name}: Baseline inventory ({inventory.Count} items, {highCount} high-confidence)"
                        : $"{Name}: Shutdown inventory ({inventory.Count} items, {newInstalls?.Count ?? 0} new during enrollment)")
                    : $"{Name}: Inventory chunk {i + 1}/{totalChunks}";

                _emitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = "software_inventory_analysis",
                    Severity = EventSeverity.Info,
                    Source = Name,
                    Phase = phase,
                    Message = message,
                    Data = data
                });
            }
        }

        private static Dictionary<string, object> SerializeEntry(SoftwareEntry e)
        {
            return new Dictionary<string, object>
            {
                { "displayName", e.DisplayName ?? "" },
                { "displayVersion", e.DisplayVersion ?? "" },
                { "publisher", e.Publisher ?? "" },
                { "installDate", e.InstallDate ?? "" },
                { "registrySource", e.RegistrySource ?? "" },
                { "normalizedPublisher", e.NormalizedPublisher ?? "" },
                { "normalizedName", e.NormalizedName ?? "" },
                { "normalizedVersion", e.NormalizedVersion ?? "" },
                { "normalizationConfidence", e.NormalizationConfidence ?? "low" }
            };
        }

        // -----------------------------------------------------------------------
        // Private model
        // -----------------------------------------------------------------------

        private class SoftwareEntry
        {
            public string DisplayName { get; set; }
            public string DisplayVersion { get; set; }
            public string Publisher { get; set; }
            public string InstallDate { get; set; }
            public string InstallLocation { get; set; }
            public string UninstallString { get; set; }
            public bool IsWindowsInstaller { get; set; }
            public string RegistrySource { get; set; }

            // Normalized fields
            public string NormalizedPublisher { get; set; }
            public string NormalizedName { get; set; }
            public string NormalizedVersion { get; set; }
            public string NormalizationConfidence { get; set; }
        }
    }
}
