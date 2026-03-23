import { IntegrationsSidebar } from "./IntegrationsSidebar";

export default function IntegrationsLayout({ children }: { children: React.ReactNode }) {
  return <IntegrationsSidebar>{children}</IntegrationsSidebar>;
}
