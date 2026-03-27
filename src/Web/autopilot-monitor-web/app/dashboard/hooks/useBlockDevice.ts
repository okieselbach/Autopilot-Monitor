"use client";
import { useState, useEffect } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { NotificationType } from "@/contexts/NotificationContext";

interface BlockTarget {
  serialNumber: string;
  tenantId: string;
  deviceName?: string;
}

export function useBlockDevice(
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>,
  addNotification: (type: NotificationType, title: string, message: string, key?: string, href?: string) => void,
  adminMode: boolean,
  globalAdminMode: boolean
) {
  const [showBlockConfirm, setShowBlockConfirm] = useState(false);
  const [sessionToBlock, setSessionToBlock] = useState<BlockTarget | null>(null);
  const [blockingDevice, setBlockingDevice] = useState(false);
  const [blockedDevicesSet, setBlockedDevicesSet] = useState<Set<string>>(new Set());

  // Reset blocked set when admin mode or global admin mode is turned off
  useEffect(() => {
    if (!adminMode || !globalAdminMode) {
      setBlockedDevicesSet(new Set());
    }
  }, [adminMode, globalAdminMode]);

  const blockDevice = (serialNumber: string, tenantId: string, deviceName?: string) => {
    setSessionToBlock({ serialNumber, tenantId, deviceName });
    setShowBlockConfirm(true);
  };

  const confirmBlock = async () => {
    if (!sessionToBlock) return;

    try {
      setBlockingDevice(true);

      const response = await authenticatedFetch(api.devices.block(), getAccessToken, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          tenantId: sessionToBlock.tenantId,
          serialNumber: sessionToBlock.serialNumber,
          durationHours: 24,
          reason: `Blocked from dashboard by Global Admin`
        })
      });

      if (response.ok) {
        console.log(`Device ${sessionToBlock.serialNumber} blocked successfully`);
        setShowBlockConfirm(false);
        setSessionToBlock(null);
        addNotification('success', 'Device Blocked', `Device ${sessionToBlock.deviceName || sessionToBlock.serialNumber} blocked for 24 hours.`);
        setBlockedDevicesSet(prev => {
          const next = new Set(prev);
          next.add(`${sessionToBlock.tenantId}:${sessionToBlock.serialNumber}`);
          return next;
        });
      } else {
        const data = await response.json();
        alert(`Fehler beim Blocken: ${data.message || 'Unbekannter Fehler'}`);
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error('Failed to block device:', error);
        alert('Fehler beim Blocken des Geräts');
      }
    } finally {
      setBlockingDevice(false);
    }
  };

  const cancelBlock = () => {
    setShowBlockConfirm(false);
    setSessionToBlock(null);
  };

  return {
    showBlockConfirm,
    sessionToBlock,
    blockingDevice,
    blockedDevicesSet,
    setBlockedDevicesSet,
    blockDevice,
    confirmBlock,
    cancelBlock,
  };
}
