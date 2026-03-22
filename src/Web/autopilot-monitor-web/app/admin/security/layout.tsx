import { SecuritySidebar } from "./SecuritySidebar";

export default function SecurityLayout({ children }: { children: React.ReactNode }) {
  return <SecuritySidebar>{children}</SecuritySidebar>;
}
