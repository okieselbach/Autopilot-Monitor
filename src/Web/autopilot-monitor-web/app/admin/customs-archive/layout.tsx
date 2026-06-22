import { ProtectedRoute } from "@/components/ProtectedRoute";

// Customs Archive review/delete is a destructive cleanup operation — real Global Admin only. Nav-hidden
// for a read-only Global Reader; gate by route too.
export default function CustomsArchiveLayout({ children }: { children: React.ReactNode }) {
  return <ProtectedRoute requireGlobalAdmin>{children}</ProtectedRoute>;
}
