/**
 * Factory for creating SearchProvider instances.
 *
 * Selection logic:
 *   1. Explicit env var SEARCH_BACKEND=vector|fuse  →  use that
 *   2. Auto-detect: try to load @huggingface/transformers
 *      - available  →  vector
 *      - missing    →  fuse (graceful fallback)
 *
 * This allows running the MCP server without the ~23 MB model download
 * by setting SEARCH_BACKEND=fuse or simply not installing @huggingface/transformers.
 */

import type { SearchBackend, SearchProvider } from './search-provider.js';

async function isTransformersAvailable(): Promise<boolean> {
  try {
    await import('@huggingface/transformers');
    return true;
  } catch {
    return false;
  }
}

export async function resolveBackend(): Promise<SearchBackend> {
  const explicit = process.env.SEARCH_BACKEND?.toLowerCase();
  if (explicit === 'vector' || explicit === 'fuse') return explicit;

  return (await isTransformersAvailable()) ? 'vector' : 'fuse';
}

/**
 * Create a new SearchProvider instance.
 * Pass a backend explicitly, or let it auto-detect.
 */
export async function createSearchProvider(backend?: SearchBackend): Promise<SearchProvider> {
  const resolved = backend ?? await resolveBackend();

  if (resolved === 'vector') {
    const { VectorSearchProvider } = await import('./vector-search-provider.js');
    return new VectorSearchProvider();
  }

  const { FuseSearchProvider } = await import('./fuse-search-provider.js');
  return new FuseSearchProvider();
}
