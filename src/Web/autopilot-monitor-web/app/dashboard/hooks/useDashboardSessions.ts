"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { asGuidOrUndefined } from "@/utils/inputValidation";
import type { NotificationType } from "@/contexts/NotificationContext";
import type { Session } from "../types";

type AddNotification = (
  type: NotificationType,
  title: string,
  message: string,
  key?: string,
  href?: string,
) => void;

interface User {
  isTenantAdmin?: boolean;
  isGlobalAdmin?: boolean;
  role?: string | null;
}

interface SignalRApi {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  on: (event: string, handler: (...args: any[]) => void) => void;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  off: (event: string, handler: (...args: any[]) => void) => void;
  isConnected: boolean;
  joinGroup: (group: string) => Promise<void>;
  leaveGroup: (group: string) => Promise<void>;
}

interface UseDashboardSessionsParams {
  user: User | null | undefined;
  tenantId: string | null | undefined;
  globalAdminMode: boolean;
  tenantIdFilter: string;
  adminMode: boolean;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  addNotification: AddNotification;
  setBlockedDevicesSet: (next: Set<string>) => void;
  signalR: SignalRApi;
}

export interface UseDashboardSessionsReturn {
  sessions: Session[];
  loading: boolean;
  hasMore: boolean;
  loadingMore: boolean;
  refetch: () => void;
  refetchWith: (tenantIdOverride: string) => void;
  loadMore: () => void;
  removeSession: (sessionId: string) => void;
}

/**
 * Owns the dashboard's session list lifecycle:
 *  - initial fetch (gated on user role + tenantId/globalAdminMode readiness)
 *  - SignalR group joining (tenant + global-admins) and live update handlers
 *  - reconnect refetch
 *  - paginated load-more via cursor
 *  - blocked-devices sync after each fresh fetch
 *  - reset-on-globalAdminMode-toggle
 */
