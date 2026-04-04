"use client";

import { ProtectedRoute } from "../../components/ProtectedRoute";
import { AdminConfigProvider } from "./AdminConfigContext";

export default function AdminLayout({ children }: { children: React.ReactNode }) {
  return (
    <ProtectedRoute requireGlobalAdmin>
      <AdminConfigProvider>
        {children}
      </AdminConfigProvider>
    </ProtectedRoute>
  );
}
