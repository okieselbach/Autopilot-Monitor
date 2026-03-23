"use client";

import React, { createContext, useContext, useState, useCallback, useEffect, useRef } from 'react';
import { useAuth } from './AuthContext';
import { API_BASE_URL } from '@/lib/config';
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

const POLL_INTERVAL_MS = 60_000;

export function GlobalNotificationProvider({ children }: { children: React.ReactNode }) {
  const { user, getAccessToken } = useAuth();
  const [notifications, setNotifications] = useState<GlobalNotification[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const fetchingRef = useRef(false);
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const isGlobal = user?.isGlobalAdmin === true;

  const fetchNotifications = useCallback(async () => {
    if (!isGlobal || fetchingRef.current) return;
    fetchingRef.current = true;

    try {
      const response = await authenticatedFetch(
        `${API_BASE_URL}/api/global/notifications`,
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

  // Initial fetch + polling
  useEffect(() => {
    if (!isGlobal) {
      setNotifications([]);
      return;
    }

    setIsLoading(true);
    fetchNotifications().finally(() => setIsLoading(false));

    intervalRef.current = setInterval(fetchNotifications, POLL_INTERVAL_MS);

    return () => {
      if (intervalRef.current) clearInterval(intervalRef.current);
    };
  }, [isGlobal, fetchNotifications]);

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

  const dismissNotification = useCallback(async (id: string) => {
    // Optimistic removal
    setNotifications(prev => prev.filter(n => n.id !== id));

    try {
      await authenticatedFetch(
        `${API_BASE_URL}/api/global/notifications/${id}/dismiss`,
        getAccessToken,
        { method: 'POST' },
      );
    } catch {
      // Best-effort; next poll will reconcile
    }
  }, [getAccessToken]);

  const dismissAll = useCallback(async () => {
    setNotifications([]);

    try {
      await authenticatedFetch(
        `${API_BASE_URL}/api/global/notifications/dismiss-all`,
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
