import { describe, it, expect } from "vitest";
import { buildUniqueValuesByField } from "../uniqueValuesByField";
import type { Session } from "@/types";

function s(partial: Partial<Session>): Session {
  return partial as unknown as Session;
}

describe("buildUniqueValuesByField", () => {
  it("returns an empty string[] for every requested field when sessions is empty", () => {
    const result = buildUniqueValuesByField([], ["status", "osName"]);
    expect(result.status).toEqual([]);
    expect(result.osName).toEqual([]);
  });

  it("collects unique non-empty values per field", () => {
    const sessions = [
      s({ status: "success", osName: "Windows 11" }),
      s({ status: "failed",  osName: "Windows 11" }),
      s({ status: "success", osName: "Windows 10" }),
    ];
    const result = buildUniqueValuesByField(sessions, ["status", "osName"]);
    expect(result.status).toEqual(["failed", "success"]);
    expect(result.osName).toEqual(["Windows 10", "Windows 11"]);
  });

  it("skips null, undefined, and empty-string values", () => {
    const sessions = [
      s({ status: "success", osName: null as unknown as string }),
      s({ status: undefined as unknown as string, osName: "" }),
      s({ status: "", osName: "Windows 11" }),
    ];
    const result = buildUniqueValuesByField(sessions, ["status", "osName"]);
    expect(result.status).toEqual(["success"]);
    expect(result.osName).toEqual(["Windows 11"]);
  });

  it("coerces non-string values to string (e.g. numbers)", () => {
    const sessions = [
      s({ eventCount: 10 }),
      s({ eventCount: 3 }),
      s({ eventCount: 10 }),
    ];
    const result = buildUniqueValuesByField(sessions, ["eventCount"]);
    expect(result.eventCount).toEqual(["10", "3"]);
  });

  it("returns sorted output", () => {
    const sessions = [
      s({ status: "zulu" }),
      s({ status: "alpha" }),
      s({ status: "mike" }),
    ];
    const result = buildUniqueValuesByField(sessions, ["status"]);
    expect(result.status).toEqual(["alpha", "mike", "zulu"]);
  });

  it("makes a single pass regardless of field count (behavioral: all fields populated from same session list)", () => {
    const sessions = [
      s({ status: "success", osName: "Win11", manufacturer: "Dell" }),
      s({ status: "failed",  osName: "Win10", manufacturer: "HP"   }),
    ];
    const result = buildUniqueValuesByField(sessions, ["status", "osName", "manufacturer"]);
    expect(result.status).toEqual(["failed", "success"]);
    expect(result.osName).toEqual(["Win10", "Win11"]);
    expect(result.manufacturer).toEqual(["Dell", "HP"]);
  });
});
