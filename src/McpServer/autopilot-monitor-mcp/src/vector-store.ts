/**
 * Lightweight in-memory vector store using cosine similarity.
 * Each document is stored alongside its embedding vector.
 */

export interface VectorDocument {
  id: string;
  text: string;
  metadata: Record<string, unknown>;
  embedding: number[];
}

export interface SearchResult {
  id: string;
  text: string;
  metadata: Record<string, unknown>;
  score: number;
}

function cosineSimilarity(a: number[], b: number[]): number {
  let dot = 0;
  let normA = 0;
  let normB = 0;
  for (let i = 0; i < a.length; i++) {
    dot += a[i] * b[i];
    normA += a[i] * a[i];
    normB += b[i] * b[i];
  }
  const denom = Math.sqrt(normA) * Math.sqrt(normB);
  return denom === 0 ? 0 : dot / denom;
}

export class VectorStore {
  private documents: VectorDocument[] = [];

  add(doc: VectorDocument): void {
    this.documents.push(doc);
  }

  addMany(docs: VectorDocument[]): void {
    this.documents.push(...docs);
  }

  search(queryEmbedding: number[], topK = 5, minScore = 0.3): SearchResult[] {
    const scored = this.documents.map((doc) => ({
      id: doc.id,
      text: doc.text,
      metadata: doc.metadata,
      score: cosineSimilarity(queryEmbedding, doc.embedding),
    }));

    return scored
      .filter((r) => r.score >= minScore)
      .sort((a, b) => b.score - a.score)
      .slice(0, topK);
  }

  get size(): number {
    return this.documents.length;
  }
}
