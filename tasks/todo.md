# MCP Server → Azure Container App (Remote Deployment)

## Ziel
MCP Server als Remote-Service auf Azure Container Apps deployen (`autopilotmonitor-mcp`), damit OAuth mit richtiger Redirect URI funktioniert und kein lokaler Device Code Flow nötig ist.

## Plan

### Phase 1: Transport umstellen (stdio → Streamable HTTP)
- [ ] `index.ts` auf HTTP/SSE Transport umstellen (MCP SDK `StreamableHTTPServerTransport`)
- [ ] Health-Check Endpoint `/health` hinzufügen
- [ ] Port konfigurierbar via `PORT` env var (default 8080)
- [ ] Auth-Strategie anpassen: Remote Server braucht keinen Device Code Flow — der **Claude Code Client** übernimmt die OAuth-Authentifizierung und sendet das Token im MCP-Protokoll

### Phase 2: Zugriffskontrolle (MCP Access Policy + User Whitelist)
- [ ] Shared: `McpAccessPolicy` Enum — `Disabled`, `WhitelistOnly`, `AllMembers`
- [ ] Shared: `AdminConfiguration.McpAccessPolicy` Property (Default: `WhitelistOnly`)
- [ ] Backend: `McpUsers` Table Storage (Pattern wie `GlobalAdmins`) — PartitionKey="McpUsers", RowKey=UPN
- [ ] Backend: `McpUserService` mit Cache (5min TTL), `IsAllowedAsync(upn)` prüft Policy:
  - `Disabled` → immer false
  - `WhitelistOnly` → GlobalAdmin ODER McpUsers-Eintrag
  - `AllMembers` → jeder authentifizierte User
- [ ] Backend: `McpUserFunction` — CRUD Endpoints (`GET/POST/DELETE /api/admin/mcp-users`), Policy: `GlobalAdminOnly`
- [ ] Backend: `GET /api/admin/mcp-users/check` — für MCP Server Auth-Check (lightweight, cached)
- [ ] Backend: Endpoints in `EndpointAccessPolicyCatalog` registrieren
- [ ] MCP Server: Token-Validierung Middleware — JWT prüfen, UPN extrahieren, gegen Backend `/api/admin/mcp-users/check` validieren
- [ ] MCP Server: Per-User Rate Limiting (in-memory, z.B. 60 req/min pro UPN, konfigurierbar via `MCP_RATE_LIMIT_PER_MINUTE`)

### Phase 3: Container-Setup
- [ ] `Dockerfile` erstellen (Node.js slim, multi-stage build, mit `@huggingface/transformers`)
- [ ] `.dockerignore` erstellen
- [ ] Lokal testen: `docker build` + `docker run`

### Phase 4: Azure Container App Deployment
- [ ] Container App Konfiguration (YAML oder Bicep) erstellen
- [ ] Environment Variables definieren (`AUTOPILOT_API_URL`, `PORT`, etc.)
- [ ] Container App erstellen: `autopilotmonitor-mcp`
- [ ] VS Code Azure Extension: Deploy to Container App testen

### Phase 5: OAuth für Remote MCP
- [ ] Entra App Registration: Redirect URI auf Container App URL setzen
- [ ] MCP Server OAuth Provider Metadata implementieren (`/.well-known/oauth-authorization-server`)
- [ ] Claude Code `.mcp.json` auf Remote URL umstellen

### Phase 6: Web UI — MCP Verwaltung
- [ ] Settings-Seite: MCP Access Policy Toggle (Disabled / WhitelistOnly / AllMembers)
- [ ] Settings-Seite: MCP Users Section (nur sichtbar wenn Policy = WhitelistOnly)
  - [ ] User-Liste mit UPN, AddedBy, AddedDate, IsEnabled
  - [ ] Add User (UPN Eingabe + Add Button)
  - [ ] Remove User (Delete Button mit Confirmation)
  - [ ] Enable/Disable Toggle
- [ ] Success/Error Notifications (bestehendes Pattern mit `error`/`successMessage` State)

### Phase 7: Aufräumen & Test
- [ ] Device Code Flow Code entfernen (nicht mehr nötig bei Remote)
- [ ] Lokale stdio-Variante als Fallback behalten? (Entscheidung)
- [ ] End-to-End Test: MCP Tools aus VS Code Claude Extension aufrufen

## Architektur-Entscheidungen

- **Container Apps Consumption Plan** — Scale to Zero, praktisch kostenlos bei sporadischer Nutzung
- **Name**: `autopilotmonitor-mcp` (konsistent mit `autopilotmonitor-api`)
- **Search Backend**: Vector Search (`@huggingface/transformers`, Xenova/all-MiniLM-L6-v2) — semantische Suche über Rules/Knowledge Base
- **Auth**: OAuth wird vom Claude Code Client gehandhabt — der MCP Server validiert das Bearer Token
- **Zugriffskontrolle**: 3-stufige Policy via `AdminConfiguration.McpAccessPolicy`
  - `Disabled` — MCP komplett aus
  - `WhitelistOnly` (Default) — GlobalAdmins + explizit freigeschaltete McpUsers
  - `AllMembers` — jeder authentifizierte User
  - Pattern identisch zu `GlobalAdmins` Table (UPN-basiert, IsEnabled Flag, Cache)
  - GlobalAdmins haben automatisch Zugriff (kein separater Eintrag nötig)
  - Policy-Wechsel = Config-Flip im Settings UI, kein Deployment nötig

---

# Data Access Layer — Migration Plan (abgeschlossen)

<details>
<summary>Phase 1-4 ✅ abgeschlossen</summary>

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

</details>

## Phase 5: Event Streaming (When Ready)
- [ ] Implement `EventHubPublisher` (or `ServiceBusPublisher`)
- [ ] Register via `services.AddEventStreaming<EventHubPublisher>()`
- [ ] Define event schemas for key domain events

## Phase 6: Cosmos DB Migration (When Ready)
- [ ] Create `CosmosSessionRepository`, `CosmosRuleRepository`, etc.
- [ ] Create `AddCosmosDataAccess()` extension method
- [ ] Replace inverted-tick indexing with native ORDER BY DESC
- [ ] Swap `AddTableStorageDataAccess()` → `AddCosmosDataAccess()` in Program.cs
