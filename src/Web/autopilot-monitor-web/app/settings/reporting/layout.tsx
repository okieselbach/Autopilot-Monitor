import { ReportingSidebar } from "./ReportingSidebar";

export default function ReportingLayout({ children }: { children: React.ReactNode }) {
  return <ReportingSidebar>{children}</ReportingSidebar>;
}
