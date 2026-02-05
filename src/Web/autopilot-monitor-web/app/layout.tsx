import type { Metadata } from "next";
import { Inter } from "next/font/google";
import "./globals.css";
import { AuthProvider } from "../contexts/AuthContext";
import { SignalRProvider } from "../contexts/SignalRContext";
import { TenantProvider } from "../contexts/TenantContext";
import { NotificationProvider } from "../contexts/NotificationContext";
import Navbar from "../components/Navbar";

const inter = Inter({ subsets: ["latin"] });

export const metadata: Metadata = {
  title: "Autopilot Monitor",
  description: "Advanced monitoring and troubleshooting for Windows Autopilot deployments",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en">
      <body className={inter.className}>
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
      </body>
    </html>
  );
}
