export interface DiagnosticsLogPath {
  path: string;
  description: string;
  isBuiltIn: boolean;
}

export interface TenantConfiguration {
  tenantId: string;
  lastUpdated: string;
  updatedBy: string;
  manufacturerWhitelist: string;
  modelWhitelist: string;
  validateAutopilotDevice: boolean;
  validateCorporateIdentifier?: boolean;
  allowInsecureAgentRequests?: boolean;
  dataRetentionDays: number;
  sessionTimeoutHours: number;
  customSettings?: string;
  // Agent collector settings
  enablePerformanceCollector: boolean;
  performanceCollectorIntervalSeconds: number;
  helloWaitTimeoutSeconds?: number;
  // Agent behavior
  selfDestructOnComplete?: boolean;
  keepLogFile?: boolean;
  rebootOnComplete?: boolean;
  rebootDelaySeconds?: number;
  enableGeoLocation?: boolean;
  enableImeMatchLog?: boolean;
  logLevel?: string;
  // Teams notifications (legacy)
  teamsWebhookUrl?: string;
  teamsNotifyOnSuccess?: boolean;
  teamsNotifyOnFailure?: boolean;
  // Webhook notifications (new)
  webhookProviderType?: number;
  webhookUrl?: string;
  webhookNotifyOnSuccess?: boolean;
  webhookNotifyOnFailure?: boolean;
  // Diagnostics package
  diagnosticsBlobSasUrl?: string;
  diagnosticsUploadMode?: string;
  diagnosticsLogPathsJson?: string;
  // Enrollment summary dialog
  showEnrollmentSummary?: boolean;
  enrollmentSummaryTimeoutSeconds?: number;
  enrollmentSummaryBrandingImageUrl?: string;
  enrollmentSummaryLaunchRetrySeconds?: number;
  // Script output visibility
  showScriptOutput?: boolean;
  // Agent analyzer settings
  enableLocalAdminAnalyzer?: boolean;
  localAdminAllowedAccountsJson?: string;
  // Bootstrap token
  bootstrapTokenEnabled?: boolean;
  // Unrestricted mode
  unrestrictedModeEnabled?: boolean;
  unrestrictedMode?: boolean;
}

export interface TenantAdmin {
  tenantId: string;
  upn: string;
  isEnabled: boolean;
  addedDate: string;
  addedBy: string;
  role: string | null;
  canManageBootstrapTokens: boolean;
}
