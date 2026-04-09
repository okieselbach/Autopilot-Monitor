import { describe, it, expect } from "vitest";
import {
  KNOWN_EVENT_TYPES,
  findEventType,
  filterEventTypes,
} from "../../app/gather-rules/eventTypes";

describe("KNOWN_EVENT_TYPES", () => {
  it("is not empty", () => {
    expect(KNOWN_EVENT_TYPES.length).toBeGreaterThan(0);
  });

  it("contains all new stall-detection event types", () => {
    const required = [
      "session_stalled",
      "stall_probe_result",
      "stall_probe_check",
      "modern_deployment_error",
      "modern_deployment_warning",
      "modern_deployment_log",
    ];
    for (const r of required) {
      expect(KNOWN_EVENT_TYPES.find((e) => e.value === r), `missing: ${r}`).toBeDefined();
    }
  });

  it("contains core enrollment lifecycle events", () => {
    const required = [
      "enrollment_complete",
      "enrollment_failed",
      "whiteglove_complete",
      "whiteglove_resumed",
      "desktop_arrived",
    ];
    for (const r of required) {
      expect(KNOWN_EVENT_TYPES.find((e) => e.value === r), `missing: ${r}`).toBeDefined();
    }
  });

  it("has no duplicate values", () => {
    const seen = new Set<string>();
    for (const e of KNOWN_EVENT_TYPES) {
      expect(seen.has(e.value), `duplicate: ${e.value}`).toBe(false);
      seen.add(e.value);
    }
  });

  it("gives every entry a non-empty description", () => {
    for (const e of KNOWN_EVENT_TYPES) {
      expect(e.description.length, `empty description for ${e.value}`).toBeGreaterThan(0);
    }
  });
});

describe("findEventType", () => {
  it("returns an entry by exact value", () => {
    const e = findEventType("session_stalled");
    expect(e).toBeDefined();
    expect(e?.category).toBe("stall");
  });

  it("is case-insensitive", () => {
    expect(findEventType("SESSION_STALLED")).toBeDefined();
    expect(findEventType("Session_Stalled")).toBeDefined();
  });

  it("returns undefined for unknown values", () => {
    expect(findEventType("totally_made_up_event")).toBeUndefined();
    expect(findEventType("")).toBeUndefined();
  });
});

describe("filterEventTypes", () => {
  it("returns the full list for an empty query", () => {
    expect(filterEventTypes("").length).toBe(KNOWN_EVENT_TYPES.length);
  });

  it("matches on the value field", () => {
    const r = filterEventTypes("stall");
    expect(r.find((e) => e.value === "session_stalled")).toBeDefined();
    expect(r.find((e) => e.value === "stall_probe_result")).toBeDefined();
  });

  it("matches on the description field", () => {
    const r = filterEventTypes("Pre-Provisioning");
    expect(r.find((e) => e.value === "whiteglove_complete")).toBeDefined();
  });
});
