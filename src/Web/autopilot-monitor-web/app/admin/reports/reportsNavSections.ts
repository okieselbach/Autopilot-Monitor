export const REPORTS_NAV_SECTIONS = [
  { id: "session-reports", label: "Session Reports", description: "Sessions reported by Tenant Admins for analysis" },
  { id: "user-feedback", label: "User Feedback", description: "Product feedback from users" },
  { id: "session-export", label: "Session Export", description: "Export session event data" },
] as const;

export type ReportsSectionId = (typeof REPORTS_NAV_SECTIONS)[number]["id"];
