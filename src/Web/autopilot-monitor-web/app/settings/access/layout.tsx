import { AccessSidebar } from "./AccessSidebar";

export default function AccessLayout({ children }: { children: React.ReactNode }) {
  return <AccessSidebar>{children}</AccessSidebar>;
}
