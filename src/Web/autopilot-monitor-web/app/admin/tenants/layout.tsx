import { TenantsSidebar } from "./TenantsSidebar";

export default function TenantsLayout({ children }: { children: React.ReactNode }) {
  return <TenantsSidebar>{children}</TenantsSidebar>;
}
