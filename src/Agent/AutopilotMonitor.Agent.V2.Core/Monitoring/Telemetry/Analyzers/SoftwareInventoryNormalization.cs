using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers
{
    /// <summary>
    /// Static normalization tables for <see cref="SoftwareInventoryAnalyzer"/>:
    /// publisher canonicalization, exclusion patterns, AppX whitelist, and
    /// regex helpers used to clean up display name / version strings.
    ///
    /// Edit this file when adding new vendor mappings, exclusion entries, or
    /// AppX whitelist members. The collection / emission logic lives in
    /// <c>SoftwareInventoryAnalyzer.cs</c>.
    /// </summary>
    internal static class SoftwareInventoryNormalization
    {
        // -----------------------------------------------------------------------
        // Publisher normalization map
        //
        // Maps raw Publisher strings (as written into the Uninstall registry by
        // each installer) to a canonical lowercase token. The token is consumed
        // by server-side CVE/KEV correlation as a vendor key, and locally by
        // AssessConfidence as the "publisher is known" signal.
        //
        // Lookup is case-insensitive (OrdinalIgnoreCase). Keep keys lowercase
        // for readability.
        // -----------------------------------------------------------------------

        public static readonly Dictionary<string, string> PublisherMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Browsers & web stack
            { "microsoft corporation", "microsoft" },
            { "microsoft", "microsoft" },
            { "google llc", "google" },
            { "google inc", "google" },
            { "google inc.", "google" },
            { "mozilla corporation", "mozilla" },
            { "mozilla", "mozilla" },
            { "brave software, inc.", "brave" },
            { "brave software inc.", "brave" },
            { "brave software", "brave" },
            { "vivaldi technologies as", "vivaldi" },
            { "vivaldi technologies", "vivaldi" },
            { "opera software", "opera" },
            { "opera norway as", "opera" },
            { "the tor project, inc.", "tor" },
            { "the tor project", "tor" },

            // Major OEMs / vendors
            { "adobe inc.", "adobe" },
            { "adobe systems incorporated", "adobe" },
            { "adobe systems, incorporated", "adobe" },
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

            // Communication & collaboration
            { "zoom video communications, inc.", "zoom" },
            { "zoom", "zoom" },
            { "slack technologies, inc.", "slack" },
            { "slack technologies, llc", "slack" },
            { "webex communications", "webex" },
            { "cisco webex llc", "webex" },
            { "discord inc.", "discord" },
            { "signal messenger, llc", "signal" },
            { "signal", "signal" },
            { "ringcentral, inc.", "ringcentral" },
            { "ringcentral", "ringcentral" },
            { "gotomeeting", "goto" },
            { "goto inc.", "goto" },
            { "logmein, inc.", "logmein" },
            { "whatsapp llc", "whatsapp" },
            { "meta platforms, inc.", "meta" },

            // Compression / archive utilities
            { "7-zip", "7-zip" },
            { "igor pavlov", "7-zip" },
            { "the 7-zip developers", "7-zip" },
            { "rarlab", "winrar" },
            { "alexander roshal", "winrar" },
            { "winrar", "winrar" },

            // Editors / small utilities
            { "notepad++ team", "notepad++" },
            { "don ho", "notepad++" },
            { "putty", "putty" },
            { "simon tatham", "putty" },
            { "winscp", "winscp" },
            { "martin prikryl", "winscp" },
            { "teamviewer", "teamviewer" },
            { "teamviewer germany gmbh", "teamviewer" },

            // Endpoint security & AV
            { "fortinet technologies (canada) inc.", "fortinet" },
            { "fortinet inc", "fortinet" },
            { "palo alto networks", "paloaltonetworks" },
            { "crowdstrike, inc.", "crowdstrike" },
            { "crowdstrike inc.", "crowdstrike" },
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
            { "bitdefender", "bitdefender" },
            { "bitdefender srl", "bitdefender" },
            { "gen digital inc.", "gendigital" },
            { "nortonlifelock inc.", "norton" },
            { "norton", "norton" },
            { "avast software s.r.o.", "avast" },
            { "avast", "avast" },
            { "avg technologies", "avg" },
            { "avg", "avg" },
            { "withsecure", "withsecure" },
            { "f-secure", "withsecure" },
            { "f-secure corporation", "withsecure" },
            { "webroot inc.", "webroot" },
            { "webroot", "webroot" },
            { "cybereason inc.", "cybereason" },
            { "blackberry limited", "blackberry" },
            { "blackberry", "blackberry" },
            { "cylance, inc.", "cylance" },
            { "cylance", "cylance" },
            { "trellix", "trellix" },
            { "bromium", "bromium" },

            // Vulnerability / scanning agents
            { "rapid7 llc", "rapid7" },
            { "rapid7", "rapid7" },
            { "tenable, inc.", "tenable" },
            { "tenable", "tenable" },
            { "qualys, inc.", "qualys" },
            { "qualys", "qualys" },

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
            { "cloudflare, inc.", "cloudflare" },
            { "cloudflare", "cloudflare" },
            { "tailscale inc.", "tailscale" },
            { "tailscale", "tailscale" },
            { "mullvad", "mullvad" },
            { "nordvpn s.a.", "nordvpn" },
            { "nordvpn", "nordvpn" },
            { "express vpn international ltd", "expressvpn" },
            { "proton ag", "proton" },
            { "proton vpn ag", "proton" },
            { "forcepoint llc", "forcepoint" },
            { "forcepoint", "forcepoint" },

            // Identity & MFA
            { "okta, inc.", "okta" },
            { "okta", "okta" },
            { "duo security", "duo" },
            { "duo security llc", "duo" },
            { "cisco duo", "duo" },
            { "ping identity corporation", "pingidentity" },
            { "ping identity", "pingidentity" },

            // Email security
            { "proofpoint, inc.", "proofpoint" },
            { "proofpoint", "proofpoint" },
            { "mimecast services limited", "mimecast" },
            { "mimecast", "mimecast" },
            { "barracuda networks, inc.", "barracuda" },
            { "barracuda networks", "barracuda" },

            // Password managers
            { "agilebits inc.", "1password" },
            { "1password", "1password" },
            { "bitwarden inc.", "bitwarden" },
            { "bitwarden", "bitwarden" },

            // File sync & cloud storage
            { "dropbox, inc.", "dropbox" },
            { "dropbox", "dropbox" },
            { "box, inc.", "box" },
            { "box", "box" },
            { "egnyte inc.", "egnyte" },

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

            // IT management & RMM (US-heavy)
            { "connectwise, llc", "connectwise" },
            { "kaseya us llc", "kaseya" },
            { "n-able solutions", "nable" },
            { "n-able", "nable" },
            { "datto, inc.", "datto" },
            { "atera networks ltd.", "atera" },
            { "manageengine", "manageengine" },
            { "zoho corporation", "zoho" },

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
            { "axosoft, llc", "gitkraken" },
            { "kong inc.", "insomnia" },
            { "anaconda, inc.", "anaconda" },
            { "posit software, pbc", "rstudio" },
            { "rstudio, pbc", "rstudio" },

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
            { "qualcomm technologies, inc.", "qualcomm" },
            { "qualcomm", "qualcomm" },
            { "mediatek inc.", "mediatek" },
            { "synaptics incorporated", "synaptics" },
            { "synaptics", "synaptics" },
            { "razer inc.", "razer" },
            { "razer", "razer" },

            // Database & data tools
            { "dbeaver corp", "dbeaver" },
            { "mysql ab", "mysql" },
            { "mariadb", "mariadb" },
            { "the postgresql global development group", "postgresql" },
            { "pgadmin development team", "pgadmin" },
            { "snowflake inc.", "snowflake" },
            { "databricks, inc.", "databricks" },
            { "tableau software, llc", "tableau" },
            { "tableau software", "tableau" },
            { "the mathworks, inc.", "matlab" },
            { "wolfram research", "wolfram" },
            { "sas institute inc.", "sas" },
            { "alteryx, inc.", "alteryx" },

            // Observability / monitoring (US-heavy)
            { "splunk inc.", "splunk" },
            { "splunk", "splunk" },
            { "datadog, inc.", "datadog" },
            { "datadog", "datadog" },
            { "new relic, inc.", "newrelic" },
            { "elasticsearch b.v.", "elastic" },
            { "elastic n.v.", "elastic" },

            // Languages / runtimes
            { "git", "git" },
            { "the git development community", "git" },
            { "python software foundation", "python" },
            { "node.js", "nodejs" },
            { "node.js foundation", "nodejs" },
            { "openjs foundation", "nodejs" },

            // Backup & imaging
            { "veeam software", "veeam" },
            { "veeam", "veeam" },
            { "acronis", "acronis" },
            { "acronis international gmbh", "acronis" },
            { "macrium software", "macrium" },
            { "corel corporation", "corel" },
            { "veritas technologies llc", "veritas" },
            { "commvault systems, inc.", "commvault" },
            { "code42 software, inc.", "code42" },

            // Finance / business / signing (US-heavy)
            { "intuit inc.", "intuit" },
            { "intuit", "intuit" },
            { "bloomberg finance l.p.", "bloomberg" },
            { "docusign, inc.", "docusign" },
            { "docusign", "docusign" },
            { "qualtrics, llc", "qualtrics" },

            // Consumer / gaming (often present on BYOD / first-time-user devices)
            { "spotify ab", "spotify" },
            { "valve corporation", "valve" },
            { "valve", "valve" },
            { "epic games, inc.", "epicgames" },
        };

        // -----------------------------------------------------------------------
        // Publisher suffix stripping
        //
        // Applied as a fallback when the raw publisher string is NOT in the
        // PublisherMap above. We strip exactly one trailing legal-entity suffix
        // so that "Acme Software, Inc." → "acme software".
        // -----------------------------------------------------------------------

        public static readonly string[] PublisherSuffixes = new[]
        {
            ", inc.", " inc.", " inc", ", llc", " llc", ", ltd", " ltd", " ltd.",
            " corporation", " corp.", " corp", " gmbh", " ag", " se",
            " co.", " co", " group", " limited"
        };

        // -----------------------------------------------------------------------
        // Exclude patterns for filtering registry noise
        // -----------------------------------------------------------------------

        public static readonly Regex KbUpdatePattern = new Regex(@"^KB\d{6,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static readonly string[] ExcludeContains = new[]
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

        public static readonly string[] ExcludeStartsWith = new[]
        {
            "vs_",          // Visual Studio installer bootstrapper components
            "MSVC",         // MSVC redistributable sub-components (the parent is kept)
        };

        // -----------------------------------------------------------------------
        // AppX / MSIX package whitelist (strict)
        //
        // Sandboxed AppX/MSIX apps rarely have impactful CVEs and the sandbox
        // limits blast radius, so we only surface packages with known security
        // or enterprise relevance to keep the report clean.
        // -----------------------------------------------------------------------

        public static readonly HashSet<string> AppxWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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
            "Microsoft.MicrosoftOfficeHub",

            // Cloud sync (data-exfil + frequent CVE surface)
            "Microsoft.OneDrive",

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
        public static readonly Regex AppxPackagePattern = new Regex(
            @"^(.+?)_(\d+(?:\.\d+)*)_([^_]*)__([a-z0-9]+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // -----------------------------------------------------------------------
        // Regex helpers for display name / version normalization
        // -----------------------------------------------------------------------

        public static readonly Regex ArchitecturePattern = new Regex(
            @"\s*[\(\-]\s*(?:x64|x86|64-bit|32-bit|amd64|arm64)\s*[\)]?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static readonly Regex TrailingVersionPattern = new Regex(
            @"\s+v?\d+(?:\.\d+)*\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static readonly Regex VersionExtractPattern = new Regex(
            @"(\d+(?:\.\d+){0,3})",
            RegexOptions.Compiled);
    }
}
