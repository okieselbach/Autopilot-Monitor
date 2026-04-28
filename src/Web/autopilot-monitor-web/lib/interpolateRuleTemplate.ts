/**
 * Replaces `{{token}}` placeholders in a rule's explanation/remediation text
 * with values pulled from the rule's `matchedConditions` evidence map.
 *
 * Resolution order for `{{token}}`:
 *   1. The first matchedConditions entry whose `field` equals `token`
 *      (e.g. {{uefiCA2023Status}} -> entry where field === "uefiCA2023Status").
 *   2. A matchedConditions entry whose key (signal name) equals `token`
 *      (e.g. {{ca2023_not_updated}} -> that signal's value).
 *   3. Unresolved tokens are left untouched so authors notice the typo.
 *
 * Token chars: [a-zA-Z0-9_]. Whitespace inside the braces is tolerated.
 */
export function interpolateRuleTemplate(
  text: string | null | undefined,
  matchedConditions: Record<string, unknown> | null | undefined
): string {
  if (!text) return text ?? "";
  if (!matchedConditions || typeof matchedConditions !== "object") return text;

  const byField = new Map<string, string>();
  const bySignal = new Map<string, string>();

  for (const [signal, evidence] of Object.entries(matchedConditions)) {
    if (!evidence || typeof evidence !== "object") {
      if (evidence != null) bySignal.set(signal, formatValue(evidence));
      continue;
    }
    const e = evidence as Record<string, unknown>;
    if (typeof e.field === "string" && "value" in e) {
      byField.set(e.field, formatValue(e.value));
    }
    if ("value" in e) {
      bySignal.set(signal, formatValue(e.value));
    }
  }

  return text.replace(/\{\{\s*([a-zA-Z0-9_]+)\s*\}\}/g, (raw, token: string) => {
    if (byField.has(token)) return byField.get(token)!;
    if (bySignal.has(token)) return bySignal.get(token)!;
    return raw;
  });
}

function formatValue(v: unknown): string {
  if (v == null) return "";
  if (typeof v === "string") return v;
  if (typeof v === "number" || typeof v === "boolean") return String(v);
  try {
    return JSON.stringify(v);
  } catch {
    return String(v);
  }
}
