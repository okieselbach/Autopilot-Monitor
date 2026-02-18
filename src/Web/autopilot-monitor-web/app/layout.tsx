import type { Metadata } from "next";
import { Inter } from "next/font/google";
import "./globals.css";
import { AuthProvider } from "../contexts/AuthContext";
import { SignalRProvider } from "../contexts/SignalRContext";
import { TenantProvider } from "../contexts/TenantContext";
import { NotificationProvider } from "../contexts/NotificationContext";
import { ThemeProvider } from "../contexts/ThemeContext";
import Navbar from "../components/Navbar";

const inter = Inter({ subsets: ["latin"] });

export const metadata: Metadata = {
  metadataBase: new URL("https://autopilotmonitor.com"),
  title: {
    default: "Autopilot Monitor",
    template: "%s | Autopilot Monitor",
  },
  description:
    "Real-time monitoring and troubleshooting for Windows Autopilot deployments. Track enrollment phases, detect issues automatically, and resolve failures faster.",
  keywords: [
    "Windows Autopilot",
    "Autopilot monitoring",
    "Windows enrollment monitoring",
    "Intune Autopilot",
    "Autopilot troubleshooting",
    "Autopilot deployment",
    "Windows device enrollment",
    "Autopilot analytics",
    "OOBE monitoring",
    "Autopilot ESP",
    "Windows Autopilot deployment",
    "Intune enrollment monitoring",
  ],
  authors: [{ name: "Oliver Kieselbach", url: "https://www.linkedin.com/in/oliver-kieselbach/" }],
  creator: "Oliver Kieselbach",
  openGraph: {
    type: "website",
    locale: "en_US",
    url: "https://autopilotmonitor.com",
    siteName: "Autopilot Monitor",
    title: "Autopilot Monitor – Real-Time Windows Autopilot Monitoring",
    description:
      "Real-time monitoring and troubleshooting for Windows Autopilot deployments. Track enrollment phases, detect issues automatically, and resolve failures faster.",
  },
  twitter: {
    card: "summary_large_image",
    title: "Autopilot Monitor – Real-Time Windows Autopilot Monitoring",
    description:
      "Real-time monitoring and troubleshooting for Windows Autopilot deployments. Track enrollment phases, detect issues automatically, and resolve failures faster.",
  },
  robots: {
    index: true,
    follow: true,
    googleBot: {
      index: true,
      follow: true,
    },
  },
  icons: {
    icon: "/icon.svg",
    shortcut: "/icon.svg",
    apple: "/icon.svg",
  },
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en">
      <body className={inter.className}>
        <ThemeProvider>
          <AuthProvider>
            <NotificationProvider>
              <TenantProvider>
                <SignalRProvider>
                  <Navbar />
                  {children}
                </SignalRProvider>
              </TenantProvider>
            </NotificationProvider>
          </AuthProvider>
        </ThemeProvider>
      </body>
    </html>
  );
}
