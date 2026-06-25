/**
 * Per-tenant health summary for one managed tenant — the headline subset of the server-aggregated
 * DashboardStats returned by `/global/stats/sessions?tenantId=`. Pure data; no React.
 */
export interface FleetSummary {
  activeCount: number;
  totalLastNDays: number;
  succeededLastNDays: number;
  failedLastNDays: number;
  successRatePct: number;
}

/** Fleet-wide roll-up across the managed tenants that have reported a summary. */
export interface FleetRollup {
  /** Number of tenants contributing to the roll-up (those with a loaded summary). */
  tenantCount: number;
  activeCount: number;
  totalLastNDays: number;
  succeededLastNDays: number;
  failedLastNDays: number;
  /** Weighted success rate across the fleet (sum succeeded / sum total), 1 decimal. 0 when no sessions. */
  successRatePct: number;
}

/**
 * Aggregates per-tenant summaries into a fleet roll-up. The success rate is WEIGHTED by session volume
 * (sum of succeeded / sum of total) — not an average of per-tenant rates — so a tenant with 1000 sessions
 * counts more than one with 3. Pure + testable.
 */
export function computeFleetRollup(summaries: FleetSummary[]): FleetRollup {
  const acc = summaries.reduce(
    (a, s) => {
      a.activeCount += s.activeCount;
      a.totalLastNDays += s.totalLastNDays;
      a.succeededLastNDays += s.succeededLastNDays;
      a.failedLastNDays += s.failedLastNDays;
      return a;
    },
    { activeCount: 0, totalLastNDays: 0, succeededLastNDays: 0, failedLastNDays: 0 }
  );

  const successRatePct =
    acc.totalLastNDays > 0
      ? Math.round((acc.succeededLastNDays / acc.totalLastNDays) * 1000) / 10
      : 0;

  return { tenantCount: summaries.length, ...acc, successRatePct };
}
