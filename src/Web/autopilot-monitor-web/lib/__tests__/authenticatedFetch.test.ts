import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { authenticatedFetch, TokenExpiredError } from "../authenticatedFetch";

function mockResponse(status: number, body: unknown = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("authenticatedFetch", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("attaches Bearer token and returns the response on success", async () => {
    const getToken = vi.fn().mockResolvedValue("tok-1");
    (globalThis.fetch as ReturnType<typeof vi.fn>).mockResolvedValueOnce(mockResponse(200, { ok: true }));

    const res = await authenticatedFetch("https://api/x", getToken);

    expect(res.status).toBe(200);
    expect(getToken).toHaveBeenCalledTimes(1);
    const call = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls[0];
    const headers = call[1].headers as Headers;
    expect(headers.get("Authorization")).toBe("Bearer tok-1");
  });

  it("throws TokenExpiredError immediately when no token is available", async () => {
    const getToken = vi.fn().mockResolvedValue(null);

    await expect(authenticatedFetch("https://api/x", getToken)).rejects.toBeInstanceOf(TokenExpiredError);
    expect(globalThis.fetch).not.toHaveBeenCalled();
  });

  it("retries with forced fresh token on 401 and returns the retry response", async () => {
    const getToken = vi.fn()
      .mockResolvedValueOnce("stale")
      .mockResolvedValueOnce("fresh");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock
      .mockResolvedValueOnce(mockResponse(401))
      .mockResolvedValueOnce(mockResponse(200, { ok: true }));

    const res = await authenticatedFetch("https://api/x", getToken);

    expect(res.status).toBe(200);
    expect(getToken).toHaveBeenCalledTimes(2);
    expect(getToken).toHaveBeenNthCalledWith(2, true); // force refresh
    expect(fetchMock).toHaveBeenCalledTimes(2);
    const retryHeaders = fetchMock.mock.calls[1][1].headers as Headers;
    expect(retryHeaders.get("Authorization")).toBe("Bearer fresh");
  });

  it("throws TokenExpiredError when retry after 401 also returns 401", async () => {
    const getToken = vi.fn()
      .mockResolvedValueOnce("stale")
      .mockResolvedValueOnce("fresh");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock
      .mockResolvedValueOnce(mockResponse(401))
      .mockResolvedValueOnce(mockResponse(401));

    await expect(authenticatedFetch("https://api/x", getToken)).rejects.toBeInstanceOf(TokenExpiredError);
  });

  it("throws TokenExpiredError when forced refresh returns no token", async () => {
    const getToken = vi.fn()
      .mockResolvedValueOnce("stale")
      .mockResolvedValueOnce(null);
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(mockResponse(401));

    await expect(authenticatedFetch("https://api/x", getToken)).rejects.toBeInstanceOf(TokenExpiredError);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("passes through non-401 error responses without retry", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(mockResponse(500, { error: "boom" }));

    const res = await authenticatedFetch("https://api/x", getToken);

    expect(res.status).toBe(500);
    expect(getToken).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
