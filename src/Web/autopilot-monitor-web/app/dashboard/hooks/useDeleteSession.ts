"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { trackEvent } from "@/lib/appInsights";
import { NotificationType } from "@/contexts/NotificationContext";
import { useSignalR } from "@/contexts/SignalRContext";
import { classifyDeleteResponse, classifyPollingResponse, dispatchSessionDeleted } from "./deleteSessionResponse";

interface DeleteTarget {
  sessionId: string;
  tenantId: string;
  deviceName?: string;
}

export function useDeleteSession(
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>,
  addNotification: (type: NotificationType, title: string, message: string, key?: string, href?: string) => void,
  adminMode: boolean,
  onSessionDeleted: (sessionId: string) => void
) {
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [sessionToDelete, setSessionToDelete] = useState<DeleteTarget | null>(null);

  // V2 cascade path: sessions awaiting the worker's `sessionDeleted` SignalR push. Surfaced
  // to consumers so the dashboard table can render a per-row spinner (plan §5 PR5: "show
  // 'deletion queued' toast + spinner on the session row until SignalR notification arrives").
  const [pendingDeletions, setPendingDeletions] = useState<Set<string>>(new Set());
  // Reverse lookup so the SignalR handler can leave the per-session group it joined.
  const pendingTenantsRef = useRef<Map<string, string>>(new Map());

  const { on, off, isConnected, joinGroup, leaveGroup } = useSignalR();

  const removePending = useCallback((sessionId: string) => {
    setPendingDeletions((prev) => {
      if (!prev.has(sessionId)) return prev;
      const next = new Set(prev);
      next.delete(sessionId);
      return next;
    });
    const tenantId = pendingTenantsRef.current.get(sessionId);
    pendingTenantsRef.current.delete(sessionId);
    if (tenantId && isConnected) {
      // Fire-and-forget leave; the SignalR layer no-ops if we're not in the group.
      leaveGroup(`session-${tenantId}-${sessionId}`).catch(() => { /* best-effort */ });
    }
  }, [isConnected, leaveGroup]);

  // Single SignalR subscription that dispatches by sessionId — one listener handles N
  // concurrent pending deletions without registering / unregistering per-session handlers.
  useEffect(() => {
    const handleSessionDeleted = (payload: unknown) => {
      const pendingIds = new Set(pendingTenantsRef.current.keys());
      const id = dispatchSessionDeleted(payload, pendingIds);
      if (!id) return;
      onSessionDeleted(id);
      removePending(id);
    };
    on("sessionDeleted", handleSessionDeleted);
    return () => off("sessionDeleted", handleSessionDeleted);
  }, [on, off, onSessionDeleted, removePending]);

  // Polling fallback for missed `sessionDeleted` events (plan §5 PR5 finding 3). SignalR
  // doesn't replay messages that fire while we're disconnected; the auto-reconnect rejoins
  // groups but does not back-fill events. Every 60s we re-fetch each pending session and
  // treat a 404 as "cascade completed". The interval is conservative (≤ 5 reqs/min/user with
  // a busy admin), well under the rate limit.
  useEffect(() => {
    if (pendingDeletions.size === 0) return;
    const intervalId = setInterval(async () => {
      for (const sessionId of Array.from(pendingTenantsRef.current.keys())) {
        const tenantId = pendingTenantsRef.current.get(sessionId);
        if (!tenantId) continue;
        try {
          const r = await authenticatedFetch(api.sessions.get(sessionId, tenantId), getAccessToken, { method: 'GET' });
          if (classifyPollingResponse(r.status) === 'deleted') {
            onSessionDeleted(sessionId);
            removePending(sessionId);
          }
          // 'wait' = row still there (cascade in progress or poisoned) — keep waiting.
          // Auth/rate-limit/5xx are also 'wait'; the next tick retries.
        } catch {
          // Network / auth blip — ignore, next tick retries. We never want to clear a
          // pending row on an inconclusive poll because the cascade may still be running.
        }
      }
    }, 60_000);
    return () => clearInterval(intervalId);
  }, [pendingDeletions, getAccessToken, onSessionDeleted, removePending]);

  const deleteSession = (sessionId: string, tenantId: string, deviceName?: string) => {
    setSessionToDelete({ sessionId, tenantId, deviceName });
    setShowDeleteConfirm(true);
  };

  const confirmDelete = async () => {
    if (!sessionToDelete) return;

    const { sessionId, tenantId } = sessionToDelete;

    try {
      const response = await authenticatedFetch(api.sessions.delete(sessionId, tenantId), getAccessToken, {
        method: 'DELETE',
      });

      // Always close the confirm dialog before any async branch — the user already committed.
      setShowDeleteConfirm(false);
      setSessionToDelete(null);

      const action = await classifyDeleteResponse(response, sessionId, tenantId);

      switch (action.kind) {
        case 'queued':
          setPendingDeletions((prev) => {
            const next = new Set(prev);
            next.add(action.sessionId);
            return next;
          });
          pendingTenantsRef.current.set(action.sessionId, action.tenantId);
          if (isConnected) {
            joinGroup(`session-${action.tenantId}-${action.sessionId}`).catch(() => { /* best-effort */ });
          }
          trackEvent("session_deletion_queued", { inAdminMode: adminMode, manifestId: action.manifestId ?? "" });
          addNotification(
            'info',
            'Deletion queued',
            'The cascade worker is draining this session. The row will disappear when it completes.',
            `session-delete-queued-${action.sessionId}`,
          );
          return;

        case 'immediate':
          trackEvent("session_deleted", { inAdminMode: adminMode });
          onSessionDeleted(action.sessionId);
          return;

        case 'conflict':
          addNotification('warning', action.title, action.message, `session-delete-conflict-${action.sessionId}`);
          return;

        case 'unavailable':
          addNotification(
            'warning',
            'Deletion temporarily unavailable',
            action.message,
            `session-delete-unavailable-${action.sessionId}`,
          );
          return;

        case 'notFound':
          // Already gone server-side — remove from the table to match reality.
          onSessionDeleted(action.sessionId);
          return;

        case 'error':
          addNotification('error', 'Delete failed', action.message, `session-delete-error-${action.sessionId}`);
          return;
      }
    } catch (error) {
      // Errors in the catch are network / auth failures, not HTTP-status branches.
      setShowDeleteConfirm(false);
      setSessionToDelete(null);

      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error('Failed to delete session:', error);
        addNotification('error', 'Delete failed', 'Unable to reach the backend.', 'session-delete-network-error');
      }
    }
  };

  const cancelDelete = () => {
    setShowDeleteConfirm(false);
    setSessionToDelete(null);
  };

  return {
    showDeleteConfirm,
    sessionToDelete,
    pendingDeletions,
    deleteSession,
    confirmDelete,
    cancelDelete,
  };
}
