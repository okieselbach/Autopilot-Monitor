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
- [x] IngestEventsFunction → `ISessionRepository` + `IMetricsRepository` + `IMaintenanceRepository` + `IRuleRepository` + `IVulnerabilityRepository`
- [x] Auth, Progress, Feedback Functions → appropriate repos

## Phase 2b: Migrate Domain Services ✅
- [x] `RuleEngine` → `IRuleRepository` + `ISessionRepository`
- [x] `AnalyzeRuleService` → `IRuleRepository`
- [x] `GatherRuleService` → `IRuleRepository`
- [x] `ImeLogPatternService` → `IRuleRepository`
- [x] `UsageMetricsService` → `IMetricsRepository` + `IMaintenanceRepository`
- [x] `PlatformMetricsService` → `ISessionRepository`
- [x] `MaintenanceService` + `.Aggregation` → `IMaintenanceRepository` + `ISessionRepository` + `IMetricsRepository`

## Phase 3: Migrate Remaining Files ✅
- [x] All 8 Vulnerability Functions → `IVulnerabilityRepository`
- [x] `ReseedFromGitHubFunction` → `IVulnerabilityRepository` + `IRuleRepository`
- [x] `ApiKeyManagementFunction` → `IAdminRepository`
- [x] `ApiKeyMiddleware` → `IAdminRepository`
- [x] `BlockedDeviceService` → own TableClient (removed TableStorageService dep)
- [x] `BlockedVersionService` → own TableClient (removed TableStorageService dep)
- [x] `SessionReportService` → own TableClient (removed TableStorageService dep)

## Current State
**Zero** functions or services inject `TableStorageService` directly.
`TableStorageService` is only referenced by:
- Its own 6 partial class files (the implementation)
- 6 `DataAccess/TableStorage/` repository implementations (wrap it)
- `Program.cs` (DI registration)
- `TableInitializerService` (startup)
- `VulnerabilityCorrelationService` (uses `GetTableClient()` — future migration)

## Phase 4: Future — Config/Admin Services to Repos
These services manage their own `TableServiceClient`. Migrate to repository interfaces when needed:
- [ ] `TenantConfigurationService` → `IConfigRepository`
- [ ] `AdminConfigurationService` → `IConfigRepository`
- [ ] `PreviewWhitelistService` → `IConfigRepository`
- [ ] `GlobalAdminService` → `IAdminRepository`
- [ ] `TenantAdminsService` → `IAdminRepository`
- [ ] `BootstrapSessionService` → `IBootstrapRepository`
- [ ] `GlobalNotificationService` → `INotificationRepository`
- [ ] `VulnerabilityCorrelationService` → `IVulnerabilityRepository`

## Phase 5: Event Streaming (When Ready)
- [ ] Implement `EventHubPublisher` (or `ServiceBusPublisher`)
- [ ] Register via `services.AddEventStreaming<EventHubPublisher>()`
- [ ] Define event schemas for key domain events

## Phase 6: Cosmos DB Migration (When Ready)
- [ ] Create `CosmosSessionRepository`, `CosmosRuleRepository`, etc.
- [ ] Create `AddCosmosDataAccess()` extension method
- [ ] Replace inverted-tick indexing with native ORDER BY DESC
- [ ] Swap `AddTableStorageDataAccess()` → `AddCosmosDataAccess()` in Program.cs
