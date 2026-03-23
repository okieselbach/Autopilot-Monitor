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
    if (user && !user.isGalacticAdmin) {
      router.push("/dashboard");
    }
  }, [user, router]);

  if (!user?.isGalacticAdmin) return null;

  return (
    <ProtectedRoute requireGalacticAdmin>
      <AdminConfigProvider>
        {children}
      </AdminConfigProvider>
    </ProtectedRoute>
  );
}
