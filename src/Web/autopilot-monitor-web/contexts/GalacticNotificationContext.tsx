"use client";

import React, { createContext, useContext, useState, useCallback, useEffect, useRef } from 'react';
import { useAuth } from './AuthContext';
import { API_BASE_URL } from '@/lib/config';
import { authenticatedFetch } from '@/lib/authenticatedFetch';

export interface GalacticNotification {
  id: string;
  type: string;
  title: string;
  message: string;
  href?: string;
  createdAt: string;
}

interface GalacticNotificationContextType {
  notifications: GalacticNotification[];
  unreadCount: number;
  dismissNotification: (id: string) => Promise<void>;
  dismissAll: () => Promise<void>;
  isLoading: boolean;
}

const GalacticNotificationContext = createContext<GalacticNotificationContextType | undefined>(undefined);

const POLL_INTERVAL_MS = 60_000;

export function GalacticNotificationProvider({ children }: { children: React.ReactNode }) {
  const { user, getAccessToken } = useAuth();
  const [notifications, setNotifications] = useState<GalacticNotification[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const fetchingRef = useRef(false);
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const isGalactic = user?.isGalacticAdmin === true;

  const fetchNotifications = useCallback(async () => {
    if (!isGalactic || fetchingRef.current) return;
    fetchingRef.current = true;

    try {
      const response = await authenticatedFetch(
        `${API_BASE_URL}/api/galactic/notifications`,
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
  }, [isGalactic, getAccessToken]);

  // Initial fetch + polling
  useEffect(() => {
    if (!isGalactic) {
      setNotifications([]);
      return;
    }

    setIsLoading(true);
    fetchNotifications().finally(() => setIsLoading(false));

    intervalRef.current = setInterval(fetchNotifications, POLL_INTERVAL_MS);

    return () => {
      if (intervalRef.current) clearInterval(intervalRef.current);
    };
  }, [isGalactic, fetchNotifications]);

  // Re-fetch when galacticAdminMode is toggled in localStorage
  useEffect(() => {
    const handleStorageChange = () => {
      if (isGalactic) {
        fetchNotifications();
      }
    };
    window.addEventListener('localStorageChange', handleStorageChange);
    return () => window.removeEventListener('localStorageChange', handleStorageChange);
  }, [isGalactic, fetchNotifications]);

  const dismissNotification = useCallback(async (id: string) => {
    // Optimistic removal
    setNotifications(prev => prev.filter(n => n.id !== id));

    try {
      await authenticatedFetch(
        `${API_BASE_URL}/api/galactic/notifications/${id}/dismiss`,
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
        `${API_BASE_URL}/api/galactic/notifications/dismiss-all`,
        getAccessToken,
        { method: 'POST' },
      );
    } catch {
      // Best-effort
    }
  }, [getAccessToken]);

  const unreadCount = notifications.length;

  return (
    <GalacticNotificationContext.Provider value={{ notifications, unreadCount, dismissNotification, dismissAll, isLoading }}>
      {children}
    </GalacticNotificationContext.Provider>
  );
}

export function useGalacticNotifications() {
  const context = useContext(GalacticNotificationContext);
  if (context === undefined) {
    throw new Error('useGalacticNotifications must be used within a GalacticNotificationProvider');
  }
  return context;
}
