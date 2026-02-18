import type { Metadata } from "next";

export const metadata: Metadata = {
  title: "About",
  description:
    "Learn about Autopilot Monitor – an open-source real-time monitoring and troubleshooting platform for Windows Autopilot enrollments built by Oliver Kieselbach.",
  keywords: [
    "Autopilot Monitor overview",
    "Windows Autopilot tool",
    "open source Autopilot monitoring",
    "Oliver Kieselbach",
    "Intune Autopilot platform",
  ],
  openGraph: {
    title: "About Autopilot Monitor",
    description:
      "Learn about Autopilot Monitor – an open-source real-time monitoring and troubleshooting platform for Windows Autopilot enrollments.",
    url: "https://autopilotmonitor.com/about",
  },
  twitter: {
    title: "About Autopilot Monitor",
    description:
      "Learn about Autopilot Monitor – an open-source real-time monitoring and troubleshooting platform for Windows Autopilot enrollments.",
  },
  alternates: {
    canonical: "https://autopilotmonitor.com/about",
  },
};

export default function AboutLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
