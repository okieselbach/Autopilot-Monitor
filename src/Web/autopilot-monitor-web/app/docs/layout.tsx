import type { Metadata } from "next";

export const metadata: Metadata = {
  title: "Documentation",
  description:
    "Complete setup and configuration guide for Autopilot Monitor. Learn how to deploy the bootstrapper via Intune, configure the agent, and start monitoring Windows Autopilot enrollments in real time.",
  keywords: [
    "Autopilot Monitor documentation",
    "Autopilot Monitor setup guide",
    "Intune bootstrapper deployment",
    "Autopilot agent configuration",
    "Windows Autopilot setup",
    "Autopilot Monitor install",
  ],
  openGraph: {
    title: "Documentation – Autopilot Monitor",
    description:
      "Complete setup and configuration guide for Autopilot Monitor. Deploy the bootstrapper via Intune and start monitoring Windows Autopilot enrollments.",
    url: "https://autopilotmonitor.com/docs",
  },
  twitter: {
    title: "Documentation – Autopilot Monitor",
    description:
      "Complete setup and configuration guide for Autopilot Monitor. Deploy the bootstrapper via Intune and start monitoring Windows Autopilot enrollments.",
  },
  alternates: {
    canonical: "https://autopilotmonitor.com/docs",
  },
};

export default function DocsLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
