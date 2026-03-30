import { TenantSidebar } from "./TenantSidebar";

export default function TenantLayout({ children }: { children: React.ReactNode }) {
  return <TenantSidebar>{children}</TenantSidebar>;
}
