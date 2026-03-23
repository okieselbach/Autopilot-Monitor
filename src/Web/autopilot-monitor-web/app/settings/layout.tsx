"use client";

import { useRouter } from "next/navigation";
import { useEffect } from "react";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useAuth } from "../../contexts/AuthContext";
import { TenantConfigProvider } from "./TenantConfigContext";
import { SettingsPageSections } from "./SettingsPageSections";

export default function SettingsLayout({ children }: { children: React.ReactNode }) {
  const { user } = useAuth();
  const router = useRouter();

  useEffect(() => {
    if (!user) return;

    // Regular users (not admin, not operator) → redirect to progress portal
    if (!user.isTenantAdmin && !user.isGalacticAdmin && user.role !== "Operator") {
      router.replace("/progress");
      return;
    }

    // Operator without bootstrap permission → no settings access
    if (user.role === "Operator" && !user.isTenantAdmin && !user.isGalacticAdmin && !user.canManageBootstrapTokens) {
      router.replace("/dashboard");
    }
  }, [user, router]);

  // Don't render until we know user is allowed
  if (!user) return null;
  if (!user.isTenantAdmin && !user.isGalacticAdmin && user.role !== "Operator") return null;
  if (user.role === "Operator" && !user.isTenantAdmin && !user.isGalacticAdmin && !user.canManageBootstrapTokens) return null;

  return (
    <ProtectedRoute>
      <TenantConfigProvider>
        <SettingsPageSections />
        <div className="min-h-screen bg-gray-50">
          {children}
        </div>
      </TenantConfigProvider>
    </ProtectedRoute>
  );
}