export function useDashboardSessions({
  user,
  tenantId,
  globalAdminMode,
  tenantIdFilter,
  adminMode,
  getAccessToken,
  addNotification,
  setBlockedDevicesSet,
  signalR,
}: UseDashboardSessionsParams): UseDashboardSessionsReturn {
  const { on, off, isConnected, joinGroup, leaveGroup } = signalR;

  const [sessions, setSessions] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  const [hasMore, setHasMore] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [cursor, setCursor] = useState<string | null>(null);

  // Refs for SignalR handlers to access current filter state without restarting subscriptions
  const tenantIdFilterRef = useRef(tenantIdFilter);
  tenantIdFilterRef.current = tenantIdFilter;
  const globalAdminModeRef = useRef(globalAdminMode);
  globalAdminModeRef.current = globalAdminMode;
  const tenantIdRef = useRef(tenantId);
  tenantIdRef.current = tenantId;

  // Refs for fetch closures (refetch is called from various effects/handlers and should
  // always see current filter values without forcing dependency-driven recreation)
  const adminModeRef = useRef(adminMode);
  adminModeRef.current = adminMode;
  const cursorRef = useRef(cursor);
  cursorRef.current = cursor;
  const loadingMoreRef = useRef(loadingMore);
  loadingMoreRef.current = loadingMore;

  const hasInitialFetch = useRef(false);
  const hasGlobalModeInitialized = useRef(false);
  const hasJoinedGroup = useRef(false);
  const wasConnectedRef = useRef(false);

  const fetchBlockedDevices = useCallback(async (currentSessions: Session[]) => {
    if (!adminModeRef.current || !globalAdminModeRef.current) {
      setBlockedDevicesSet(new Set());
      return;
    }

    try {
      const tenantIds = globalAdminModeRef.current
        ? [...new Set(currentSessions.map((s) => s.tenantId))]
        : tenantIdRef.current ? [tenantIdRef.current] : [];

      if (tenantIds.length === 0) {
        setBlockedDevicesSet(new Set());
        return;
      }

      const results = await Promise.allSettled(
        tenantIds.map((tid) =>
          authenticatedFetch(api.devices.blocked(tid), getAccessToken)
            .then((res) => (res.ok ? res.json() : { blocked: [] })),
        ),
      );

      const newSet = new Set<string>();
      results.forEach((result) => {
        if (result.status === "fulfilled" && result.value?.blocked) {
          for (const device of result.value.blocked) {
            newSet.add(`${device.tenantId}:${device.serialNumber}`);
          }
        }
      });

      setBlockedDevicesSet(newSet);
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification("error", "Session Expired", error.message, "session-expired-error");
      } else {
        console.error("Failed to fetch blocked devices:", error);
      }
    }
  }, [getAccessToken, addNotification, setBlockedDevicesSet]);

  const fetchSessions = useCallback(async (loadMoreCursor?: string, globalTenantIdOverride?: string) => {
    try {
      const rawFilter = globalTenantIdOverride !== undefined ? globalTenantIdOverride : tenantIdFilterRef.current.trim();
      const effectiveTenantFilter = asGuidOrUndefined(rawFilter);
      let endpoint = globalAdminModeRef.current
        ? api.globalSessions.list(effectiveTenantFilter)
        : api.sessions.list(tenantIdRef.current ?? undefined);

      if (loadMoreCursor) {
        endpoint += endpoint.includes("?") ? "&" : "?";
        endpoint += `cursor=${encodeURIComponent(loadMoreCursor)}`;
      }

      const response = await authenticatedFetch(endpoint, getAccessToken);

      if (response.ok) {
        const data = await response.json();
        const newSessions: Session[] = data.sessions || [];

        if (loadMoreCursor) {
          setSessions((prev) => [...prev, ...newSessions]);
        } else {
          setSessions(newSessions);
          fetchBlockedDevices(newSessions);
        }

        setHasMore(data.hasMore || false);
        setCursor(data.cursor || null);
      } else {
        addNotification("error", "Backend Error", `Failed to fetch sessions: ${response.statusText}`, "backend-error");
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification("error", "Session Expired", error.message, "session-expired-error");
      } else {
        console.error("Failed to fetch sessions:", error);
        addNotification(
          "error",
          "Backend Not Reachable",
          "Unable to connect to the backend API. Please ensure the backend server is running.",
          "backend-unreachable",
        );
      }
    } finally {
      setLoading(false);
      setLoadingMore(false);
    }
  }, [getAccessToken, addNotification, fetchBlockedDevices]);

  const refetch = useCallback(() => {
    setLoading(true);
    fetchSessions();
  }, [fetchSessions]);

  const refetchWith = useCallback((tenantIdOverride: string) => {
    setLoading(true);
    fetchSessions(undefined, tenantIdOverride);
  }, [fetchSessions]);

  const loadMore = useCallback(() => {
    if (!cursorRef.current || loadingMoreRef.current) return;
    setLoadingMore(true);
    fetchSessions(cursorRef.current);
  }, [fetchSessions]);

  const removeSession = useCallback((sessionId: string) => {
    setSessions((prev) => prev.filter((s) => s.sessionId !== sessionId));
  }, []);

  // Initial fetch — gated on user role + tenant readiness
  useEffect(() => {
    if (user && !user.isTenantAdmin && !user.isGlobalAdmin && user.role !== "Operator") {
      return; // regular users are redirected elsewhere; don't fetch
    }
    if (!globalAdminMode && !tenantId) return; // wait for real tenant ID
    if (hasInitialFetch.current) return;
    hasInitialFetch.current = true;

    fetchSessions();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [user, tenantId, globalAdminMode]);

  // Join tenant SignalR group; refetch on reconnect
  useEffect(() => {
    if (isConnected) {
      const isReconnect = wasConnectedRef.current;
      wasConnectedRef.current = true;

      if (!hasJoinedGroup.current) {
        const groupName = `tenant-${tenantId}`;
        hasJoinedGroup.current = true;
        joinGroup(groupName);
      }

      if (isReconnect && hasInitialFetch.current) {
        fetchSessions();
      }
    }

    return () => {
      if (hasJoinedGroup.current) {
        const groupName = `tenant-${tenantId}`;
        leaveGroup(groupName);
        hasJoinedGroup.current = false;
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isConnected, tenantId]);

  // Join/leave global-admins group when Global Admin mode changes
  useEffect(() => {
    if (!isConnected) return;

    if (globalAdminMode) {
      console.log("[Dashboard] Global Admin mode enabled: joining global-admins group");
      joinGroup("global-admins");
    } else {
      console.log("[Dashboard] Global Admin mode disabled: leaving global-admins group");
      leaveGroup("global-admins");
    }

    return () => {
      if (globalAdminMode) {
        console.log("[Dashboard] Component unmounting: leaving global-admins group");
        leaveGroup("global-admins");
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isConnected, globalAdminMode]);

  // SignalR listeners — re-register when connection cycles
  useEffect(() => {
    const handleNewSession = (...args: unknown[]) => {
      const data = args[0] as { sessionId: string; tenantId: string; session: Session } | undefined;
      if (!data) return;
      console.log("New session registered", data);

      if (!globalAdminModeRef.current && tenantIdRef.current && data.tenantId !== tenantIdRef.current) {
        console.log(`Ignoring newSession from tenant ${data.tenantId} (not in global mode, own tenant: ${tenantIdRef.current})`);
        return;
      }

      const activeFilter = tenantIdFilterRef.current.trim();
      if (globalAdminModeRef.current && activeFilter && data.tenantId !== activeFilter) {
        console.log(`Ignoring newSession from tenant ${data.tenantId} (filtered to ${activeFilter})`);
        return;
      }

      if (data.session) {
        setSessions((prevSessions) => {
          const sessionIndex = prevSessions.findIndex((s) => s.sessionId === data.session.sessionId);
          if (sessionIndex >= 0) {
            const updated = [...prevSessions];
            updated[sessionIndex] = data.session;
            return updated;
          }
          return [data.session, ...prevSessions];
        });
      } else {
        console.warn("newSession event received without session data, falling back to fetch");
        fetchSessions();
      }
    };

    const handleNewEvents = (...args: unknown[]) => {
      const data = args[0] as { sessionId: string; tenantId: string; eventCount: number; sessionUpdate?: Partial<Session>; session?: Session } | undefined;
      if (!data) return;
      console.log("New events notification received on dashboard", data);

      const update = data.sessionUpdate || data.session;
      if (update) {
        setSessions((prevSessions) => {
          const sessionIndex = prevSessions.findIndex((s) => s.sessionId === data.sessionId);
          if (sessionIndex >= 0) {
            const updated = [...prevSessions];
            updated[sessionIndex] = { ...prevSessions[sessionIndex], ...update };
            return updated;
          }
          return prevSessions;
        });
      }
    };

    on("newSession", handleNewSession);
    on("newevents", handleNewEvents);

    return () => {
      off("newSession", handleNewSession);
      off("newevents", handleNewEvents);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isConnected]);

  // Refetch + reset visible state when Global Admin mode toggles (after first init)
  useEffect(() => {
    if (!hasGlobalModeInitialized.current) {
      hasGlobalModeInitialized.current = true;
      return;
    }

    setSessions([]);
    setCursor(null);
    setLoading(true);
    fetchSessions();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [globalAdminMode]);

  return {
    sessions,
    loading,
    hasMore,
    loadingMore,
    refetch,
    refetchWith,
    loadMore,
    removeSession,
  };
}
