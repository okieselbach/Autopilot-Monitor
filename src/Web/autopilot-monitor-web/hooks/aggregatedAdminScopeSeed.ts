/**
 * Pure seed-resolution core for {@link useAggregatedAdminScope}. Kept free of any React / context imports
 * so the seed PRECEDENCE (the regression-prone part of the hook's state machine) is unit-testable in the
 * node test environment without a DOM. The hook wires the live inputs; these functions decide the value.
 */

/**
 * GA/Reader seed resolver. Precedence: a first-init `?tenantId=` deep-link wins, else the tab-persisted
 * selection ("" = the aggregated view is a valid persisted value for a GA), else the page default — own
 * tenant, or "" when {@link params.defaultAggregated} is set (the audit page).
 */
export function resolveGaSeed(params: {
  firstInit: boolean;
  urlTenantId: string | undefined;
  storedScope: string | null;
  ownTenantId: string;
  defaultAggregated: boolean;
}): string {
  const { firstInit, urlTenantId, storedScope, ownTenantId, defaultAggregated } = params;
  if (firstInit && urlTenantId) return urlTenantId;
  if (storedScope !== null) return storedScope;
  return defaultAggregated ? "" : ownTenantId;
}

/**
 * Delegated ("MSP") seed resolver — a delegated caller is NEVER aggregated, so there is no
 * `defaultAggregated` input here: the result is always a concrete managed tenant. Reuses the tab-persisted
 * selection only when it is still inside the managed set, else falls back to the first managed tenant.
 */
export function resolveDelegatedSeed(params: {
  storedScope: string | null;
  managedTenantIds: string[];
  firstManagedTenantId: string;
}): string {
  const { storedScope, managedTenantIds, firstManagedTenantId } = params;
  const storedManaged = storedScope && managedTenantIds.includes(storedScope) ? storedScope : null;
  return storedManaged ?? firstManagedTenantId;
}
