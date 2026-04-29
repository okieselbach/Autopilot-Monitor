export interface Session {
  sessionId: string;
  tenantId: string;
  serialNumber: string;
  deviceName: string;
  manufacturer: string;
  model: string;
  startedAt: string;
  completedAt?: string;
  status: string;
  currentPhase: number;
  eventCount: number;
  durationSeconds: number;
  failureReason?: string;
  /** Origin of a Failed status — "" for agent-reported, "rule:<RuleId>" for rule-based, "manual" for portal. */
  failureSource?: string;
  /** Non-null only when an administrator flipped the session manually via the portal. Values: "Succeeded" | "Failed". */
  adminMarkedAction?: string;
  enrollmentType?: string; // "v1" | "v2" — absent for sessions before this feature
  diagnosticsBlobName?: string;
  lastEventAt?: string;
  isPreProvisioned?: boolean;
  isHybridJoin?: boolean;
  isUserDriven?: boolean;
  agentVersion?: string;
  // OS details
  osName?: string;
  osBuild?: string;
  osDisplayVersion?: string;
  osEdition?: string;
  osLanguage?: string;
  // Geographic location
  geoCountry?: string;
  geoRegion?: string;
  geoCity?: string;
}
