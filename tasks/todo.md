# Data Access Layer — Migration Plan

## Phase 1: Foundation ✅
- [x] Define 10 Repository Interfaces in `Shared/DataAccess/`
- [x] Define `IDataEventPublisher` + `NullDataEventPublisher`
- [x] Define `IStorageInitializer`
- [x] Create 5 core Table Storage implementations (delegate to `TableStorageService`)
- [x] Create `DataAccessServiceExtensions` with `AddTableStorageDataAccess()`
- [x] Register DAL in `Program.cs`

## Phase 2: Migrate Core Functions to Use Interfaces
These functions currently inject `TableStorageService` directly. Migrate to inject repository interfaces instead.

### Session Functions
- [ ] `GetSessionsFunction` → `ISessionRepository`
- [ ] `GetSessionFunction` → `ISessionRepository`
- [ ] `DeleteSessionFunction` → `ISessionRepository`
- [ ] `RegisterSessionFunction` → `ISessionRepository`
- [ ] `IngestEventsFunction` → `ISessionRepository`
- [ ] `SearchSessionsFunction` → `ISessionRepository`
- [ ] `SearchSessionsByEventFunction` → `ISessionRepository`
- [ ] `SearchSessionsByCveFunction` → `ISessionRepository`

### Rule Functions
- [ ] `GetRuleResultsFunction` → `IRuleRepository`
- [ ] `RunRulesFunction` → `IRuleRepository`
- [ ] Rule management functions → `IRuleRepository`

### Metrics Functions
- [ ] `GetMetricsFunction` → `IMetricsRepository`
- [ ] `GetHistoricalMetricsFunction` → `IMetricsRepository`
- [ ] `GetPlatformStatsFunction` → `IMetricsRepository`

### Maintenance Functions
- [ ] `GetAuditLogsFunction` → `IMaintenanceRepository`
- [ ] `MaintenanceFunction` → `IMaintenanceRepository`

### Vulnerability Functions
- [ ] All vulnerability functions → `IVulnerabilityRepository`

## Phase 3: Migrate "Bypass" Services
These services create their own `TableServiceClient` instances. Create proper repository implementations and migrate.

- [ ] `TenantConfigurationService` → `IConfigRepository`
- [ ] `AdminConfigurationService` → `IConfigRepository`
- [ ] `PreviewWhitelistService` → `IConfigRepository`
- [ ] `GlobalAdminService` → `IAdminRepository`
- [ ] `TenantAdminsService` → `IAdminRepository`
- [ ] `BootstrapSessionService` → `IBootstrapRepository`
- [ ] `GlobalNotificationService` → `INotificationRepository`
- [ ] `SessionReportService` → `INotificationRepository`
- [ ] `BlockedDeviceService` → `IDeviceSecurityRepository`
- [ ] `BlockedVersionService` → `IDeviceSecurityRepository`
- [ ] `ApiKeyMiddleware` → `IAdminRepository`

## Phase 4: Remove Legacy Code
- [ ] Remove `GetTableClient()` and `GetTableServiceClient()` from `TableStorageService`
- [ ] Move `TableStorageService` logic into repository implementations
- [ ] Delete `TableStorageService` partial files once fully migrated
- [ ] Remove direct `TableServiceClient` creation from all services

## Phase 5: Event Streaming (When Ready)
- [ ] Implement `EventHubPublisher` (or `ServiceBusPublisher`)
- [ ] Register via `services.AddEventStreaming<EventHubPublisher>()`
- [ ] Define event schemas for key domain events
- [ ] Add event consumers for downstream processing

## Phase 6: Cosmos DB Migration (When Ready)
- [ ] Create `CosmosSessionRepository`, `CosmosRuleRepository`, etc.
- [ ] Create `AddCosmosDataAccess()` extension method
- [ ] Replace inverted-tick indexing with native ORDER BY DESC
- [ ] Replace denormalized indexes with Cosmos queries
- [ ] Handle 2MB document limit vs. 64KB Table Storage limit
- [ ] Swap `AddTableStorageDataAccess()` → `AddCosmosDataAccess()` in Program.cs
