"use client";

import { useEffect, useRef } from "react";
import { Session } from "@/types";

interface SignalRApi {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  on: (event: string, handler: (...args: any[]) => void) => void;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  off: (event: string, handler: (...args: any[]) => void) => void;
  isConnected: boolean;
  joinGroup: (group: string) => Promise<void>;
  leaveGroup: (group: string) => Promise<void>;
}

interface UseProgressSignalRParams {
  session: Session | null;
  sessionRef: React.MutableRefObject<Session | null>;
  tenantId: string;
  signalR: SignalRApi;
  scheduleFetchEvents: (delayMs?: number) => void;
}

/**
 * Owns the progress page's SignalR integration:
 *  - joins tenant group once per connection
 *  - joins session-specific group when a session is selected
 *  - listens for newevents / newSession / eventStream → debounced refetch
 *  - cleans up groups + handlers on unmount / session change
 */
export function useProgressSignalR({
  session,
  sessionRef,
  tenantId,
  signalR,
  scheduleFetchEvents,
}: UseProgressSignalRParams): void {
  const { on, off, isConnected, joinGroup, leaveGroup } = signalR;
  const hasJoinedTenantGroup = useRef(false);

  useEffect(() => {
    if (isConnected && !hasJoinedTenantGroup.current) {
      joinGroup(`tenant-${tenantId}`);
      hasJoinedTenantGroup.current = true;
    }
    return () => {
      if (hasJoinedTenantGroup.current) {
        leaveGroup(`tenant-${tenantId}`);
        hasJoinedTenantGroup.current = false;
      }
    };
  }, [isConnected, tenantId, joinGroup, leaveGroup]);

  useEffect(() => {
    if (!isConnected || !session) return;

    const sessionGroup = `session-${session.tenantId}-${session.sessionId}`;
    console.log("[Progress] Joining session group:", sessionGroup);
    joinGroup(sessionGroup);

    return () => {
      console.log("[Progress] Leaving session group:", sessionGroup);
      leaveGroup(sessionGroup);
    };
  }, [isConnected, session?.sessionId, session?.tenantId, joinGroup, leaveGroup]);

  useEffect(() => {
    const scheduleRefetch = (source: string, sessionId: string) => {
      if (!sessionRef.current || sessionId !== sessionRef.current.sessionId) return;
      console.log(`[Progress] ${source} signal for current session, scheduling refetch`);
      scheduleFetchEvents(500);
    };

    const handleNewEvents = (data: { sessionId: string }) => {
      scheduleRefetch("newevents", data.sessionId);
    };
    const handleEventStream = (data: { sessionId: string }) => {
      scheduleRefetch("eventStream", data.sessionId);
    };

    on("newevents", handleNewEvents);
    on("newSession", handleNewEvents);
    on("eventStream", handleEventStream);
    return () => {
      off("newevents", handleNewEvents);
      off("newSession", handleNewEvents);
      off("eventStream", handleEventStream);
    };
  }, [on, off, sessionRef, scheduleFetchEvents]);
}
