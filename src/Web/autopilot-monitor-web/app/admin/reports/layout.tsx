import { ReportsSidebar } from "./ReportsSidebar";

export default function ReportsLayout({ children }: { children: React.ReactNode }) {
  return <ReportsSidebar>{children}</ReportsSidebar>;
}
