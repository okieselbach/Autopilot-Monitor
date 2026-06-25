import { describe, expect, it } from "vitest";
import { computeFleetRollup, type FleetSummary } from "../fleetRollup";

function summary(p: Partial<FleetSummary>): FleetSummary {
  return {
    activeCount: 0,
    totalLastNDays: 0,
    succeededLastNDays: 0,
    failedLastNDays: 0,
    successRatePct: 0,
    ...p,
  };
}

describe("computeFleetRollup", () => {
  it("returns zeros for an empty fleet", () => {
    const r = computeFleetRollup([]);
    expect(r).toEqual({
      tenantCount: 0,
      activeCount: 0,
      totalLastNDays: 0,
      succeededLastNDays: 0,
      failedLastNDays: 0,
      successRatePct: 0,
    });
  });

  it("sums per-tenant counts", () => {
    const r = computeFleetRollup([
      summary({ activeCount: 2, totalLastNDays: 10, succeededLastNDays: 8, failedLastNDays: 2 }),
      summary({ activeCount: 3, totalLastNDays: 5, succeededLastNDays: 5, failedLastNDays: 0 }),
    ]);
    expect(r.tenantCount).toBe(2);
    expect(r.activeCount).toBe(5);
    expect(r.totalLastNDays).toBe(15);
    expect(r.succeededLastNDays).toBe(13);
    expect(r.failedLastNDays).toBe(2);
  });

  it("weights the success rate by session volume, not by tenant", () => {
    // A: 900/1000 = 90%, B: 0/10 = 0%. A naive average would be 45%; the weighted rate is 900/1010 ≈ 89.1%.
    const r = computeFleetRollup([
      summary({ totalLastNDays: 1000, succeededLastNDays: 900 }),
      summary({ totalLastNDays: 10, succeededLastNDays: 0 }),
    ]);
    expect(r.successRatePct).toBeCloseTo(89.1, 1);
  });

  it("reports 0% success when the fleet has no sessions", () => {
    const r = computeFleetRollup([summary({ activeCount: 1 }), summary({})]);
    expect(r.totalLastNDays).toBe(0);
    expect(r.successRatePct).toBe(0);
    expect(r.tenantCount).toBe(2);
  });
});
