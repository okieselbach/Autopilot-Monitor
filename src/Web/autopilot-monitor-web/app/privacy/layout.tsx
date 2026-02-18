import type { Metadata } from "next";

export const metadata: Metadata = {
  title: "Privacy Policy",
  description:
    "Privacy Policy for Autopilot Monitor. Learn how we collect, store, and protect your data when using the Windows Autopilot monitoring platform.",
  openGraph: {
    title: "Privacy Policy – Autopilot Monitor",
    description:
      "Privacy Policy for Autopilot Monitor. Learn how we collect, store, and protect your data.",
    url: "https://autopilotmonitor.com/privacy",
  },
  alternates: {
    canonical: "https://autopilotmonitor.com/privacy",
  },
  robots: {
    index: true,
    follow: false,
  },
};

export default function PrivacyLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
