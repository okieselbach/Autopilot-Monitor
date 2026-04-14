"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { isGuid } from "@/utils/inputValidation";
import { EnrollmentEvent, Session } from "@/types";
import type { NotificationType } from "@/contexts/NotificationContext";

type AddNotification = (
  type: NotificationType,
  title: string,
  message: string,
  key?: string,
  href?: string,
) => void;

interface UseSessionEventsParams {
  sessionId: string;
  sessionTenantId: string | null;
  resolveEffectiveTenantId: () => string | null;
  sessionRef: React.MutableRefObject<Session | null>;
  sessionIdRef: React.MutableRefObject<string>;
  fetchSessionDetails: () => Promise<void>;
  setLoading: React.Dispatch<React.SetStateAction<boolean>>;
  isConnected: boolean;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  addNotification: AddNotification;
}

export interface UseSessionEventsReturn {
  events: EnrollmentEvent[];
  setEvents: React.Dispatch<React.SetStateAction<EnrollmentEvent[]>>;
  fetchEvents: () => Promise<void>;
  scheduleFetchEvents: (delayMs?: number) => void;
}

/**
 * Owns the session detail page's event list lifecycle:
 *  - fetch events from Table Storage (canonical truth)
 *  - in-flight dedup (SignalR + 30s timer + group-join can overlap)
 *  - debounced scheduleFetchEvents to absorb bursts
 *  - empty-refresh guard: ignores transient empty lists, keeps last known-good
 *  - terminal-event detection: triggers session refetch if SignalR status delta was lost
 *  - triggers initial fetch once sessionTenantId is known
 *  - 30s fallback polling while SignalR disconnected (visible tab only)
 */
export function useSessionEvents({
  sessionId,
  sessionTenantId,
  resolveEffectiveTenantId,
  sessionRef,
  sessionIdRef,
  fetchSessionDetails,
  setLoading,
  isConnected,
  getAccessToken,
  addNotification,
}: UseSessionEventsParams): UseSessionEventsReturn {
  const [events, setEvents] = useState<EnrollmentEvent[]>([]);

  // Debounce real-time event refreshes to avoid burst reads in Table Storage.
  const eventRefreshTimeoutRef = useRef<NodeJS.Timeout | null>(null);
  // Deduplication: track in-flight fetchEvents to avoid concurrent calls
  const fetchEventsInFlight = useRef(false);
  const fetchEventsQueued = useRef(false);

  const fetchEvents = useCallback(async () => {
    // Deduplication: if a fetch is already in flight, queue one follow-up instead of
    // stacking concurrent requests (SignalR signal + 30s timer + group-join can overlap).
    if (fetchEventsInFlight.current) {
      fetchEventsQueued.current = true;
      return;
    }
    const effectiveTenantId = resolveEffectiveTenantId();
    if (!effectiveTenantId || !isGuid(effectiveTenantId)) {
      return;
    }
    fetchEventsInFlight.current = true;
    fetchEventsQueued.current = false;

    try {
      const response = await authenticatedFetch(
        api.sessions.events(sessionId, effectiveTenantId),
        getAccessToken
      );
      if (response.ok) {
        const data = await response.json();
        // Replace entire event list with fresh data from Table Storage (canonical truth).
        // No merge needed — fetchEvents() is the single source; SignalR only signals.
        // phaseName is computed at render time in eventsByPhase useMemo (no race condition).
        const fetchedEvents = Array.isArray(data.events) ? data.events : [];
        setEvents(prevEvents => {
          // Keep the last known-good snapshot if backend transiently returns an empty list.
          if (fetchedEvents.length === 0 && prevEvents.length > 0) {
            console.warn(
              `[SessionDetail] Ignoring empty event refresh for session ${sessionIdRef.current} (tenant ${effectiveTenantId}); keeping ${prevEvents.length} cached events`
            );
            return prevEvents;
          }
          return fetchedEvents;
        });

        // Surgical status-stale detection: if events contain a terminal event but session
        // status is still InProgress, the SignalR status delta was likely lost. Refetch
        // session details from the DB (single read, only when truly needed).
        const currentStatus = sessionRef.current?.status;
        if (currentStatus && currentStatus !== "Succeeded" && currentStatus !== "Failed") {
          const hasTerminalEvent = fetchedEvents.some(
            (e: EnrollmentEvent) => e.eventType === "enrollment_complete" || e.eventType === "enrollment_failed"
          );
          if (hasTerminalEvent) {
            console.info(
              `[SessionDetail] Terminal event detected but session status is '${currentStatus}' — refetching session details`
            );
            fetchSessionDetails();
          }
        }
      } else {
        addNotification('error', 'Backend Error', `Failed to load session events: ${response.statusText}`, 'session-events-fetch-error');
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error("Failed to fetch events:", error);
        addNotification('error', 'Backend Not Reachable', 'Unable to load session events. Please check your connection.', 'session-events-fetch-error');
      }
    } finally {
      setLoading(false);
      fetchEventsInFlight.current = false;

      // If another fetch was requested while we were in flight, run it now
      if (fetchEventsQueued.current) {
        fetchEventsQueued.current = false;
        fetchEvents();
      }
    }
  }, [sessionId, resolveEffectiveTenantId, sessionRef, sessionIdRef, fetchSessionDetails, setLoading, getAccessToken, addNotification]);

  const scheduleFetchEvents = useCallback((delayMs = 300) => {
    if (eventRefreshTimeoutRef.current) {
      clearTimeout(eventRefreshTimeoutRef.current);
    }
    eventRefreshTimeoutRef.current = setTimeout(() => {
      fetchEvents();
    }, delayMs);
  }, [fetchEvents]);

  // Fetch events when we have the session's tenant ID
  useEffect(() => {
    if (sessionTenantId && sessionId) {
      fetchEvents();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionTenantId, sessionId]);

  // Clear debounce timer on unmount
  useEffect(() => {
    return () => {
      if (eventRefreshTimeoutRef.current) {
        clearTimeout(eventRefreshTimeoutRef.current);
      }
    };
  }, []);

  // Fallback polling only while SignalR is disconnected.
  useEffect(() => {
    const effectiveTenantId = resolveEffectiveTenantId();
    if (!sessionId || !effectiveTenantId || isConnected) return;
    const interval = setInterval(() => {
      if (document.visibilityState === "visible") fetchEvents();
    }, 30_000);
    return () => clearInterval(interval);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId, sessionTenantId, isConnected]);

  return { events, setEvents, fetchEvents, scheduleFetchEvents };
}
