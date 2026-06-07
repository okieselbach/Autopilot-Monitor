import type { useAggregatedAdminScope } from "@/hooks";

/** Global-admin tenant scope shared by every Software-hub tab (read values + setters). */
export type SoftwareTabScope = ReturnType<typeof useAggregatedAdminScope>;

export type TimeRange = "7d" | "30d" | "90d";

export function rangeToDays(range: TimeRange): number {
  return range === "7d" ? 7 : range === "30d" ? 30 : 90;
}
