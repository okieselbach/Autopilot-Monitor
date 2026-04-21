export interface AdminConfiguration {
  partitionKey: string;
  rowKey: string;
  lastUpdated: string;
  updatedBy: string;
  globalRateLimitRequestsPerMinute: number;
  platformStatsBlobSasUrl?: string;
  collectorIdleTimeoutMinutes?: number;
  excessiveEventCountThreshold?: number;
  maxSessionWindowHours?: number;
  maintenanceBlockDurationHours?: number;
  opsEventRetentionDays?: number;
  diagnosticsGlobalLogPathsJson?: string;
  customSettings?: string;
  nvdApiKey?: string;
  vulnerabilityCorrelationEnabled?: boolean;
  vulnerabilityDataLastSyncUtc?: string;
  opsAlertRulesJson?: string;
  opsAlertTelegramEnabled?: boolean;
  opsAlertTelegramChatId?: string;
  opsAlertTeamsEnabled?: boolean;
  opsAlertTeamsWebhookUrl?: string;
  opsAlertSlackEnabled?: boolean;
  opsAlertSlackWebhookUrl?: string;
  allowAgentDowngrade?: boolean;
  modernDeploymentHarmlessEventIdsJson?: string;
  /**
   * Feature flag for the V2 Decision Engine index-table dual-write (Plan §M5.d).
   * When true, IngestTelemetryFunction enqueues telemetry-index-reconcile envelopes after
   * committing each primary row, and the 2h IndexReconcileTimer re-scans the last 4h as
   * a safety net. Default false — keeps pre-M5.d behaviour bit-exact until explicitly flipped.
   */
  enableIndexDualWrite?: boolean;
}

export interface OpsAlertRule {
  eventType: string;
  minSeverity: string;
  enabled: boolean;
}
