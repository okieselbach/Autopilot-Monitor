"use client";

import React, { createContext, useContext, useState, useCallback, useEffect, useRef } from 'react';
import { useAuth } from './AuthContext';
import { useSignalR } from './SignalRContext';
import { api } from '@/lib/api';
import { authenticatedFetch } from '@/lib/authenticatedFetch';

export interface GlobalNotification {
  id: string;
  type: string;
  title: string;
  message: string;
  href?: string;
  createdAt: string;
}

interface GlobalNotificationContextType {
  notifications: GlobalNotification[];
  unreadCount: number;
  dismissNotification: (id: string) => Promise<void>;
  dismissAll: () => Promise<void>;
  isLoading: boolean;
}

const GlobalNotificationContext = createContext<GlobalNotificationContextType | undefined>(undefined);

export function GlobalNotificationProvider({ children }: { children: React.ReactNode }) {
  const { user, getAccessToken } = useAuth();
  const { connection, isConnected, joinGroup, leaveGroup } = useSignalR();
  const [notifications, setNotifications] = useState<GlobalNotification[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const fetchingRef = useRef(false);

  const isGlobal = user?.isGlobalAdmin === true;

  const fetchNotifications = useCallback(async () => {
    if (!isGlobal || fetchingRef.current) return;
    fetchingRef.current = true;

    try {
      const response = await authenticatedFetch(
        api.notifications.list(),
        getAccessToken,
      );
      if (response.ok) {
        const data = await response.json();
        setNotifications(data.notifications ?? []);
      }
    } catch {
      // Silently ignore — notifications are best-effort
    } finally {
      fetchingRef.current = false;
    }
  }, [isGlobal, getAccessToken]);

  // Initial state hydration. Re-fetched on SignalR reconnect to recover any deltas
  // pushed during the disconnect window.
  useEffect(() => {
    if (!isGlobal) {
      setNotifications([]);
      return;
    }
    setIsLoading(true);
    fetchNotifications().finally(() => setIsLoading(false));
  }, [isGlobal, fetchNotifications]);

  useEffect(() => {
    if (!connection || !isGlobal) return;
    const handler = () => { fetchNotifications(); };
    connection.onreconnected(handler);
  }, [connection, isGlobal, fetchNotifications]);

  // Re-fetch when globalAdminMode is toggled in localStorage
  useEffect(() => {
    const handleStorageChange = () => {
      if (isGlobal) {
        fetchNotifications();
      }
    };
    window.addEventListener('localStorageChange', handleStorageChange);
    return () => window.removeEventListener('localStorageChange', handleStorageChange);
  }, [isGlobal, fetchNotifications]);

  // Group membership: only Global Admins join the global-admins group.
  useEffect(() => {
    if (!isConnected || !isGlobal) return;
    const group = 'global-admins';
    joinGroup(group);
    return () => { leaveGroup(group); };
  }, [isConnected, isGlobal, joinGroup, leaveGroup]);

  // Live-push handlers.
  useEffect(() => {
    if (!connection) return;

    const onCreate = (notification: GlobalNotification) => {
      if (!notification?.id) return;
      setNotifications(prev => prev.some(n => n.id === notification.id) ? prev : [notification, ...prev]);
    };
    const onDismiss = (payload: { id: string }) => {
      if (!payload?.id) return;
      setNotifications(prev => prev.filter(n => n.id !== payload.id));
    };
    const onDismissAll = () => {
      setNotifications([]);
    };

    connection.on('globalNotification', onCreate);
    connection.on('globalNotificationDismissed', onDismiss);
    connection.on('globalNotificationsDismissedAll', onDismissAll);

    return () => {
      connection.off('globalNotification', onCreate);
      connection.off('globalNotificationDismissed', onDismiss);
      connection.off('globalNotificationsDismissedAll', onDismissAll);
    };
  }, [connection]);

  const dismissNotification = useCallback(async (id: string) => {
    setNotifications(prev => prev.filter(n => n.id !== id));

    try {
      await authenticatedFetch(
        api.notifications.dismiss(id),
        getAccessToken,
        { method: 'POST' },
      );
    } catch {
      // Best-effort; backend push will reconcile other tabs if dismiss eventually lands
    }
  }, [getAccessToken]);

  const dismissAll = useCallback(async () => {
    setNotifications([]);

    try {
      await authenticatedFetch(
        api.notifications.dismissAll(),
        getAccessToken,
        { method: 'POST' },
      );
    } catch {
      // Best-effort
    }
  }, [getAccessToken]);

  const unreadCount = notifications.length;

  return (
    <GlobalNotificationContext.Provider value={{ notifications, unreadCount, dismissNotification, dismissAll, isLoading }}>
      {children}
    </GlobalNotificationContext.Provider>
  );
}

export function useGlobalNotifications() {
  const context = useContext(GlobalNotificationContext);
  if (context === undefined) {
    throw new Error('useGlobalNotifications must be used within a GlobalNotificationProvider');
  }
  return context;
}
