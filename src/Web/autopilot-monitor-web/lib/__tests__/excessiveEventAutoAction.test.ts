import { describe, it, expect } from "vitest";
import {
  AUTO_ACTION_MODES,
  describeAutoActionWarning,
} from "../../app/admin/components/excessiveEventAutoAction";

// Locks in the soft-validation contract for the auto-block/kill section under
// the Excessive Session Events rule in OpsAlertRulesSection. Submit must remain
// enabled in every case (backend tolerates), but the warning text guides operators
// away from configurations that would either fire instantly or never fire at all.

describe("AUTO_ACTION_MODES", () => {
  it("exposes Off first so it's the default option in the select", () => {
    expect(AUTO_ACTION_MODES[0]).toBe("Off");
    expect(AUTO_ACTION_MODES).toEqual(["Off", "Block", "Kill"]);
  });
});

describe("describeAutoActionWarning", () => {
  it("returns null when the feature is off", () => {
    expect(describeAutoActionWarning("Off", 1000, 2000)).toBeNull();
    expect(describeAutoActionWarning("Off", 0, 0)).toBeNull();
  });

  it("warns when block/kill is selected but threshold is zero", () => {
    const msg = describeAutoActionWarning("Block", 0, 2000);
    expect(msg).toContain("greater than 0");
  });

  it("warns when block/kill is selected but threshold is negative", () => {
    const msg = describeAutoActionWarning("Kill", -1, 2000);
    expect(msg).toContain("greater than 0");
  });

  it("warns when auto-action threshold is not strictly higher than warn", () => {
    // Equal — would fire warn + auto on the same event count, defeating the warn-first design.
    expect(describeAutoActionWarning("Block", 2000, 2000)).toContain("higher than the warn threshold");
    // Lower — auto-action would fire before warn, which is what the user wanted to avoid.
    expect(describeAutoActionWarning("Kill", 1500, 2000)).toContain("higher than the warn threshold");
  });

  it("returns null when auto-action threshold is strictly higher than warn", () => {
    expect(describeAutoActionWarning("Block", 2500, 2000)).toBeNull();
    expect(describeAutoActionWarning("Kill", 10000, 100)).toBeNull();
  });

  it("ignores the warn-vs-auto comparison when warn is disabled", () => {
    // warnThreshold = 0 means warn-tier is off; auto-action stands alone and is fine.
    expect(describeAutoActionWarning("Block", 500, 0)).toBeNull();
  });
});
