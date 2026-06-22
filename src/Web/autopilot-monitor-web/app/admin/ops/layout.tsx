import { ProtectedRoute } from "@/components/ProtectedRoute";

// Ops (maintenance trigger, reseed, session cleanup/cascade-delete) are destructive platform
// operations — real Global Admin only. Nav-hidden for a read-only Global Reader; gate by route too.
export default function OpsLayout({ children }: { children: React.ReactNode }) {
  return <ProtectedRoute requireGlobalAdmin>{children}</ProtectedRoute>;
}
