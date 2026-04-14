import { describe, it, expect } from "vitest";
import { serializeHarmlessEventIds } from "../../app/admin/harmlessEventIds";

describe("serializeHarmlessEventIds", () => {
  it("parses a comma-separated list", () => {
    expect(serializeHarmlessEventIds("100, 1005")).toBe("[100,1005]");
  });

  it("drops non-integer tokens", () => {
    expect(serializeHarmlessEventIds("100, 1005, abc, 7")).toBe("[100,1005,7]");
  });

  it("handles mixed whitespace and semicolons", () => {
    expect(serializeHarmlessEventIds("100;1005  7")).toBe("[100,1005,7]");
  });

  it("returns empty array for empty input", () => {
    expect(serializeHarmlessEventIds("")).toBe("[]");
    expect(serializeHarmlessEventIds("   ")).toBe("[]");
  });

  it("ignores negative numbers", () => {
    expect(serializeHarmlessEventIds("100, -5, 1005")).toBe("[100,1005]");
  });
});
