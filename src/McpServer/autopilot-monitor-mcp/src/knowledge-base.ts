/**
 * Loads analysis rules, gather rules, and IME log patterns from the rules/
 * directory, computes embeddings via a local transformer model, and populates
 * the in-memory vector store for semantic search.
 */

import { readdir, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pipeline, type FeatureExtractionPipeline } from '@xenova/transformers';
import { VectorStore, type VectorDocument } from './vector-store.js';

const MODEL_NAME = 'Xenova/all-MiniLM-L6-v2';

let embedder: FeatureExtractionPipeline | null = null;

async function getEmbedder(): Promise<FeatureExtractionPipeline> {
  if (!embedder) {
    embedder = await pipeline('feature-extraction', MODEL_NAME, {
      quantized: true,
    }) as FeatureExtractionPipeline;
  }
  return embedder;
}

/** Compute a normalized embedding vector for a text string. */
export async function embed(text: string): Promise<number[]> {
  const model = await getEmbedder();
  const output = await model(text, { pooling: 'mean', normalize: true });
  return Array.from(output.data as Float32Array);
}

/** Batch-embed multiple texts (sequential to avoid OOM on large batches). */
async function embedBatch(texts: string[]): Promise<number[][]> {
  const model = await getEmbedder();
  const results: number[][] = [];
  // Process in small batches to balance speed and memory
  const batchSize = 16;
  for (let i = 0; i < texts.length; i += batchSize) {
    const batch = texts.slice(i, i + batchSize);
    const outputs = await Promise.all(
      batch.map(async (t) => {
        const out = await model(t, { pooling: 'mean', normalize: true });
        return Array.from(out.data as Float32Array);
      })
    );
    results.push(...outputs);
  }
  return results;
}

// ──────────────────────────────────────────────────────────────
// Rule / pattern loading helpers
// ──────────────────────────────────────────────────────────────

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

/** Build the searchable text for each document type. */

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

// ──────────────────────────────────────────────────────────────
// Public API
// ──────────────────────────────────────────────────────────────

export async function buildKnowledgeBase(rulesRoot: string): Promise<VectorStore> {
  const store = new VectorStore();

  console.error('Loading knowledge base documents…');

  const [analyzeRules, gatherRules, imePatterns] = await Promise.all([
    loadJsonFiles<AnalyzeRule>(join(rulesRoot, 'analyze')),
    loadJsonFiles<GatherRule>(join(rulesRoot, 'gather')),
    loadJsonFiles<ImeLogPattern>(join(rulesRoot, 'ime-log-patterns')),
  ]);

  const docs: Array<{ id: string; text: string; metadata: Record<string, unknown> }> = [];

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

  console.error(`Embedding ${docs.length} documents (model: ${MODEL_NAME})…`);

  const texts = docs.map((d) => d.text);
  const embeddings = await embedBatch(texts);

  const vectorDocs: VectorDocument[] = docs.map((d, i) => ({
    ...d,
    embedding: embeddings[i],
  }));

  store.addMany(vectorDocs);
  console.error(`Knowledge base ready — ${store.size} documents indexed.`);

  return store;
}
