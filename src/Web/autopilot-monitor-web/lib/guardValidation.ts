/**
 * Client-side port of agent guardrail validation logic.
 *
 * Mirrors GatherRuleGuards.cs and DiagnosticsPathGuards.cs so the UI can
 * show instant "Allowed" / "Not allowed" feedback without a round-trip.
 */

// ---------------------------------------------------------------------------
// Result type
// ---------------------------------------------------------------------------
export interface ValidationResult {
  allowed: boolean;
  /** Human-readable explanation (shown in tooltip) */
  reason: string;
  /** True if allowed ONLY because unrestricted mode is on */
  unrestricted: boolean;
}

// ---------------------------------------------------------------------------
// Constants — mirror GatherRuleGuards.cs exactly
// ---------------------------------------------------------------------------

export const ALLOWED_REGISTRY_PREFIXES: readonly string[] = [
  // MDM / Enrollment
  "SOFTWARE\\Microsoft\\Enrollments",
  "SOFTWARE\\Microsoft\\EnterpriseDesktopAppManagement",
  "SOFTWARE\\Microsoft\\Provisioning",
  "SOFTWARE\\Microsoft\\PolicyManager",
  "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MDM",
  // AAD / Hybrid Join
  "SOFTWARE\\Microsoft\\IdentityStore",
  "SYSTEM\\CurrentControlSet\\Control\\CloudDomainJoin",
  // Windows Update / WUfB
  "SOFTWARE\\Microsoft\\WindowsUpdate",
  "SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate",
  // BitLocker
  "SOFTWARE\\Microsoft\\BitLocker",
  "SYSTEM\\CurrentControlSet\\Control\\BitLockerStatus",
  // Network / Proxy
  "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Internet Settings",
  "SYSTEM\\CurrentControlSet\\Services\\Tcpip",
  // Autopilot / OOBE / Setup
  "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Setup",
  "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE",
  "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon",
  // TPM
  "SYSTEM\\CurrentControlSet\\Services\\TPM",
  "SOFTWARE\\Microsoft\\Tpm",
  // Intune IME
  "SOFTWARE\\Microsoft\\IntuneManagementExtension",
  // SCEP / Certificates
  "SOFTWARE\\Microsoft\\SystemCertificates",
  "SOFTWARE\\Policies\\Microsoft\\SystemCertificates",
];

export const ALLOWED_FILE_PREFIXES: readonly string[] = [
  "C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs",
  "C:\\Windows\\CCM\\Logs",
  "C:\\Windows\\Logs",
  "C:\\Windows\\Panther",
  "C:\\Windows\\SetupDiag",
  "C:\\ProgramData\\Microsoft\\DiagnosticLogCSP",
  "C:\\Windows\\SoftwareDistribution\\ReportingEvents.log",
];

export const ALLOWED_WMI_QUERY_PREFIXES: readonly string[] = [
  "SELECT * FROM Win32_OperatingSystem",
  "SELECT * FROM Win32_ComputerSystem",
  "SELECT * FROM Win32_BIOS",
  "SELECT * FROM Win32_Processor",
  "SELECT * FROM Win32_BaseBoard",
  "SELECT * FROM Win32_Battery",
  "SELECT * FROM Win32_TPM",
  "SELECT * FROM Win32_NetworkAdapter",
  "SELECT * FROM Win32_NetworkAdapterConfiguration",
  "SELECT * FROM Win32_DiskDrive",
  "SELECT * FROM Win32_LogicalDisk",
  "SELECT * FROM SoftwareLicensingProduct",
];

const ALLOWED_COMMANDS_LIST: readonly string[] = [
  "Get-Tpm",
  "Get-SecureBootPolicy",
  "Get-SecureBootUEFI -Name SetupMode",
  "Get-BitLockerVolume -MountPoint C:",
  "Get-NetAdapter | Select-Object Name, Status, InterfaceDescription, MacAddress, LinkSpeed",
  "Get-DnsClientServerAddress | Select-Object InterfaceAlias, ServerAddresses",
  "Get-NetIPConfiguration | Select-Object InterfaceAlias, IPv4Address, IPv4DefaultGateway, DNSServer",
  "netsh winhttp show proxy",
  "ipconfig /all",
  "nltest /dsgetdc:",
  "dsregcmd /status",
  "certutil -store My",
  "Get-HotFix | Select-Object -First 10 HotFixID, InstalledOn, Description",
];

const ALLOWED_COMMANDS_SET = new Set(
  ALLOWED_COMMANDS_LIST.map((c) => c.toLowerCase())
);

export const ALLOWED_DIAGNOSTICS_PATH_PREFIXES: readonly string[] = [
  "C:\\ProgramData\\AutopilotMonitor",
  "C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs",
  "C:\\Windows\\Panther",
  "C:\\Windows\\Logs",
  "C:\\Windows\\SetupDiag",
  "C:\\Windows\\SoftwareDistribution\\ReportingEvents.log",
  "C:\\Windows\\System32\\winevt\\Logs",
  "C:\\Windows\\CCM\\Logs",
  "C:\\ProgramData\\Microsoft\\DiagnosticLogCSP",
  "C:\\ProgramData\\Microsoft\\Windows\\WER",
  "C:\\Windows\\Logs\\CBS",
];

