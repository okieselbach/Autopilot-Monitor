import { McpSidebar } from "./McpSidebar";

export default function McpLayout({ children }: { children: React.ReactNode }) {
  return <McpSidebar>{children}</McpSidebar>;
}
