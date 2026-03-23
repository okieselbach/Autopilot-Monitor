"use client";

import { useRouter } from "next/navigation";
import { useEffect } from "react";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useAuth } from "../../contexts/AuthContext";
import { AdminConfigProvider } from "./AdminConfigContext";

export default function AdminLayout({ children }: { children: React.ReactNode }) {
  const { user } = useAuth();
  const router = useRouter();

  useEffect(() => {
    if (user && !user.isGlobalAdmin) {
      router.push("/dashboard");
    }
  }, [user, router]);

  if (!user?.isGlobalAdmin) return null;

  return (
    <ProtectedRoute requireGlobalAdmin>
      <AdminConfigProvider>
        {children}
      </AdminConfigProvider>
    </ProtectedRoute>
  );
}
