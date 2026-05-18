import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import {
  extractScriptRefsFromEvents,
  fetchScriptDisplayNames,
  lookupScriptDisplayName,
  formatRefKey,
  SCRIPT_DISPLAY_NAMES_CHUNK_SIZE,
} from "../scriptDisplayNames";

function mockResponse(status: number, body: unknown = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("extractScriptRefsFromEvents", () => {
  it("returns empty array for non-script events", () => {
    const refs = extractScriptRefsFromEvents([
      { eventType: "agent_started", data: { foo: "bar" } },
      { eventType: "download_progress", data: {} },
    ]);
    expect(refs).toEqual([]);
  });

  it("collects distinct policyIds with default scriptType=platform", () => {
    const refs = extractScriptRefsFromEvents([
      { eventType: "script_started", data: { policyId: "abc" } },
      { eventType: "script_completed", data: { policyId: "abc" } }, // dedup
      { eventType: "script_started", data: { policyId: "def" } },
    ]);
    expect(refs).toEqual([
      { kind: "Platform", id: "abc" },
      { kind: "Platform", id: "def" },
    ]);
  });

  it("respects scriptType to pick the right ScriptKind", () => {
    const refs = extractScriptRefsFromEvents([
      { eventType: "script_completed", data: { policyId: "rem-1", scriptType: "remediation" } },
      { eventType: "script_started", data: { policy_id: "plat-2", script_type: "platform" } },
    ]);
    expect(refs).toContainEqual({ kind: "Remediation", id: "rem-1" });
    expect(refs).toContainEqual({ kind: "Platform", id: "plat-2" });
  });

  it("skips events without a policyId", () => {
    const refs = extractScriptRefsFromEvents([
      { eventType: "script_started", data: { foo: "bar" } },
      { eventType: "script_completed", data: null },
      { eventType: "script_failed", data: { policyId: "" } },
    ]);
    expect(refs).toEqual([]);
  });

  it("treats Platform:{id} and Remediation:{id} as distinct refs for the same id", () => {
    const refs = extractScriptRefsFromEvents([
      { eventType: "script_started", data: { policyId: "shared", scriptType: "platform" } },
      { eventType: "script_completed", data: { policyId: "shared", scriptType: "remediation" } },
    ]);
    expect(refs).toHaveLength(2);
  });
});

describe("fetchScriptDisplayNames (POST shape)", () => {
  const TENANT = "11111111-1111-1111-1111-111111111111";

  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("returns empty when no refs are passed (no HTTP call)", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    const result = await fetchScriptDisplayNames(TENANT, [], getToken);
    expect(result).toEqual({});
    expect(globalThis.fetch).not.toHaveBeenCalled();
  });

  it("POSTs the refs in the JSON body and returns the refs map", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    (globalThis.fetch as ReturnType<typeof vi.fn>).mockResolvedValueOnce(mockResponse(200, {
      refs: { "Platform:abc": "Bootstrap.ps1", "Remediation:rem-1": null },
    }));

    const result = await fetchScriptDisplayNames(
      TENANT,
      [{ kind: "Platform", id: "abc" }, { kind: "Remediation", id: "rem-1" }],
      getToken,
    );

    expect(result).toEqual({ "Platform:abc": "Bootstrap.ps1", "Remediation:rem-1": null });

    const call = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls[0];
    // URL is the bare endpoint (no refs= query string).
    expect(call[0]).toBe(`http://localhost:7071/api/tenants/${TENANT}/scripts/display-names`);
    // POST + JSON body with the refs array.
    expect(call[1].method).toBe("POST");
    const headers = call[1].headers as Headers;
    expect(headers.get("Content-Type")).toBe("application/json");
    const body = JSON.parse(call[1].body as string);
    expect(body.refs).toEqual(["Platform:abc", "Remediation:rem-1"]);
  });

  it("returns empty dict on non-2xx (transient failure)", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    (globalThis.fetch as ReturnType<typeof vi.fn>).mockResolvedValueOnce(mockResponse(500, { error: "boom" }));

    const result = await fetchScriptDisplayNames(
      TENANT,
      [{ kind: "Platform", id: "abc" }],
      getToken,
    );

    expect(result).toEqual({});
  });

  it("chunks refs above the backend cap and merges the responses", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");

    // 201 refs -> two POSTs (200 + 1).
    const refs = Array.from({ length: SCRIPT_DISPLAY_NAMES_CHUNK_SIZE + 1 }, (_, i) => ({
      kind: "Platform" as const,
      id: `id-${i}`,
    }));

    const firstChunkResponse: Record<string, string> = {};
    for (let i = 0; i < SCRIPT_DISPLAY_NAMES_CHUNK_SIZE; i++) {
      firstChunkResponse[`Platform:id-${i}`] = `name-${i}`;
    }
    const secondChunkResponse = { "Platform:id-200": "name-200" };

    (globalThis.fetch as ReturnType<typeof vi.fn>)
      .mockResolvedValueOnce(mockResponse(200, { refs: firstChunkResponse }))
      .mockResolvedValueOnce(mockResponse(200, { refs: secondChunkResponse }));

    const result = await fetchScriptDisplayNames(TENANT, refs, getToken);

    expect(globalThis.fetch).toHaveBeenCalledTimes(2);
    expect(Object.keys(result)).toHaveLength(SCRIPT_DISPLAY_NAMES_CHUNK_SIZE + 1);
    expect(result["Platform:id-0"]).toBe("name-0");
    expect(result["Platform:id-200"]).toBe("name-200");

    // Verify chunk boundaries: first POST body has 200 refs, second has 1.
    const firstBody = JSON.parse((globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls[0][1].body as string);
    const secondBody = JSON.parse((globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls[1][1].body as string);
    expect(firstBody.refs).toHaveLength(SCRIPT_DISPLAY_NAMES_CHUNK_SIZE);
    expect(secondBody.refs).toEqual(["Platform:id-200"]);
  });

  it("partial success: keeps results from earlier chunks when a later one fails", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");

    const refs = Array.from({ length: SCRIPT_DISPLAY_NAMES_CHUNK_SIZE + 1 }, (_, i) => ({
      kind: "Platform" as const,
      id: `id-${i}`,
    }));

    (globalThis.fetch as ReturnType<typeof vi.fn>)
      .mockResolvedValueOnce(mockResponse(200, { refs: { "Platform:id-0": "first-batch-ok" } }))
      .mockResolvedValueOnce(mockResponse(503, { error: "throttled" }));

    const result = await fetchScriptDisplayNames(TENANT, refs, getToken);

    // Don't discard the work the first chunk already did.
    expect(result["Platform:id-0"]).toBe("first-batch-ok");
    // Anything from the failed chunk is just absent (caller falls back to ID).
    expect(result["Platform:id-200"]).toBeUndefined();
  });

  it("partial success: preserves earlier chunks when a later one THROWS (network/abort)", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");

    const refs = Array.from({ length: SCRIPT_DISPLAY_NAMES_CHUNK_SIZE + 1 }, (_, i) => ({
      kind: "Platform" as const,
      id: `id-${i}`,
    }));

    (globalThis.fetch as ReturnType<typeof vi.fn>)
      .mockResolvedValueOnce(mockResponse(200, { refs: { "Platform:id-0": "first-batch-ok" } }))
      // Second chunk: simulate a network abort / fetch rejection.
      .mockRejectedValueOnce(new TypeError("Failed to fetch"));

    // Must not throw — the merged-so-far map should come back, with the failed chunk's
    // ids simply absent. Earlier behaviour bubbled the network error to the hook, which
    // then discarded the successful first chunk too.
    const result = await fetchScriptDisplayNames(TENANT, refs, getToken);

    expect(result["Platform:id-0"]).toBe("first-batch-ok");
    expect(result["Platform:id-200"]).toBeUndefined();
  });

  it("propagates TokenExpiredError from any chunk so the hook can react to auth expiry", async () => {
    const { TokenExpiredError } = await import("../authenticatedFetch");
    const getToken = vi.fn().mockResolvedValue(null); // null token -> authenticatedFetch throws

    const refs = [{ kind: "Platform" as const, id: "id-0" }];

    await expect(fetchScriptDisplayNames(TENANT, refs, getToken)).rejects.toBeInstanceOf(TokenExpiredError);
  });
});

