import type { Session } from "../types";

export type UniqueValuesByField = Partial<Record<keyof Session, string[]>>;

export function buildUniqueValuesByField(
  sessions: Session[],
  fields: readonly (keyof Session)[],
): UniqueValuesByField {
  const sets = new Map<keyof Session, Set<string>>();
  for (const f of fields) sets.set(f, new Set<string>());

  for (const s of sessions) {
    for (const f of fields) {
      const v = s[f];
      if (v != null && v !== "") sets.get(f)!.add(String(v));
    }
  }

  const result: UniqueValuesByField = {};
  for (const f of fields) result[f] = [...sets.get(f)!].sort();
  return result;
}
