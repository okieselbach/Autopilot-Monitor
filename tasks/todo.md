# Data Access Layer — Migration Plan

## Phase 1: Foundation ✅
- [x] Define 10 Repository Interfaces in `Shared/DataAccess/`
- [x] Define `IDataEventPublisher` + `NullDataEventPublisher`
- [x] Define `IStorageInitializer`
- [x] Create Table Storage implementations
- [x] Create `DataAccessServiceExtensions` with `AddTableStorageDataAccess()`
- [x] Register DAL in `Program.cs`

## Phase 2: Migrate Core Functions ✅
- [x] All 11 Session Functions → `ISessionRepository`
- [x] All Metrics Functions → `IMetricsRepository` / `IMaintenanceRepository`
- [x] All Admin/Audit Functions → `IMaintenanceRepository`
- [x] All Rules Functions → `IRuleRepository`
- [x] IngestEventsFunction → 5 repositories
- [x] Auth, Progress, Feedback Functions → appropriate repos

## Phase 2b: Migrate Domain Services ✅
- [x] `RuleEngine`, `AnalyzeRuleService`, `GatherRuleService`, `ImeLogPatternService` → `IRuleRepository`
- [x] `UsageMetricsService` → `IMetricsRepository` + `IMaintenanceRepository`
- [x] `PlatformMetricsService` → `ISessionRepository`
- [x] `MaintenanceService` → `IMaintenanceRepository` + `ISessionRepository` + `IMetricsRepository`

## Phase 3: Migrate GetTableClient Users ✅
- [x] All 8 Vulnerability Functions → `IVulnerabilityRepository`
- [x] `ReseedFromGitHubFunction` → `IVulnerabilityRepository` + `IRuleRepository`
- [x] `ApiKeyManagementFunction` + `ApiKeyMiddleware` → `IAdminRepository`

## Phase 4: Migrate Bypass Services ✅
All services that created their own `TableServiceClient` now use repository interfaces:
- [x] `TenantConfigurationService` → `IConfigRepository`
- [x] `AdminConfigurationService` → `IConfigRepository`
- [x] `PreviewWhitelistService` → `IConfigRepository`
- [x] `TelegramNotificationService` → `IConfigRepository`
- [x] `GlobalAdminService` → `IAdminRepository`
- [x] `TenantAdminsService` → `IAdminRepository`
- [x] `BootstrapSessionService` → `IBootstrapRepository`
- [x] `GlobalNotificationService` → `INotificationRepository`
- [x] `SessionReportService` → `INotificationRepository`
- [x] `BlockedDeviceService` → `IDeviceSecurityRepository`
- [x] `BlockedVersionService` → `IDeviceSecurityRepository`
- [x] `VulnerabilityCorrelationService` → `IVulnerabilityRepository`

## Current State ✅
`new TableServiceClient()` only exists in:
- 5 Repository implementations (DataAccess/TableStorage/) — correct, they ARE the storage layer
- `TableStorageService.cs` — the core implementation used by repos
- `FeedbackFunction.cs` — domain-specific feedback CRUD (PreviewConfig table)
- `TenantOffboardFunction.cs` — bulk tenant data deletion across all tables

All 10 repository interfaces have Table Storage implementations registered in DI.

## Phase 5: Event Streaming (When Ready)
- [ ] Implement `EventHubPublisher` (or `ServiceBusPublisher`)
- [ ] Register via `services.AddEventStreaming<EventHubPublisher>()`
- [ ] Define event schemas for key domain events

## Phase 6: Cosmos DB Migration (When Ready)
- [ ] Create `CosmosSessionRepository`, `CosmosRuleRepository`, etc.
- [ ] Create `AddCosmosDataAccess()` extension method
- [ ] Replace inverted-tick indexing with native ORDER BY DESC
- [ ] Swap `AddTableStorageDataAccess()` → `AddCosmosDataAccess()` in Program.cs
