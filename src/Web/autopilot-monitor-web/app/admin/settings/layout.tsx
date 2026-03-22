import { SettingsSidebar } from "./SettingsSidebar";

export default function SettingsLayout({ children }: { children: React.ReactNode }) {
  return <SettingsSidebar>{children}</SettingsSidebar>;
}
