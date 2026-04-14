export interface AdminConfiguration {
  partitionKey: string;
  rowKey: string;
  lastUpdated: string;
  updatedBy: string;
  globalRateLimitRequestsPerMinute: number;
  platformStatsBlobSasUrl?: string;
  collectorIdleTimeoutMinutes?: number;
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
}

export interface OpsAlertRule {
  eventType: string;
  minSeverity: string;
  enabled: boolean;
}
