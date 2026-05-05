"use client";

import React, { createContext, useContext, useState, useCallback, useEffect, useRef } from 'react';
import { useAuth } from './AuthContext';
import { canFetchTenantNotifications } from './tenantNotificationsGate';
import { api } from '@/lib/api';
import { authenticatedFetch } from '@/lib/authenticatedFetch';

export interface TenantNotification {
  id: string;
  type: string;
  title: string;
  message: string;
  href?: string;
  createdAt: string;
}

interface TenantNotificationContextType {
  tenantNotifications: TenantNotification[];
  tenantUnreadCount: number;
  dismissTenantNotification: (id: string) => Promise<void>;
  dismissAllTenant: () => Promise<void>;
  isLoading: boolean;
}

const TenantNotificationContext = createContext<TenantNotificationContextType | undefined>(undefined);

const POLL_INTERVAL_MS = 60_000;

export function TenantNotificationProvider({ children }: { children: React.ReactNode }) {
  const { user, getAccessToken } = useAuth();
  const [tenantNotifications, setTenantNotifications] = useState<TenantNotification[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const fetchingRef = useRef(false);
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null);

  // Only tenant members (Admin/Operator/Viewer) and Global Admins are entitled to the bell.
  // The backend filters per-notification visibility by audience tier, so it is safe to fetch
  // for every member role — but unauthenticated users and users without a tenant role would
  // just produce 401/403 noise, so we skip them entirely.
  const canFetchNotifications = canFetchTenantNotifications(user);

  const fetchNotifications = useCallback(async () => {
    if (!canFetchNotifications || fetchingRef.current) return;
    fetchingRef.current = true;

    try {
      const response = await authenticatedFetch(
        api.notifications.tenantList(),
        getAccessToken,
      );
      if (response.ok) {
        const data = await response.json();
        setTenantNotifications(data.notifications ?? []);
      }
    } catch {
      // Silently ignore — notifications are best-effort
    } finally {
      fetchingRef.current = false;
    }
  }, [canFetchNotifications, getAccessToken]);

  useEffect(() => {
    if (!canFetchNotifications) {
      setTenantNotifications([]);
      return;
    }

    setIsLoading(true);
    fetchNotifications().finally(() => setIsLoading(false));

    intervalRef.current = setInterval(fetchNotifications, POLL_INTERVAL_MS);

    return () => {
      if (intervalRef.current) clearInterval(intervalRef.current);
    };
  }, [canFetchNotifications, fetchNotifications]);

  const dismissTenantNotification = useCallback(async (id: string) => {
    setTenantNotifications(prev => prev.filter(n => n.id !== id));

    try {
      await authenticatedFetch(
        api.notifications.tenantDismiss(id),
        getAccessToken,
        { method: 'POST' },
      );
    } catch {
      // Best-effort; next poll will reconcile
    }
  }, [getAccessToken]);

  const dismissAllTenant = useCallback(async () => {
    setTenantNotifications([]);

    try {
      await authenticatedFetch(
        api.notifications.tenantDismissAll(),
        getAccessToken,
        { method: 'POST' },
      );
    } catch {
      // Best-effort
    }
  }, [getAccessToken]);

  const tenantUnreadCount = tenantNotifications.length;

  return (
    <TenantNotificationContext.Provider value={{
      tenantNotifications,
      tenantUnreadCount,
      dismissTenantNotification,
      dismissAllTenant,
      isLoading,
    }}>
      {children}
    </TenantNotificationContext.Provider>
  );
}

export function useTenantNotifications() {
  const context = useContext(TenantNotificationContext);
  if (context === undefined) {
    throw new Error('useTenantNotifications must be used within a TenantNotificationProvider');
  }
  return context;
}
