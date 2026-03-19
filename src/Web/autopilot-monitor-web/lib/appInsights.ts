import { ApplicationInsights } from "@microsoft/applicationinsights-web";

let appInsights: ApplicationInsights | null = null;

const telemetryConfig = {
  tenantId: null as string | null,
  isAdmin: false,
  isGalacticAdmin: false,
  theme: "light" as "light" | "dark",
};

export function initAppInsights(connectionString: string) {
  if (appInsights || !connectionString || typeof window === "undefined") return;

  appInsights = new ApplicationInsights({
    config: {
      connectionString,
      disableCookiesUsage: true,
      enableAutoRouteTracking: true,
      disableFetchTracking: false,
      disablePageUnloadEvents: ["unload"],
    },
  });

  appInsights.addTelemetryInitializer((envelope) => {
    envelope.data = envelope.data ?? {};
    if (telemetryConfig.tenantId) envelope.data["tenantId"] = telemetryConfig.tenantId;
    envelope.data["isAdmin"] = telemetryConfig.isAdmin;
    envelope.data["isGalacticAdmin"] = telemetryConfig.isGalacticAdmin;
    envelope.data["theme"] = telemetryConfig.theme;
  });

  appInsights.loadAppInsights();
}

export function setTelemetryContext(
  tenantId: string | null,
  isAdmin: boolean,
  isGalacticAdmin: boolean,
  theme: "light" | "dark"
) {
  telemetryConfig.tenantId = tenantId;
  telemetryConfig.isAdmin = isAdmin;
  telemetryConfig.isGalacticAdmin = isGalacticAdmin;
  telemetryConfig.theme = theme;
}

export function trackEvent(
  name: string,
  properties?: Record<string, string | number | boolean>
) {
  appInsights?.trackEvent({ name }, properties);
}
