import { describe, it, expect } from "vitest";
import { extractContinuation, MAX_EAGER_PAGES } from "../paginationLink";

describe("extractContinuation", () => {
  it("returns null for empty input", () => {
    expect(extractContinuation(null)).toBeNull();
    expect(extractContinuation(undefined)).toBeNull();
    expect(extractContinuation("")).toBeNull();
  });

  it("extracts the continuation param from a relative nextLink", () => {
    const link =
      "/api/sessions/abc/events?pageSize=200&continuation=eyJ0IjoiVE9LRU4ifQ&tenantId=tid";
    expect(extractContinuation(link)).toBe("eyJ0IjoiVE9LRU4ifQ");
  });

  it("extracts the continuation param from an absolute nextLink", () => {
    const link =
      "https://api.example.com/api/sessions/abc/events?pageSize=200&continuation=NEXT&tenantId=tid";
    expect(extractContinuation(link)).toBe("NEXT");
  });

  it("returns null when nextLink has no continuation param", () => {
    expect(extractContinuation("/api/sessions/abc/events?pageSize=200")).toBeNull();
  });

  it("returns null for malformed input", () => {
    // URL constructor with placeholder origin actually accepts most strings — the
    // fallback path is hit only when it really cannot parse anything sensible.
    expect(extractContinuation("https://[invalid")).toBeNull();
  });
});

describe("MAX_EAGER_PAGES", () => {
  it("provides a sane upper bound on the eager-fetch loop", () => {
    // The bound primarily guards against accidental backend-pagination cycles.
    // 200 pages × 200 events = 40k events on a single timeline — beyond which
    // the UI would be unresponsive anyway and we want the loop to bail out.
    expect(MAX_EAGER_PAGES).toBeGreaterThanOrEqual(50);
    expect(MAX_EAGER_PAGES).toBeLessThanOrEqual(1000);
  });
});

// ----------------------------------------------------------------------------
// Pattern-A eager-fetch loop semantics — exercised against a stand-in fetcher
// so the test does not depend on the React hook plumbing. The contract under
// test is exactly what useSessionEvents enforces:
//   1. First call uses pageSize=200 with no continuation
//   2. While the response carries a nextLink, follow it (extract continuation)
//   3. Append batches to the rendered list
//   4. Stop as soon as nextLink is absent
//   5. Bound by MAX_EAGER_PAGES to prevent runaway loops
// ----------------------------------------------------------------------------

interface FakeResponse {
  events: Array<{ sequence: number }>;
  nextLink: string | null;
}

async function eagerFetchLoop(
  fetcher: (continuation: string | null) => Promise<FakeResponse>,
  maxPages = MAX_EAGER_PAGES,
): Promise<{ events: Array<{ sequence: number }>; pages: number; truncated: boolean }> {
  const collected: Array<{ sequence: number }> = [];
  let continuation: string | null = null;
  let pages = 0;
  while (pages < maxPages) {
    const data = await fetcher(continuation);
    collected.push(...data.events);
    pages++;
    const next = extractContinuation(data.nextLink);
    if (!next) {
      return { events: collected, pages, truncated: false };
    }
    continuation = next;
  }
  return { events: collected, pages, truncated: true };
}

describe("eager-fetch loop", () => {
  it("terminates when nextLink is absent (single-page session)", async () => {
    const fetcher = async (): Promise<FakeResponse> => ({
      events: [{ sequence: 1 }, { sequence: 2 }, { sequence: 3 }],
      nextLink: null,
    });
    const result = await eagerFetchLoop(fetcher);
    expect(result.pages).toBe(1);
    expect(result.events).toHaveLength(3);
    expect(result.truncated).toBe(false);
  });

  it("follows nextLink across multiple pages and stops when absent", async () => {
    const pages: FakeResponse[] = [
      { events: [{ sequence: 1 }, { sequence: 2 }], nextLink: "/x?continuation=p2" },
      { events: [{ sequence: 3 }, { sequence: 4 }], nextLink: "/x?continuation=p3" },
      { events: [{ sequence: 5 }], nextLink: null },
    ];
    let calls = 0;
    const fetcher = async (cont: string | null): Promise<FakeResponse> => {
      // continuation must be carried forward correctly
      if (calls === 0) expect(cont).toBeNull();
      if (calls === 1) expect(cont).toBe("p2");
      if (calls === 2) expect(cont).toBe("p3");
      return pages[calls++];
    };
    const result = await eagerFetchLoop(fetcher);
    expect(result.pages).toBe(3);
    expect(result.events.map(e => e.sequence)).toEqual([1, 2, 3, 4, 5]);
    expect(result.truncated).toBe(false);
  });

  it("bails out after maxPages when backend would loop forever", async () => {
    // Pathological backend that always returns a continuation — defends against
    // any future pagination bug that could otherwise hang the timeline page.
    const fetcher = async (): Promise<FakeResponse> => ({
      events: [{ sequence: 0 }],
      nextLink: "/x?continuation=loop",
    });
    const result = await eagerFetchLoop(fetcher, 5);
    expect(result.pages).toBe(5);
    expect(result.truncated).toBe(true);
  });

  it("treats empty nextLink string as terminal", async () => {
    const fetcher = async (): Promise<FakeResponse> => ({
      events: [{ sequence: 1 }],
      nextLink: "",
    });
    const result = await eagerFetchLoop(fetcher);
    expect(result.pages).toBe(1);
    expect(result.truncated).toBe(false);
  });
});
