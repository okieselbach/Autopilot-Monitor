"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import { useAuth } from "../../contexts/AuthContext";

export default function SettingsPage() {
  const router = useRouter();
  const { user } = useAuth();

  useEffect(() => {
    if (!user) return;

    // Operators with bootstrap permission only → go directly to bootstrap sessions
    if (user.role === "Operator" && !user.isTenantAdmin && !user.isGlobalAdmin && user.canManageBootstrapTokens) {
      router.replace("/settings/access/bootstrap-sessions");
      return;
    }

    // Everyone else (Tenant Admins, Global Admins) → validation (first in onboarding flow)
    router.replace("/settings/validation/autopilot");
  }, [user, router]);

  return null;
}
