"use client";

import React, { createContext, useContext, useState, useEffect } from 'react';
import { useAuth } from './AuthContext';

interface TenantContextType {
  tenantId: string;
  setTenantId: (id: string) => void;
}

const TenantContext = createContext<TenantContextType | undefined>(undefined);

const DEFAULT_TENANT_ID = 'deadbeef-dead-beef-dead-beefdeadbeef';

export function TenantProvider({ children }: { children: React.ReactNode }) {
  const { user, isAuthenticated, isLoading } = useAuth();

  const [tenantId, setTenantId] = useState<string>(() => {
    // Initial: aus localStorage oder Default
    if (typeof window !== 'undefined') {
      return localStorage.getItem('tenantId') || DEFAULT_TENANT_ID;
    }
    return DEFAULT_TENANT_ID;
  });

  // Update tenant ID from authenticated user
  useEffect(() => {
    if (!isLoading && isAuthenticated && user) {
      // Use tenant ID from authenticated user
      if (user.tenantId && user.tenantId !== tenantId) {
        console.log(`[TenantContext] Setting tenant ID from auth: ${user.tenantId}`);
        setTenantId(user.tenantId);
      }
    } else if (!isLoading && !isAuthenticated) {
      // Not authenticated - use default
      if (tenantId !== DEFAULT_TENANT_ID) {
        console.log(`[TenantContext] User not authenticated, using default tenant ID`);
        setTenantId(DEFAULT_TENANT_ID);
      }
    }
  }, [user, isAuthenticated, isLoading, tenantId]);

  // Save tenant ID to localStorage
  useEffect(() => {
    if (typeof window !== 'undefined') {
      localStorage.setItem('tenantId', tenantId);
    }
  }, [tenantId]);

  return (
    <TenantContext.Provider value={{ tenantId, setTenantId }}>
      {children}
    </TenantContext.Provider>
  );
}

export function useTenant() {
  const context = useContext(TenantContext);
  if (context === undefined) {
    throw new Error('useTenant must be used within a TenantProvider');
  }
  return context;
}
