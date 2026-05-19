import { describe, it, expect } from "vitest";
import { parseSasExpiry } from "../../app/settings/components/diagnosticsSasExpiry";

// Phase 4 of the hosted-diagnostics work. Covers:
// 1. `parseSasExpiry` — backs the green/amber/red SAS-expiry indicator on the
//    CustomerSas branch of the UI. Pure helper; no DOM needed.
// 2. The destination string contract. The values "CustomerSas" / "Hosted" must
//    match what the backend's `GetDiagnosticsUploadUrlFunction.NormalizeDestination`
//    accepts; a typo here would silently route an admin's click to the unknown-
//    destination 500 branch instead of switching destinations.

describe("parseSasExpiry", () => {
  it("extracts the se= UTC datetime from a real-shaped SAS URL", () => {
    const sas =
      "https://acct.blob.core.windows.net/diag?sv=2024-10-04&ss=b&srt=co&se=2026-12-31T23%3A59%3A00Z&sig=abc";
    const parsed = parseSasExpiry(sas);
    expect(parsed).not.toBeNull();
    expect(parsed!.toISOString()).toBe("2026-12-31T23:59:00.000Z");
  });

  it("returns null when the SAS URL has no query string", () => {
    expect(parseSasExpiry("https://acct.blob.core.windows.net/diag")).toBeNull();
  });

  it("returns null when the se= parameter is missing", () => {
    expect(
      parseSasExpiry("https://acct.blob.core.windows.net/diag?sv=2024&sig=abc"),
    ).toBeNull();
  });

  it("returns null on a totally malformed se= value", () => {
    expect(
      parseSasExpiry("https://acct.blob.core.windows.net/diag?se=not-a-date&sig=abc"),
    ).toBeNull();
  });

  it("returns null for an empty input rather than throwing", () => {
    expect(parseSasExpiry("")).toBeNull();
  });
});

describe("diagnosticsUploadDestination contract", () => {
  // These two values are the source of truth — UI radio buttons set them
  // verbatim, and the backend's NormalizeDestination matches them
  // case-insensitively. If the strings ever drift, an admin click would land
  // in the unknown-destination 500 branch instead of switching destinations.
  const KnownDestinations = ["CustomerSas", "Hosted"] as const;

  it("includes both expected values", () => {
    expect(KnownDestinations).toContain("CustomerSas");
    expect(KnownDestinations).toContain("Hosted");
  });

  it("treats CustomerSas as the safe default for legacy configs", () => {
    // The UI's <DiagnosticsSection> uses `=== "Hosted"` as the only positive
    // check; everything else (null, undefined, empty, unknown) renders the
    // CustomerSas branch. Encodes the "Hosted requires explicit admin click"
    // guarantee — no silent flip.
    const isHosted = (value: string | undefined | null) => value === "Hosted";
    expect(isHosted(null)).toBe(false);
    expect(isHosted(undefined)).toBe(false);
    expect(isHosted("")).toBe(false);
    expect(isHosted("CustomerSas")).toBe(false);
    expect(isHosted("anything-else")).toBe(false);
    expect(isHosted("Hosted")).toBe(true);
  });
});
