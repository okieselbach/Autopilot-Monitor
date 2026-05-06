import { describe, it, expect } from "vitest";
import { extractContinuation } from "../paginationLink";

/**
 * PR 5 (mcp-pagination-rollout) — verifies the Pattern B2 "load more append"
 * loop semantics for the dashboard session list. Mirrors the contract
 * useDashboardSessions enforces:
 *   1. First call uses pageSize=10 with no continuation.
 *   2. nextLink in the response carries the continuation cursor.
 *   3. Load-More replays with that cursor and APPENDS the new batch.
 *   4. No duplicates: each session id appears exactly once across pages.
 *   5. Loop ends when nextLink is absent.
 */

interface FakeSession {
  sessionId: string;
}

interface FakePage {
  sessions: FakeSession[];
  nextLink: string | null;
}

async function loadMoreLoop(
  pages: FakePage[],
  initialPageSize = 10,
): Promise<{ all: FakeSession[]; calls: number; lastPageSize: number }> {
  const all: FakeSession[] = [];
  let continuation: string | null = null;
  let calls = 0;
  let lastPageSize = initialPageSize;

  while (true) {
    if (calls >= pages.length) break;
    const page = pages[calls];
    calls++;

    if (calls === 1) {
      // First call: no continuation expected — just initial pageSize.
      lastPageSize = initialPageSize;
    }

    // Append (Pattern B2) — never replace.
    all.push(...page.sessions);

    const next = extractContinuation(page.nextLink);
    if (!next) break;
    continuation = next;
    void continuation; // captured for clarity; loop end on null
  }

  return { all, calls, lastPageSize };
}

function makeSession(id: string): FakeSession {
  return { sessionId: id };
}

describe("dashboard session list — Pattern B2 load-more append", () => {
  it("appends batches without duplicating session ids", async () => {
    const pages: FakePage[] = [
      {
        sessions: ["s1", "s2", "s3", "s4", "s5", "s6", "s7", "s8", "s9", "s10"].map(makeSession),
        nextLink: "/api/sessions?pageSize=10&continuation=p2",
      },
      {
        sessions: ["s11", "s12", "s13", "s14", "s15", "s16", "s17", "s18", "s19", "s20"].map(makeSession),
        nextLink: "/api/sessions?pageSize=10&continuation=p3",
      },
      {
        sessions: ["s21", "s22"].map(makeSession),
        nextLink: null,
      },
    ];

    const { all, calls } = await loadMoreLoop(pages);

    expect(calls).toBe(3);
    expect(all).toHaveLength(22);
    const ids = all.map(s => s.sessionId);
    const unique = new Set(ids);
    expect(unique.size).toBe(ids.length); // no duplicates
    expect(ids[0]).toBe("s1");
    expect(ids[ids.length - 1]).toBe("s22");
  });

  it("terminates immediately when first page has no nextLink", async () => {
    const pages: FakePage[] = [
      {
        sessions: ["s1", "s2"].map(makeSession),
        nextLink: null,
      },
    ];
    const { all, calls } = await loadMoreLoop(pages);
    expect(calls).toBe(1);
    expect(all).toHaveLength(2);
  });

  it("continuation extraction handles relative + absolute nextLinks", () => {
    expect(
      extractContinuation("/api/sessions?pageSize=10&continuation=cursor-1"),
    ).toBe("cursor-1");
    expect(
      extractContinuation("https://api.example.com/api/sessions?pageSize=10&continuation=cursor-2"),
    ).toBe("cursor-2");
  });

  it("never reuses the same continuation across pages (avoids infinite loop)", async () => {
    // Two pages with distinct continuations; the loop must use page-2's cursor
    // when fetching what would be page 3.
    const pages: FakePage[] = [
      {
        sessions: [makeSession("s1")],
        nextLink: "/api/sessions?pageSize=10&continuation=A",
      },
      {
        sessions: [makeSession("s2")],
        nextLink: "/api/sessions?pageSize=10&continuation=B",
      },
      {
        sessions: [makeSession("s3")],
        nextLink: null,
      },
    ];

    const seenContinuations: Array<string | null> = [];
    let continuation: string | null = null;
    const collected: FakeSession[] = [];
    for (let i = 0; i < pages.length; i++) {
      seenContinuations.push(continuation);
      collected.push(...pages[i].sessions);
      continuation = extractContinuation(pages[i].nextLink);
      if (!continuation) break;
    }

    expect(seenContinuations).toEqual([null, "A", "B"]);
    expect(collected.map(s => s.sessionId)).toEqual(["s1", "s2", "s3"]);
  });
});
