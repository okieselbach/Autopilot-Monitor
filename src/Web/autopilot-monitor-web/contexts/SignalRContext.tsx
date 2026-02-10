"use client";

import React, { createContext, useContext, useEffect, useRef, useState, useCallback } from 'react';
import * as signalR from '@microsoft/signalr';
import { API_BASE_URL } from '@/lib/config';
import { useAuth } from './AuthContext';

interface SignalRContextType {
  connection: signalR.HubConnection | null;
  connectionState: signalR.HubConnectionState;
  on: (eventName: string, callback: (...args: any[]) => void) => void;
  off: (eventName: string, callback: (...args: any[]) => void) => void;
  invoke: (methodName: string, ...args: any[]) => Promise<any>;
  joinGroup: (groupName: string) => Promise<void>;
  leaveGroup: (groupName: string) => Promise<void>;
  isConnected: boolean;
}

const SignalRContext = createContext<SignalRContextType | undefined>(undefined);

export function SignalRProvider({ children }: { children: React.ReactNode }) {
  const { getAccessToken, isAuthenticated } = useAuth();
  const [connection, setConnection] = useState<signalR.HubConnection | null>(null);
  const [connectionState, setConnectionState] = useState<signalR.HubConnectionState>(signalR.HubConnectionState.Disconnected);
  const connectionRef = useRef<signalR.HubConnection | null>(null);
  const joinedGroupsRef = useRef<Set<string>>(new Set());
  const retryCountRef = useRef(0);
  const maxRetries = 3;

  useEffect(() => {
    // Only create connection if authenticated
    if (!isAuthenticated) {
      return;
    }

    // Only create connection once
    if (connectionRef.current) {
      return;
    }

    const hubUrl = `${API_BASE_URL}/api/realtime`;
    const newConnection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, {
        accessTokenFactory: async () => {
          const token = await getAccessToken();
          return token || '';
        }
      })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          // Exponential backoff: 0s, 2s, 10s, 30s, then 30s thereafter
          if (retryContext.elapsedMilliseconds < 60000) {
            return Math.min(1000 * Math.pow(2, retryContext.previousRetryCount), 30000);
          }
          return 30000;
        }
      })
      .configureLogging(signalR.LogLevel.Information)
      .build();

    connectionRef.current = newConnection;

    // Setup connection state change handlers
    newConnection.onclose((error) => {
      setConnectionState(signalR.HubConnectionState.Disconnected);
      joinedGroupsRef.current.clear(); // Clear joined groups on disconnect
    });

    newConnection.onreconnecting((error) => {
      setConnectionState(signalR.HubConnectionState.Reconnecting);
    });

    newConnection.onreconnected((connectionId) => {
      setConnectionState(signalR.HubConnectionState.Connected);
      retryCountRef.current = 0; // Reset retry count on successful reconnect
      joinedGroupsRef.current.clear(); // Clear joined groups on reconnect - need to re-join
    });

    // Start connection
    const startConnection = async () => {
      try {
        await newConnection.start();
        setConnectionState(signalR.HubConnectionState.Connected);
        setConnection(newConnection);
        retryCountRef.current = 0;
      } catch (error) {
        console.error('[SignalR] Failed to start connection:', error);
        setConnectionState(signalR.HubConnectionState.Disconnected);

        // Limited retry with exponential backoff
        if (retryCountRef.current < maxRetries) {
          retryCountRef.current++;
          const delay = Math.min(1000 * Math.pow(2, retryCountRef.current), 30000);
          setTimeout(startConnection, delay);
        } else {
          console.error('[SignalR] Max retries reached. Connection failed.');
        }
      }
    };

    startConnection();

    // Cleanup only when provider unmounts (app closes)
    return () => {
      if (connectionRef.current) {
        connectionRef.current.stop();
        connectionRef.current = null;
      }
    };
  }, [isAuthenticated, getAccessToken]);

  const on = (eventName: string, callback: (...args: any[]) => void) => {
    if (connectionRef.current) {
      connectionRef.current.on(eventName, callback);
    }
  };

  const off = (eventName: string, callback: (...args: any[]) => void) => {
    if (connectionRef.current) {
      connectionRef.current.off(eventName, callback);
    }
  };

  const invoke = async (methodName: string, ...args: any[]) => {
    if (connection && connectionState === signalR.HubConnectionState.Connected) {
      return await connection.invoke(methodName, ...args);
    }
    throw new Error('SignalR connection not established');
  };

  const joinGroup = useCallback(async (groupName: string) => {
    if (!connection || connectionState !== signalR.HubConnectionState.Connected) {
      return;
    }

    // Check if already in group to prevent duplicate joins
    if (joinedGroupsRef.current.has(groupName)) {
      return;
    }

    // Add to Set immediately to prevent race conditions with multiple simultaneous calls
    joinedGroupsRef.current.add(groupName);

    try {
      const connectionId = connection.connectionId;
      if (!connectionId) {
        joinedGroupsRef.current.delete(groupName); // Remove if we can't get connection ID
        return;
      }

      const token = await getAccessToken();
      const response = await fetch(`${API_BASE_URL}/api/realtime/groups/join`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${token}`
        },
        body: JSON.stringify({ connectionId, groupName })
      });

      if (!response.ok) {
        // Remove from Set if API call failed (so we can retry)
        joinedGroupsRef.current.delete(groupName);
      }
    } catch (error) {
      console.error(`[SignalR] Error joining group ${groupName}:`, error);
      // Remove from Set if API call failed (so we can retry)
      joinedGroupsRef.current.delete(groupName);
    }
  }, [connection, connectionState, getAccessToken]);

  const leaveGroup = useCallback(async (groupName: string) => {
    if (!connection || connectionState !== signalR.HubConnectionState.Connected) {
      return;
    }

    // Check if we're actually in the group before leaving
    if (!joinedGroupsRef.current.has(groupName)) {
      return;
    }

    // Remove from Set immediately to prevent race conditions with multiple simultaneous calls
    joinedGroupsRef.current.delete(groupName);

    try {
      const connectionId = connection.connectionId;
      if (!connectionId) {
        return;
      }

      const token = await getAccessToken();
      const response = await fetch(`${API_BASE_URL}/api/realtime/groups/leave`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${token}`
        },
        body: JSON.stringify({ connectionId, groupName })
      });

      if (!response.ok) {
        // Re-add to Set if API call failed (so we can retry)
        joinedGroupsRef.current.add(groupName);
      }
    } catch (error) {
      console.error(`[SignalR] Error leaving group ${groupName}:`, error);
      // Re-add to Set if API call failed (so we can retry)
      joinedGroupsRef.current.add(groupName);
    }
  }, [connection, connectionState, getAccessToken]);

  return (
    <SignalRContext.Provider
      value={{
        connection,
        connectionState,
        on,
        off,
        invoke,
        joinGroup,
        leaveGroup,
        isConnected: connectionState === signalR.HubConnectionState.Connected,
      }}
    >
      {children}
    </SignalRContext.Provider>
  );
}

export function useSignalR() {
  const context = useContext(SignalRContext);
  if (context === undefined) {
    throw new Error('useSignalR must be used within a SignalRProvider');
  }
  return context;
}
