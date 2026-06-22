import { ProtectedRoute } from "@/components/ProtectedRoute";

// Software mapping is vulnerability-data curation (write-heavy CPE mapping / ignore) — real Global
// Admin only. Nav-hidden for a read-only Global Reader; gate by route too.
export default function SoftwareLayout({ children }: { children: React.ReactNode }) {
  return <ProtectedRoute requireGlobalAdmin>{children}</ProtectedRoute>;
}
