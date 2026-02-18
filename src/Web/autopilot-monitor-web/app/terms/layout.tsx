import type { Metadata } from "next";

export const metadata: Metadata = {
  title: "Terms of Use",
  description:
    "Terms of Use for Autopilot Monitor. Read the usage terms and conditions for the Windows Autopilot monitoring platform.",
  openGraph: {
    title: "Terms of Use – Autopilot Monitor",
    description: "Terms of Use for Autopilot Monitor. Read the usage terms and conditions.",
    url: "https://autopilotmonitor.com/terms",
  },
  alternates: {
    canonical: "https://autopilotmonitor.com/terms",
  },
  robots: {
    index: true,
    follow: false,
  },
};

export default function TermsLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
