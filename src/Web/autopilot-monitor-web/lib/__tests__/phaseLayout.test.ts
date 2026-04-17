import { describe, it, expect } from "vitest";
import {
  resolvePhaseLayout,
  V1_PHASES,
  V1_SKIP_USER_PHASES,
  V2_PHASES,
} from "../../app/sessions/[sessionId]/utils/phaseConstants";

describe("resolvePhaseLayout", () => {
  it("returns full V1 layout for WhiteGlove + SkipUserStatusPage=true (user enrollment runs in Part 2)", () => {
    const { phases, useSkipLayout } = resolvePhaseLayout({
      enrollmentType: "v1",
      isSkipUserStatusPage: true,
      isPreProvisioned: true,
    });
    expect(useSkipLayout).toBe(false);
    expect(phases).toBe(V1_PHASES);
    expect(phases.map((p) => p.id)).toEqual([0, 1, 2, 3, 4, 5, 6, 7]);
    expect(phases.find((p) => p.id === 4)?.name).toBe("Account Setup");
  });

  it("returns collapsed V1_SKIP_USER layout for Non-WhiteGlove + SkipUserStatusPage=true (pure device-only)", () => {
    const { phases, useSkipLayout } = resolvePhaseLayout({
      enrollmentType: "v1",
      isSkipUserStatusPage: true,
      isPreProvisioned: false,
    });
    expect(useSkipLayout).toBe(true);
    expect(phases).toBe(V1_SKIP_USER_PHASES);
    // No Account Setup (id=4), Finalizing (id=6) comes before Apps (User) (id=5)
    expect(phases.map((p) => p.id)).toEqual([0, 1, 2, 3, 6, 5, 7]);
    expect(phases.find((p) => p.id === 4)).toBeUndefined();
  });

  it("returns full V1 layout for standard V1 without SkipUserStatusPage", () => {
    const { phases, useSkipLayout } = resolvePhaseLayout({
      enrollmentType: "v1",
      isSkipUserStatusPage: false,
      isPreProvisioned: false,
    });
    expect(useSkipLayout).toBe(false);
    expect(phases).toBe(V1_PHASES);
  });

  it("returns V2 layout for V2 enrollment regardless of SkipUserStatusPage/IsPreProvisioned", () => {
    for (const flags of [
      { isSkipUserStatusPage: false, isPreProvisioned: false },
      { isSkipUserStatusPage: true, isPreProvisioned: false },
      { isSkipUserStatusPage: false, isPreProvisioned: true },
      { isSkipUserStatusPage: true, isPreProvisioned: true },
    ]) {
      const { phases, useSkipLayout } = resolvePhaseLayout({ enrollmentType: "v2", ...flags });
      expect(useSkipLayout).toBe(false);
      expect(phases).toBe(V2_PHASES);
    }
  });

  it("treats undefined/missing SkipUserStatusPage as not-skip", () => {
    const { phases, useSkipLayout } = resolvePhaseLayout({
      enrollmentType: "v1",
      isPreProvisioned: false,
    });
    expect(useSkipLayout).toBe(false);
    expect(phases).toBe(V1_PHASES);
  });
});
