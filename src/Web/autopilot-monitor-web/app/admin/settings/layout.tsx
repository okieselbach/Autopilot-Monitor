import { ProtectedRoute } from "@/components/ProtectedRoute";
import { SettingsSidebar } from "./SettingsSidebar";

// Platform settings are GA-only. A read-only Global Reader reaches /admin (view scope) but must not
// open the settings sub-pages even by direct URL — gate the whole subtree on the real Global Admin
// role (the section is also nav-hidden for readers, and the backend redacts/403s).
export default function SettingsLayout({ children }: { children: React.ReactNode }) {
  return (
    <ProtectedRoute requireGlobalAdmin>
      <SettingsSidebar>{children}</SettingsSidebar>
    </ProtectedRoute>
  );
}
