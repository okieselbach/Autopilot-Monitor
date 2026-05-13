/**
 * Pure response-classification logic extracted from `useDeleteSession` so the dispatch
 * branches (200 legacy / 202 V2 / 409 / 503 / other) can be unit-tested without React
 * rendering. Plan §5 PR5.
 *
 * The hook itself runs the side-effects (toast, SignalR group join, row removal); these
 * helpers describe *what* should happen for a given backend response.
 */

/**
 * Backend body shape for the V2 cascade 202 response.
 * Mirrors `DeleteSessionFunction.BuildV2ResponseBody` in the Backend.
 */
export interface DeleteQueuedBody {
  success?: boolean;
  status?: string;
  manifestId?: string;
  message?: string;
}

/** Backend body shape for 4xx/5xx responses — same producer outcome contract. */
export interface DeleteErrorBody {
  success?: boolean;
  message?: string;
  deletionState?: string;
  manifestId?: string;
  hint?: string;
}

export type DeleteResponseAction =
  | { kind: "immediate"; sessionId: string }
  | { kind: "queued"; sessionId: string; tenantId: string; manifestId: string | null }
  | { kind: "conflict"; sessionId: string; title: string; message: string; hint: string | null }
  | { kind: "unavailable"; sessionId: string; message: string }
  | { kind: "notFound"; sessionId: string; message: string }
  | { kind: "error"; sessionId: string; message: string };

/**
 * Classify a `Response` from `DELETE /api/sessions/{id}` into the action the UI should
 * take. Reads the JSON body for non-2xx + 202 to surface the backend's `hint` strings;
 * the body is allowed to be missing/malformed (defensive — the backend always sends one,
 * but the network may corrupt it).
 */
export async function classifyDeleteResponse(
  response: Response,
  sessionId: string,
  tenantId: string,
): Promise<DeleteResponseAction> {
  if (response.status === 202) {
    const body = await safeJson<DeleteQueuedBody>(response);
    return {
      kind: "queued",
      sessionId,
      tenantId,
      manifestId: body?.manifestId ?? null,
    };
  }

  if (response.ok) {
    return { kind: "immediate", sessionId };
  }

  const errorBody = await safeJson<DeleteErrorBody>(response);
  const message = errorBody?.message ?? `HTTP ${response.status}`;

  if (response.status === 409) {
    const hint = errorBody?.hint ?? null;
    const title = hint === "cascade_poisoned_use_restore"
      ? "Cascade poisoned"
      : "Cascade already in flight";
    return { kind: "conflict", sessionId, title, message, hint };
  }

  if (response.status === 503) {
    return { kind: "unavailable", sessionId, message };
  }

  if (response.status === 404) {
    return { kind: "notFound", sessionId, message };
  }

  return { kind: "error", sessionId, message };
}

/**
 * SignalR dispatch: given a `sessionDeleted` payload and the current pending set,
 * return the sessionId to remove or `null` if the payload should be ignored
 * (no payload, no sessionId, or sessionId not in the pending set).
 */
export function dispatchSessionDeleted(
  payload: unknown,
  pendingIds: ReadonlySet<string>,
): string | null {
  if (typeof payload !== "object" || payload === null) return null;
  const id = (payload as { sessionId?: unknown }).sessionId;
  if (typeof id !== "string" || id.length === 0) return null;
  return pendingIds.has(id) ? id : null;
}

/**
 * Polling fallback dispatch: given the status code from `GET /api/sessions/{id}`, decide
 * whether to treat the cascade as complete. 404 = row gone → "deleted". Anything else =
 * keep waiting; the next poll tick re-checks. We never clear a pending row on a non-404
 * (auth/rate-limit/transient errors should not trigger a false-positive removal).
 */
export type PollingDecision = "deleted" | "wait";

export function classifyPollingResponse(status: number): PollingDecision {
  return status === 404 ? "deleted" : "wait";
}

async function safeJson<T>(response: Response): Promise<T | null> {
  try {
    return (await response.json()) as T;
  } catch {
    return null;
  }
}
