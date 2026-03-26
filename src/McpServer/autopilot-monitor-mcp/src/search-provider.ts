/**
 * Abstract search provider interface.
 *
 * Implementations can use vector embeddings, fuzzy text matching, or any other
 * ranking strategy. The MCP tools program against this interface so the backend
 * is swappable without touching tool registration code.
 */

// ── Shared types ─────────────────────────────────────────────

export interface SearchDocument {
  id: string;
  text: string;
  metadata: Record<string, unknown>;
}

export interface SearchResult {
  id: string;
  text: string;
  metadata: Record<string, unknown>;
  /** Relevance score — always normalized to 0..1 regardless of backend. */
  score: number;
}

export interface SearchOptions {
  topK?: number;
  minScore?: number;
}

// ── Provider contract ────────────────────────────────────────

export interface SearchProvider {
  /** Human-readable backend name (e.g. "vector/all-MiniLM-L6-v2", "fuse"). */
  readonly name: string;

  /** Number of currently indexed documents. */
  readonly size: number;

  /**
   * Index a batch of documents. Can be called multiple times to add more docs.
   * Implementation may precompute embeddings, build a Fuse index, etc.
   */
  index(docs: SearchDocument[]): Promise<void>;

  /**
   * Search indexed documents by a natural-language query string.
   * Returns results sorted by descending relevance.
   */
  search(query: string, options?: SearchOptions): Promise<SearchResult[]>;
}

// ── Provider identifiers ─────────────────────────────────────

export type SearchBackend = 'vector' | 'fuse';
