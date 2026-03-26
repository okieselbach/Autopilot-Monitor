/**
 * Loads analysis rules, gather rules, and IME log patterns from the rules/
 * directory and indexes them into a SearchProvider for semantic or fuzzy search.
 *
 * This module is backend-agnostic — it only deals with document loading and
 * text preparation. The actual search strategy is determined by the provider.
 */

import { readdir, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import type { SearchDocument } from './search-provider.js';

// ── Rule / pattern types ─────────────────────────────────────

interface AnalyzeRule {
  ruleId: string;
  title: string;
  description: string;
  severity: string;
  category: string;
  explanation?: string;
  remediation?: Array<{ title: string; steps: string[] }>;
  tags?: string[];
}

interface GatherRule {
  ruleId: string;
  title: string;
  description: string;
  category: string;
  collectorType?: string;
  target?: string;
  trigger?: string;
  tags?: string[];
}

interface ImeLogPattern {
  patternId: string;
  category: string;
  description: string;
  pattern: string;
  action?: string;
}

// ── File helpers ─────────────────────────────────────────────

async function loadJsonFiles<T>(dir: string): Promise<T[]> {
  let entries: string[];
  try {
    entries = await readdir(dir);
  } catch {
    return [];
  }
  const results: T[] = [];
  for (const f of entries) {
    if (!f.endsWith('.json')) continue;
    try {
      const raw = await readFile(join(dir, f), 'utf-8');
      results.push(JSON.parse(raw) as T);
    } catch {
      // skip malformed files
    }
  }
  return results;
}

// ── Text builders ────────────────────────────────────────────

function analyzeRuleText(r: AnalyzeRule): string {
  const parts = [
    `[${r.ruleId}] ${r.title}`,
    r.description,
    r.explanation ?? '',
  ];
  if (r.remediation) {
    for (const rem of r.remediation) {
      parts.push(`Remediation: ${rem.title} — ${rem.steps.join('; ')}`);
    }
  }
  if (r.tags?.length) parts.push(`Tags: ${r.tags.join(', ')}`);
  return parts.filter(Boolean).join('\n');
}

function gatherRuleText(r: GatherRule): string {
  return [
    `[${r.ruleId}] ${r.title}`,
    r.description,
    r.target ? `Target: ${r.target}` : '',
    r.tags?.length ? `Tags: ${r.tags.join(', ')}` : '',
  ].filter(Boolean).join('\n');
}

function imePatternText(p: ImeLogPattern): string {
  return [
    `[${p.patternId}] ${p.description}`,
    `Category: ${p.category}`,
    p.action ? `Action: ${p.action}` : '',
  ].filter(Boolean).join('\n');
}

// ── Public API ───────────────────────────────────────────────

/**
 * Load all rule/pattern documents from the rules directory.
 * Returns SearchDocument[] ready to be fed into any SearchProvider.
 */
export async function loadKnowledgeDocs(rulesRoot: string): Promise<SearchDocument[]> {
  const [analyzeRules, gatherRules, imePatterns] = await Promise.all([
    loadJsonFiles<AnalyzeRule>(join(rulesRoot, 'analyze')),
    loadJsonFiles<GatherRule>(join(rulesRoot, 'gather')),
    loadJsonFiles<ImeLogPattern>(join(rulesRoot, 'ime-log-patterns')),
  ]);

  const docs: SearchDocument[] = [];

  for (const r of analyzeRules) {
    docs.push({
      id: r.ruleId,
      text: analyzeRuleText(r),
      metadata: {
        type: 'analyze-rule',
        severity: r.severity,
        category: r.category,
        title: r.title,
        tags: r.tags ?? [],
      },
    });
  }

  for (const r of gatherRules) {
    docs.push({
      id: r.ruleId,
      text: gatherRuleText(r),
      metadata: {
        type: 'gather-rule',
        category: r.category,
        title: r.title,
        tags: r.tags ?? [],
      },
    });
  }

  for (const p of imePatterns) {
    docs.push({
      id: p.patternId,
      text: imePatternText(p),
      metadata: {
        type: 'ime-log-pattern',
        category: p.category,
        description: p.description,
        action: p.action ?? null,
      },
    });
  }

  return docs;
}
