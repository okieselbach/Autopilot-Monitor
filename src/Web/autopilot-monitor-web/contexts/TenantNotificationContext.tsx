"use client";

import React, { createContext, useContext, useState, useCallback, useEffect, useRef } from 'react';
import { useAuth } from './AuthContext';
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

  const isAuthenticated = user != null;

  const fetchNotifications = useCallback(async () => {
    if (!isAuthenticated || fetchingRef.current) return;
    fetchingRef.current = true;

    try {
      const response = await authenticatedFetch(
        api.notifications.tenantList(),
        getAccessToken,
      );
      if (response.ok) {
        const data = await response.json();
        setTenantNotifications(data.notifications ?? []);
      } else if (response.status === 403) {
        // Backend gates this on TenantAdminOrGA today; non-admins get 403 — treat as empty.
        setTenantNotifications([]);
      }
    } catch {
      // Silently ignore — notifications are best-effort
    } finally {
      fetchingRef.current = false;
    }
  }, [isAuthenticated, getAccessToken]);

  useEffect(() => {
    if (!isAuthenticated) {
      setTenantNotifications([]);
      return;
    }

    setIsLoading(true);
    fetchNotifications().finally(() => setIsLoading(false));

    intervalRef.current = setInterval(fetchNotifications, POLL_INTERVAL_MS);

    return () => {
      if (intervalRef.current) clearInterval(intervalRef.current);
    };
  }, [isAuthenticated, fetchNotifications]);

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
