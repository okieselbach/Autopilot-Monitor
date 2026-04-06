/**
 * Confidence level for an error code mapping.
 *  - "high"   — Documented by Microsoft (MS Learn, official docs)
 *  - "medium" — Community-confirmed (MVP blogs, Q&A forums, consistent field reports)
 *  - "low"    — Inferred or rarely seen, may not be accurate in all contexts
 */
export type ErrorCodeConfidence = "high" | "medium" | "low";

export interface ErrorCodeEntry {
  description: string;
  confidence: ErrorCodeConfidence;
  source: string;
}

/**
 * Mapping of known Windows / MSI / Intune error codes to structured entries.
 * Keys are normalised lowercase hex strings (e.g. "0x80070005") or decimal strings (e.g. "1603").
 */
const errorCodeMap: Record<string, ErrorCodeEntry> = {
  // ── MSI / Installer exit codes (decimal) ──────────────────────────
  "0":    { description: "Success", confidence: "high", source: "MS Learn Docs" },
  "1602": { description: "User cancelled installation", confidence: "high", source: "MS Learn Docs" },
  "1603": { description: "Fatal error during installation", confidence: "high", source: "MS Learn Docs" },
  "1618": { description: "Another installation already in progress", confidence: "high", source: "MS Learn Docs" },
  "1619": { description: "Installation package could not be opened", confidence: "high", source: "MS Learn Docs" },
  "1620": { description: "Installation package invalid", confidence: "high", source: "MS Learn Docs" },
  "1622": { description: "Error opening installation log file", confidence: "high", source: "MS Learn Docs" },
  "1624": { description: "Error applying transforms", confidence: "high", source: "MS Learn Docs" },
  "1625": { description: "Installation prohibited by policy", confidence: "high", source: "MS Learn Docs" },
  "1633": { description: "Unsupported platform", confidence: "high", source: "MS Learn Docs" },
  "1638": { description: "Another version already installed", confidence: "high", source: "MS Learn Docs" },
  "3010": { description: "Reboot required to complete installation", confidence: "high", source: "MS Learn Docs" },

  // ── Windows HRESULTs (hex) ────────────────────────────────────────
  "0x00000000": { description: "Success (S_OK)", confidence: "high", source: "MS Learn Docs" },
  "0x80004005": { description: "Unspecified failure (E_FAIL)", confidence: "high", source: "MS Learn Docs" },
  "0x8000ffff": { description: "Unexpected error during installation", confidence: "high", source: "MS Learn Docs" },
  "0x80070001": { description: "Incorrect function (generally invalid tasks)", confidence: "medium", source: "MVP Blog" },
  "0x80070002": { description: "System cannot find the file specified (AV interference)", confidence: "medium", source: "MVP Blog" },
  "0x80070005": { description: "Access denied", confidence: "high", source: "MS Learn Docs" },
  "0x80070020": { description: "File in use (sharing violation)", confidence: "high", source: "MS Learn Docs" },
  "0x80070032": { description: "Request not supported", confidence: "high", source: "MS Learn Docs" },
  "0x80070057": { description: "Invalid parameter (E_INVALIDARG)", confidence: "high", source: "MS Learn Docs" },
  "0x80070070": { description: "Insufficient disk space", confidence: "high", source: "MS Learn Docs" },
  "0x80070652": { description: "Another installation in progress", confidence: "high", source: "MS Learn Docs" },
  "0x800704c7": { description: "Operation cancelled by user", confidence: "high", source: "MS Learn Docs" },
  "0x80070bc9": { description: "Windows Update restart required", confidence: "high", source: "MS Learn Docs" },
  "0x8007013a": { description: "Physical resources of disk exhausted", confidence: "medium", source: "MVP Blog" },
  "0x80073b92": { description: "Package already exists (APPX)", confidence: "high", source: "MS Learn Docs" },
  "0x80073cf0": { description: "Package unsigned / publisher mismatch", confidence: "high", source: "MS Learn Docs" },
  "0x80073cf3": { description: "Package conflict / dependency not found", confidence: "high", source: "MS Learn Docs" },
  "0x80073cf9": { description: "Package install failed (APPX)", confidence: "high", source: "MS Learn Docs" },
  "0x80073cfb": { description: "Package already installed, reinstall blocked", confidence: "high", source: "MS Learn Docs" },
  "0x80073cff": { description: "Sideloading not enabled", confidence: "high", source: "MS Learn Docs" },
  "0x80073d06": { description: "Package registration failure (APPX)", confidence: "high", source: "MS Learn Docs" },

  // ── Intune-specific codes (hex) ───────────────────────────────────
  "0x87d00213": { description: "App not applicable", confidence: "high", source: "MS Learn Docs" },
  "0x87d00215": { description: "App dependency failed", confidence: "high", source: "MS Learn Docs" },
  "0x87d00216": { description: "App supersedence conflict", confidence: "high", source: "MS Learn Docs" },
  "0x87d00324": { description: "App installation failed", confidence: "high", source: "MS Learn Docs" },
  "0x87d00607": { description: "App download failed", confidence: "high", source: "MS Learn Docs" },
  "0x87d00651": { description: "Detection rules not met after install", confidence: "high", source: "MS Learn Docs" },
  "0x87d103e8": { description: "Unknown error (generic)", confidence: "high", source: "MS Learn Docs" },
  "0x87d13b88": { description: "License assignment failed with token expired (VPP)", confidence: "high", source: "MS Learn Docs / Q&A" },
  "0x87d1fde8": { description: "Remediation failed", confidence: "high", source: "MS Learn Docs" },
  "0x87d1041c": { description: "Application not detected after installation completed successfully", confidence: "high", source: "MS Learn Docs" },
  "0x87d30000": { description: "Unknown error (no defined output from current step)", confidence: "medium", source: "MS Community Hub + MVP Blogs" },
  "0x87d30004": { description: "App download size exceeds limit or hash mismatch", confidence: "low", source: "MVP Blog" },
  "0x87d30006": { description: "Win32 app failed with unspecified error", confidence: "medium", source: "MS Community Hub" },
  "0x87d30065": { description: "Failed to retrieve content info (network)", confidence: "medium", source: "MVP Blog" },
  "0x87d30067": { description: "Error unzipping downloaded content", confidence: "high", source: "MS Q&A + MVP Blogs" },
  "0x87d30068": { description: "Error downloading content", confidence: "medium", source: "MVP Blog" },
  "0x87d3006a": { description: "CDN timeout for downloading app content", confidence: "medium", source: "MVP Blog" },
  "0x87d300c9": { description: "Unmonitored process in progress, may timeout", confidence: "high", source: "MS Q&A + MVP Blogs" },
  "0x87d300cd": { description: "User logged off while app policy was being processed", confidence: "medium", source: "MVP Blog" },
};

