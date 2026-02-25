"use client";

import React, { createContext, useContext, useState, useEffect } from 'react';
import { useAuth } from './AuthContext';

interface TenantContextType {
  tenantId: string;
  setTenantId: (id: string) => void;
}

const TenantContext = createContext<TenantContextType | undefined>(undefined);

const LEGACY_FAKE_TENANT_ID = 'deadbeef-dead-beef-dead-beefdeadbeef';

export function TenantProvider({ children }: { children: React.ReactNode }) {
  const { user, isAuthenticated, isLoading } = useAuth();

  const [tenantId, setTenantId] = useState<string>(() => {
    if (typeof window !== 'undefined') {
      const stored = localStorage.getItem('tenantId');
      // Migrate away from the old fake placeholder ID
      return (stored && stored !== LEGACY_FAKE_TENANT_ID) ? stored : '';
    }
    return '';
  });

  // Update tenant ID from authenticated user
  useEffect(() => {
    if (!isLoading && isAuthenticated && user) {
      // Use tenant ID from authenticated user's token
      // This is the APPLICATION tenant ID, not the Azure AD tenant ID
      if (user.tenantId && user.tenantId !== tenantId) {
        console.log(`[TenantContext] Setting application tenant ID from auth: ${user.tenantId}`);
        setTenantId(user.tenantId);
      }
    } else if (!isLoading && !isAuthenticated) {
      // Not authenticated - clear tenant ID
      if (tenantId !== '') {
        console.log(`[TenantContext] User not authenticated, clearing tenant ID`);
        setTenantId('');
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
