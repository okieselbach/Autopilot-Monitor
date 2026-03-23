import { ManagementSidebar } from "./ManagementSidebar";

export default function ManagementLayout({ children }: { children: React.ReactNode }) {
  return <ManagementSidebar>{children}</ManagementSidebar>;
}
