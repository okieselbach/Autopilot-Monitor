"use client";

import { ProtectedRoute } from "../../components/ProtectedRoute";
import { AdminConfigProvider } from "./AdminConfigContext";

export default function AdminLayout({ children }: { children: React.ReactNode }) {
  return (
    // Platform scope (Global Admin OR read-only Global Reader) may enter the admin area to VIEW
    // cross-tenant data. The platform-settings sub-pages are nav-hidden for a reader and remain
    // gated on the real Global-Admin status (AdminConfigContext load/save) + backend (redaction/403).
    <ProtectedRoute requireGlobalScope>
      <AdminConfigProvider>
        {children}
      </AdminConfigProvider>
    </ProtectedRoute>
  );
}