const BLOCKED_USERS_PREFIX = "C:\\Users";

// ---------------------------------------------------------------------------
// Common Windows environment variables (for client-side expansion)
// ---------------------------------------------------------------------------
const COMMON_ENV_VARS: Record<string, string> = {
  "%ProgramData%": "C:\\ProgramData",
  "%SystemRoot%": "C:\\Windows",
  "%windir%": "C:\\Windows",
  "%SystemDrive%": "C:",
};

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/** Expand known Windows environment variables (case-insensitive). */
function expandEnvVars(path: string): string {
  let result = path;
  for (const [envVar, replacement] of Object.entries(COMMON_ENV_VARS)) {
    const regex = new RegExp(envVar.replace(/%/g, "%"), "gi");
    result = result.replace(regex, replacement);
  }
  return result;
}

/**
 * Simple client-side path normalization.
 * Replaces forward slashes, resolves .. and ., trims trailing backslash.
 */
function normalizePath(path: string): string {
  let p = path.replace(/\//g, "\\");
  // Resolve . and .. segments
  const parts = p.split("\\");
  const resolved: string[] = [];
  for (const part of parts) {
    if (part === "..") {
      if (resolved.length > 1) resolved.pop();
    } else if (part !== "." && part !== "") {
      resolved.push(part);
    } else if (resolved.length === 0 && part === "") {
      // Keep leading empty for UNC paths
    }
  }
  p = resolved.join("\\");
  // Remove trailing backslash (unless it's a root like C:\)
  if (p.length > 3 && p.endsWith("\\")) {
    p = p.slice(0, -1);
  }
  return p;
}

/**
 * Segment-bounded prefix match (case-insensitive).
 * The value must start with prefix and the character at prefix.length
 * must be `separator` or end-of-string.
 */
function matchesPrefix(
  value: string,
  prefix: string,
  separator: string
): boolean {
  if (value.length < prefix.length) return false;
  if (!value.substring(0, prefix.length).toLowerCase().startsWith(prefix.toLowerCase()))
    return false;
  return (
    value.length === prefix.length || value[prefix.length] === separator
  );
}

/**
 * Strip registry hive prefix (HKLM\, HKCU\, or long forms).
 * Returns the subpath after the hive, or the original string if no hive found.
 */
function stripRegistryHive(target: string): string {
  const hivePrefixes = [
    "HKLM\\",
    "HKEY_LOCAL_MACHINE\\",
    "HKCU\\",
    "HKEY_CURRENT_USER\\",
  ];
  const upper = target.toUpperCase();
  for (const prefix of hivePrefixes) {
    if (upper.startsWith(prefix.toUpperCase())) {
      return target.substring(prefix.length);
    }
  }
  return target;
}

// ---------------------------------------------------------------------------
// Validation functions
// ---------------------------------------------------------------------------

export function validateRegistryTarget(
  target: string,
  unrestrictedMode: boolean
): ValidationResult {
  const subPath = stripRegistryHive(target.trim());
  if (!subPath) {
    return { allowed: false, reason: "No registry subpath provided", unrestricted: false };
  }

  if (unrestrictedMode) {
    return { allowed: true, reason: "All registry paths allowed in unrestricted mode", unrestricted: true };
  }

  for (const prefix of ALLOWED_REGISTRY_PREFIXES) {
    if (matchesPrefix(subPath, prefix, "\\")) {
      return { allowed: true, reason: `Matches: ${prefix}`, unrestricted: false };
    }
  }

  return {
    allowed: false,
    reason: "Not under any allowed registry prefix",
    unrestricted: false,
  };
}

export function validateFileTarget(
  target: string,
  unrestrictedMode: boolean
): ValidationResult {
  const trimmed = target.trim();
  if (!trimmed) {
    return { allowed: false, reason: "No file path provided", unrestricted: false };
  }

  const expanded = expandEnvVars(trimmed);

  // Check for unexpanded env vars
  const unexpanded = expanded.match(/%[^%]+%/);
  if (unexpanded) {
    return {
      allowed: false,
      reason: `Contains unknown environment variable ${unexpanded[0]}`,
      unrestricted: false,
    };
  }

  // Handle wildcards in filename: strip filename and normalize directory
  const lastSep = expanded.lastIndexOf("\\");
  const fileName = lastSep >= 0 ? expanded.substring(lastSep + 1) : expanded;
  const hasWildcard = fileName.includes("*") || fileName.includes("?");

  let normalizedDir: string;
  if (hasWildcard) {
    const dir = lastSep >= 0 ? expanded.substring(0, lastSep) : "";
    if (!dir) {
      return { allowed: false, reason: "Wildcard path has no directory", unrestricted: false };
    }
    normalizedDir = normalizePath(dir);
  } else {
    normalizedDir = normalizePath(expanded);
  }

  // Hard block: C:\Users always blocked
  if (matchesPrefix(normalizedDir, BLOCKED_USERS_PREFIX, "\\")) {
    return { allowed: false, reason: "C:\\Users is always blocked (privacy protection)", unrestricted: false };
  }

  if (unrestrictedMode) {
    return { allowed: true, reason: "All file paths allowed in unrestricted mode (except C:\\Users)", unrestricted: true };
  }

  for (const prefix of ALLOWED_FILE_PREFIXES) {
    if (matchesPrefix(normalizedDir, prefix, "\\")) {
      return { allowed: true, reason: `Matches: ${prefix}`, unrestricted: false };
    }
  }

  return {
    allowed: false,
    reason: "Not under any allowed file path prefix",
    unrestricted: false,
  };
}

export function validateWmiTarget(
  target: string,
  unrestrictedMode: boolean
): ValidationResult {
  const trimmed = target.trim();
  if (!trimmed) {
    return { allowed: false, reason: "No WMI query provided", unrestricted: false };
  }

  if (unrestrictedMode) {
    return { allowed: true, reason: "All WMI queries allowed in unrestricted mode", unrestricted: true };
  }

  for (const prefix of ALLOWED_WMI_QUERY_PREFIXES) {
    if (trimmed.length >= prefix.length) {
      const candidate = trimmed.substring(0, prefix.length);
      if (candidate.toLowerCase() === prefix.toLowerCase()) {
        if (trimmed.length === prefix.length || /\s/.test(trimmed[prefix.length])) {
          return { allowed: true, reason: `Matches: ${prefix}`, unrestricted: false };
        }
      }
    }
  }

  return {
    allowed: false,
    reason: "Not a recognized WMI query prefix",
    unrestricted: false,
  };
}

export function validateCommandTarget(
  target: string,
  unrestrictedMode: boolean
): ValidationResult {
  const trimmed = target.trim();
  if (!trimmed) {
    return { allowed: false, reason: "No command provided", unrestricted: false };
  }

  if (unrestrictedMode) {
    return { allowed: true, reason: "All commands allowed in unrestricted mode", unrestricted: true };
  }

  if (ALLOWED_COMMANDS_SET.has(trimmed.toLowerCase())) {
    return { allowed: true, reason: `Exact match on allowlist`, unrestricted: false };
  }

  return {
    allowed: false,
    reason: "Command not on the allowlist (exact match required)",
    unrestricted: false,
  };
}

export function validateDiagnosticsPath(
  rawPath: string,
  unrestrictedMode: boolean
): ValidationResult {
  const trimmed = rawPath.trim();
  if (!trimmed) {
    return { allowed: false, reason: "No path provided", unrestricted: false };
  }

  const expanded = expandEnvVars(trimmed);

  const unexpanded = expanded.match(/%[^%]+%/);
  if (unexpanded) {
    return {
      allowed: false,
      reason: `Contains unknown environment variable ${unexpanded[0]}`,
      unrestricted: false,
    };
  }

  // Handle wildcards in filename
  const lastSep = expanded.lastIndexOf("\\");
  const fileName = lastSep >= 0 ? expanded.substring(lastSep + 1) : expanded;
  const hasWildcard = fileName.includes("*") || fileName.includes("?");

  let normalizedDir: string;
  if (hasWildcard) {
    const dir = lastSep >= 0 ? expanded.substring(0, lastSep) : "";
    if (!dir) {
      return { allowed: false, reason: "Wildcard path has no directory", unrestricted: false };
    }
    normalizedDir = normalizePath(dir);
  } else {
    normalizedDir = normalizePath(expanded);
  }

  // Hard block: C:\Users always blocked
  if (matchesPrefix(normalizedDir, BLOCKED_USERS_PREFIX, "\\")) {
    return { allowed: false, reason: "C:\\Users is always blocked (privacy protection)", unrestricted: false };
  }

  if (unrestrictedMode) {
    return { allowed: true, reason: "All paths allowed in unrestricted mode (except C:\\Users)", unrestricted: true };
  }

  for (const prefix of ALLOWED_DIAGNOSTICS_PATH_PREFIXES) {
    if (matchesPrefix(normalizedDir, prefix, "\\")) {
      return { allowed: true, reason: `Matches: ${prefix}`, unrestricted: false };
    }
  }

  return {
    allowed: false,
    reason: "Not under any allowed diagnostics path prefix",
    unrestricted: false,
  };
}

// ---------------------------------------------------------------------------
// Dispatcher — routes to the correct validator by collector type
// ---------------------------------------------------------------------------

/**
 * Validate a gather rule target. Returns null for empty targets or
 * collector types that don't need path validation (eventlog).
 */
export function validateGatherRuleTarget(
  collectorType: string,
  target: string,
  unrestrictedMode: boolean
): ValidationResult | null {
  if (!target.trim()) return null;

  switch (collectorType) {
    case "registry":
      return validateRegistryTarget(target, unrestrictedMode);
    case "file":
    case "logparser":
    case "json":
    case "xml":
      return validateFileTarget(target, unrestrictedMode);
    case "wmi":
      return validateWmiTarget(target, unrestrictedMode);
    case "command_allowlisted":
    case "command":
      return validateCommandTarget(target, unrestrictedMode);
    case "eventlog":
      return null; // Event log channel names are not path-validated
    default:
      return null;
  }
}
