import { describe, it, expect } from "vitest";
import { interpolateRuleTemplate } from "../interpolateRuleTemplate";

describe("interpolateRuleTemplate", () => {
  const matched = {
    ca2023_not_updated: {
      eventId: "abc",
      sequence: 23,
      timestamp: "2026-04-28T18:24:16Z",
      field: "uefiCA2023Status",
      value: "unknown",
    },
    secureboot_enabled: {
      eventId: "abc",
      field: "uefiSecureBootEnabled",
      value: "True",
    },
  };

  it("replaces {{field}} tokens from matchedConditions", () => {
    const out = interpolateRuleTemplate(
      "Status: `{{uefiCA2023Status}}`",
      matched
    );
    expect(out).toBe("Status: `unknown`");
  });

  it("replaces multiple distinct tokens in one string", () => {
    const out = interpolateRuleTemplate(
      "SB={{uefiSecureBootEnabled}} CA={{uefiCA2023Status}}",
      matched
    );
    expect(out).toBe("SB=True CA=unknown");
  });

  it("falls back to signal-name lookup when field is unknown", () => {
    const out = interpolateRuleTemplate("{{ca2023_not_updated}}", matched);
    expect(out).toBe("unknown");
  });

  it("tolerates whitespace inside braces", () => {
    const out = interpolateRuleTemplate("[{{  uefiCA2023Status  }}]", matched);
    expect(out).toBe("[unknown]");
  });

  it("leaves unknown tokens untouched so authors notice the typo", () => {
    const out = interpolateRuleTemplate("{{notARealField}}", matched);
    expect(out).toBe("{{notARealField}}");
  });

  it("returns input unchanged when matchedConditions is null/undefined", () => {
    expect(interpolateRuleTemplate("hello {{x}}", null)).toBe("hello {{x}}");
    expect(interpolateRuleTemplate("hello {{x}}", undefined)).toBe(
      "hello {{x}}"
    );
  });

  it("handles non-string scalar values (numbers, booleans)", () => {
    const out = interpolateRuleTemplate("seq={{n}} ok={{b}}", {
      n: { field: "n", value: 42 },
      b: { field: "b", value: false },
    });
    expect(out).toBe("seq=42 ok=false");
  });

  it("returns empty string for null/undefined input", () => {
    expect(interpolateRuleTemplate(null, matched)).toBe("");
    expect(interpolateRuleTemplate(undefined, matched)).toBe("");
  });

  it("does not blow up on text with no placeholders", () => {
    expect(interpolateRuleTemplate("plain text", matched)).toBe("plain text");
  });
});
