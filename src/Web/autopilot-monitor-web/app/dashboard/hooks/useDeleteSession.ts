"use client";
import { useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { trackEvent } from "@/lib/appInsights";
import { NotificationType } from "@/contexts/NotificationContext";

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

  const deleteSession = (sessionId: string, tenantId: string, deviceName?: string) => {
    setSessionToDelete({ sessionId, tenantId, deviceName });
    setShowDeleteConfirm(true);
  };

  const confirmDelete = async () => {
    if (!sessionToDelete) return;

    try {
      const response = await authenticatedFetch(api.sessions.delete(sessionToDelete.sessionId, sessionToDelete.tenantId), getAccessToken, {
        method: 'DELETE',
      });

      if (response.ok) {
        trackEvent("session_deleted", { inAdminMode: adminMode });
        onSessionDeleted(sessionToDelete.sessionId);
        console.log(`Session ${sessionToDelete.sessionId} deleted successfully`);
        setShowDeleteConfirm(false);
        setSessionToDelete(null);
      } else {
        const data = await response.json();
        alert(`Fehler beim Löschen: ${data.message || 'Unbekannter Fehler'}`);
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error('Failed to delete session:', error);
        alert('Fehler beim Löschen der Session');
      }
    }
  };

  const cancelDelete = () => {
    setShowDeleteConfirm(false);
    setSessionToDelete(null);
  };

  return { showDeleteConfirm, sessionToDelete, deleteSession, confirmDelete, cancelDelete };
}
