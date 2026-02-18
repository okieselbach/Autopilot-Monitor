import type { Metadata } from "next";

export const metadata: Metadata = {
  title: {
    absolute: "Autopilot Monitor – Real-Time Windows Autopilot Monitoring",
  },
  description:
    "Real-time monitoring and troubleshooting for Windows Autopilot deployments. Track every enrollment phase, detect issues automatically with Analyze Rules, and resolve failures faster.",
  keywords: [
    "Windows Autopilot monitoring",
    "Autopilot deployment visibility",
    "Intune Autopilot analytics",
    "Windows enrollment tracking",
    "Autopilot troubleshooting",
    "Autopilot real-time monitoring",
    "Autopilot Monitor",
    "Windows Autopilot dashboard",
    "Autopilot failure detection",
    "enrollment phase tracking",
  ],
  openGraph: {
    title: "Autopilot Monitor – Real-Time Windows Autopilot Monitoring",
    description:
      "Real-time monitoring and troubleshooting for Windows Autopilot deployments. Track every enrollment phase, detect issues automatically, and resolve failures faster.",
    url: "https://autopilotmonitor.com/landing",
    type: "website",
  },
  twitter: {
    title: "Autopilot Monitor – Real-Time Windows Autopilot Monitoring",
    description:
      "Real-time monitoring and troubleshooting for Windows Autopilot deployments. Track every enrollment phase, detect issues automatically, and resolve failures faster.",
  },
  alternates: {
    canonical: "https://autopilotmonitor.com/landing",
  },
};

const jsonLd = {
  "@context": "https://schema.org",
  "@type": "SoftwareApplication",
  name: "Autopilot Monitor",
  description:
    "Real-time monitoring and troubleshooting platform for Windows Autopilot deployments. Gives IT teams full visibility into enrollment phases, app progress, errors, and timelines.",
  applicationCategory: "BusinessApplication",
  operatingSystem: "Web",
  offers: {
    "@type": "Offer",
    price: "0",
    priceCurrency: "USD",
  },
  author: {
    "@type": "Person",
    name: "Oliver Kieselbach",
    url: "https://www.linkedin.com/in/oliver-kieselbach/",
  },
  url: "https://autopilotmonitor.com/landing",
  codeRepository: "https://github.com/okieselbach/Autopilot-Monitor",
  keywords:
    "Windows Autopilot, Intune, enrollment monitoring, autopilot troubleshooting, Windows deployment",
};

export default function LandingLayout({ children }: { children: React.ReactNode }) {
  return (
    <>
      <script
        type="application/ld+json"
        dangerouslySetInnerHTML={{ __html: JSON.stringify(jsonLd) }}
      />
      {children}
    </>
  );
}
