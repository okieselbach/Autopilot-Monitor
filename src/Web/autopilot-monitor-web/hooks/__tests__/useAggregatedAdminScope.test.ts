import { describe, expect, it } from "vitest";
import { resolveDelegatedSeed, resolveGaSeed } from "../aggregatedAdminScopeSeed";

/**
 * Tests for the pure seed-resolution core of useAggregatedAdminScope. The hook itself wires four React
 * contexts (tenant / auth / admin-mode / tenant-list) and adjusts state during render; the interesting,
 * regression-prone logic is the seed PRECEDENCE, which these resolvers isolate so it can be pinned without
 * a DOM. Scenarios mirror the code-review follow-up: a GA/Reader defaulting to the aggregated view, a URL
 * deep-link / persisted selection overriding that default, and a delegated caller being unaffected by it.
 */

const OWN = "own-tenant-id";
const OTHER = "other-tenant-id";

describe("resolveGaSeed", () => {
  it("defaults a GA/Reader to the aggregated view ('') when defaultAggregated is set (audit page)", () => {
    // No deep-link, nothing persisted → the page default takes over. With defaultAggregated the GA lands
    // in the cross-tenant 'All tenants' aggregate instead of their own tenant.
    expect(
      resolveGaSeed({
        firstInit: true,
        urlTenantId: undefined,
        storedScope: null,
        ownTenantId: OWN,
        defaultAggregated: true,
      }),
    ).toBe("");
  });

  it("defaults a GA/Reader to their own tenant when defaultAggregated is NOT set (other pages)", () => {
    expect(
      resolveGaSeed({
        firstInit: true,
        urlTenantId: undefined,
        storedScope: null,
        ownTenantId: OWN,
        defaultAggregated: false,
      }),
    ).toBe(OWN);
  });

  it("lets a first-init ?tenantId= deep-link win over the defaultAggregated default", () => {
    expect(
      resolveGaSeed({
        firstInit: true,
        urlTenantId: OTHER,
        storedScope: null,
        ownTenantId: OWN,
        defaultAggregated: true,
      }),
    ).toBe(OTHER);
  });

  it("lets the deep-link win over a persisted selection too (first-init precedence)", () => {
    expect(
      resolveGaSeed({
        firstInit: true,
        urlTenantId: OTHER,
        storedScope: "stored-tenant",
        ownTenantId: OWN,
        defaultAggregated: false,
      }),
    ).toBe(OTHER);
  });

  it("ignores the deep-link when it is NOT the first init (re-entering GA mode restores persisted scope)", () => {
    // The URL seed expresses one-time intent on first load. On a later re-seed (e.g. toggling GA mode back
    // on) the deep-link must not resurface and clobber what the user has since selected.
    expect(
      resolveGaSeed({
        firstInit: false,
        urlTenantId: OTHER,
        storedScope: "stored-tenant",
        ownTenantId: OWN,
        defaultAggregated: false,
      }),
    ).toBe("stored-tenant");
  });

  it("honours a persisted selection over the page default", () => {
    expect(
      resolveGaSeed({
        firstInit: true,
        urlTenantId: undefined,
        storedScope: "stored-tenant",
        ownTenantId: OWN,
        defaultAggregated: true,
      }),
    ).toBe("stored-tenant");
  });

  it("treats a persisted '' as an intentional aggregated selection, not 'unset'", () => {
    // "" is a VALID persisted value for a GA (they chose the aggregate). It must be honoured verbatim and
    // must not fall through to the own-tenant default — only a null (never-persisted) storage does that.
    expect(
      resolveGaSeed({
        firstInit: true,
        urlTenantId: undefined,
        storedScope: "",
        ownTenantId: OWN,
        defaultAggregated: false,
      }),
    ).toBe("");
  });

  it("ignores an empty-string deep-link (falsy) and falls through to persisted/default", () => {
    // urlTenantId of "" is not a real deep-link target; the truthiness guard skips it.
    expect(
      resolveGaSeed({
        firstInit: true,
        urlTenantId: "",
        storedScope: null,
        ownTenantId: OWN,
        defaultAggregated: true,
      }),
    ).toBe("");
  });
});

describe("resolveDelegatedSeed", () => {
  const MANAGED = ["t-a", "t-b", "t-c"];

  it("has no defaultAggregated input — a delegated caller is never aggregated (always concrete tenant)", () => {
    // Structural guarantee: the resolver's signature cannot express "aggregated", so the audit page's
    // defaultAggregated flag can have no effect on a delegated ('MSP') admin.
    const seed = resolveDelegatedSeed({
      storedScope: null,
      managedTenantIds: MANAGED,
      firstManagedTenantId: MANAGED[0],
    });
    expect(seed).toBe("t-a");
    expect(seed).not.toBe("");
  });

  it("reuses a persisted selection when it is still inside the managed set", () => {
    expect(
      resolveDelegatedSeed({
        storedScope: "t-b",
        managedTenantIds: MANAGED,
        firstManagedTenantId: MANAGED[0],
      }),
    ).toBe("t-b");
  });

  it("falls back to the first managed tenant when the persisted selection left the managed set", () => {
    // Defence in depth: a stored tenant that is no longer delegated to this admin must not leak through.
    expect(
      resolveDelegatedSeed({
        storedScope: "t-revoked",
        managedTenantIds: MANAGED,
        firstManagedTenantId: MANAGED[0],
      }),
    ).toBe("t-a");
  });

  it("falls back to the first managed tenant when a GA's persisted aggregate ('') is inherited", () => {
    // "" is falsy → treated as no usable stored value; a delegated caller can never sit on the aggregate.
    expect(
      resolveDelegatedSeed({
        storedScope: "",
        managedTenantIds: MANAGED,
        firstManagedTenantId: MANAGED[0],
      }),
    ).toBe("t-a");
  });
});
