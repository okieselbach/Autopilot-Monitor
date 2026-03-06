export interface Session {
  sessionId: string;
  tenantId: string;
  serialNumber: string;
  deviceName: string;
  manufacturer: string;
  model: string;
  startedAt: string;
  status: string;
  currentPhase: number;
  eventCount: number;
  durationSeconds: number;
  failureReason?: string;
  isPreProvisioned?: boolean;
  isHybridJoin?: boolean;
  agentVersion?: string;
  geoCountry?: string;
  geoRegion?: string;
  geoCity?: string;
  osBuild?: string;
  osEdition?: string;
  osLanguage?: string;
}