describe("lookupScriptDisplayName", () => {
  it("returns null for unknown ref", () => {
    const map = { "Platform:abc": "Found" };
    expect(lookupScriptDisplayName(map, "platform", "def")).toBeNull();
  });

  it("distinguishes Platform vs Remediation under the same id", () => {
    const map = {
      "Platform:shared": "Platform Name",
      "Remediation:shared": "Remediation Name",
    };
    expect(lookupScriptDisplayName(map, "platform", "shared")).toBe("Platform Name");
    expect(lookupScriptDisplayName(map, "remediation", "shared")).toBe("Remediation Name");
  });

  it("treats unknown scriptType as Platform (safer default for legacy events)", () => {
    const map = { "Platform:abc": "X" };
    expect(lookupScriptDisplayName(map, undefined, "abc")).toBe("X");
    expect(lookupScriptDisplayName(map, "", "abc")).toBe("X");
  });

  it("returns null when map or policyId missing", () => {
    expect(lookupScriptDisplayName(undefined, "platform", "abc")).toBeNull();
    expect(lookupScriptDisplayName({}, "platform", undefined)).toBeNull();
  });
});

describe("formatRefKey", () => {
  it("formats canonical Kind:Id", () => {
    expect(formatRefKey({ kind: "Platform", id: "abc" })).toBe("Platform:abc");
    expect(formatRefKey({ kind: "Remediation", id: "rem-1" })).toBe("Remediation:rem-1");
  });

  it("produces a stable, sortable refset fingerprint from any ordering", () => {
    // The hook's internal refsKey is built by `.map(formatRefKey).sort().join("|")`.
    // Asserting the building blocks here keeps the hook's stable-key contract honest
    // without needing a full React renderer.
    const a = [{ kind: "Platform" as const, id: "two" }, { kind: "Platform" as const, id: "one" }];
    const b = [{ kind: "Platform" as const, id: "one" }, { kind: "Platform" as const, id: "two" }];
    const fingerprint = (refs: typeof a) => refs.map(formatRefKey).sort().join("|");
    expect(fingerprint(a)).toBe(fingerprint(b));
    expect(fingerprint(a)).toBe("Platform:one|Platform:two");
  });
});