/**
 * Look up a structured error code entry for a Windows / MSI / Intune error code.
 * Returns null when no mapping is found.
 */
function lookupEntry(code: string | number | null | undefined): ErrorCodeEntry | null {
  if (code == null) return null;
  const raw = String(code).trim();
  if (raw === "") return null;

  // 1) Direct lookup (handles decimal keys like "1603" and already-lowered hex)
  const direct = errorCodeMap[raw.toLowerCase()];
  if (direct) return direct;

  // 2) Hex input normalisation ("0X..." → "0x...")
  if (/^0x/i.test(raw)) {
    const normalised = "0x" + raw.slice(2).toLowerCase().replace(/^0+/, "")
      .padStart(8, "0");
    const found = errorCodeMap[normalised];
    if (found) return found;
  }

  // 3) Signed-decimal HRESULT → unsigned hex  (e.g. -2147024891 → 0x80070005)
  const num = parseInt(raw, 10);
  if (!isNaN(num) && num < 0) {
    const hex = "0x" + (num >>> 0).toString(16).padStart(8, "0");
    const found = errorCodeMap[hex];
    if (found) return found;
  }

  return null;
}

/**
 * Look up a human-readable description for a Windows / MSI / Intune error code.
 *
 * Accepts:
 *  - Decimal strings:       "1603", "0"
 *  - Hex strings:           "0x80070005", "0X80070005"
 *  - Signed-decimal HRESULT: "-2147024891"  (converted to unsigned hex internally)
 *
 * Returns null when no mapping is found.
 */
export function getErrorCodeDescription(code: string | number | null | undefined): string | null {
  return lookupEntry(code)?.description ?? null;
}

/**
 * Look up the full structured entry (description + confidence + source) for an error code.
 * Returns null when no mapping is found.
 */
export function getErrorCodeEntry(code: string | number | null | undefined): ErrorCodeEntry | null {
  return lookupEntry(code);
}

/**
 * Format a raw numeric error code for display.
 * Signed-decimal HRESULTs are converted to hex notation.
 * Decimal exit codes stay as-is.
 */
export function formatErrorCode(code: string | number): string {
  const raw = String(code).trim();
  const num = parseInt(raw, 10);

  // Already hex
  if (/^0x/i.test(raw)) return raw.toUpperCase();

  // Negative signed-decimal → hex
  if (!isNaN(num) && num < 0) {
    return "0x" + (num >>> 0).toString(16).toUpperCase();
  }

  // Positive decimal (exit code) — keep as-is
  return raw;
}
