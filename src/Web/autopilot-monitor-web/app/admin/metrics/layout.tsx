import { MetricsSidebar } from "./MetricsSidebar";

export default function MetricsLayout({ children }: { children: React.ReactNode }) {
  return <MetricsSidebar>{children}</MetricsSidebar>;
}
