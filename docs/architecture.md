# Autopilot Monitor – Architecture Guide

> Living document describing the system architecture, design decisions, and conventions.

---

## High-Level Overview

```text
+----------------------------------------------------------------------------------+
|                         Autopilot Monitor - Architecture                         |
+----------------------------------------------------------------------------------+

 [Admin/User Browser]
          |
          | HTTPS
          v
+-----------------------------+      OIDC        +-------------------------------+
| Web App (Next.js)           |<---------------->| Microsoft Identity (Entra ID) |
| Dashboard / Progress / Docs |                  | Auth / Authorization          |
+-----------------------------+                  +-------------------------------+
          ^    ^
          |    |
          |    | Live updates
          |    |
          |    |                         +---------------------------+
          |    +-------------------------| SignalR Hub               |
          |                              | Real-time event stream    |
          |                              +---------------------------+
          |                                          ^
          | REST / API                               |
          v                                          | events
+---------------------------+        Data &          |
| Azure Functions Backend   |------> Integrations ---+
| APIs / Ingestion / Rules  |               \
+---------------------------+                \       +---------------------------+
          ^                                   +----->| Storage Tables            |
          | mTLS                                     | Sessions / Events / Rules |
          | telemetry / events                       +---------------------------+
          |                                          +---------------------------+
+---------------------------+                        | Notifications             |
| Agent on Windows Devices  |                        | Teams / Slack / Telegram  |
| Enrollment + app progress |                        +---------------------------+
+---------------------------+
                                                     +---------------------------+
                                                     | MCP Server                |
                                                     | Azure Container App       |
                                                     +---------------------------+
```

**Core Data Flow:**
```
Device (Agent) ──NDJSON+gzip──► Azure Functions ──► Azure Table Storage
                                      │                     │
                                      ├── SignalR ─────────►│
                                      │                     │
                              Web Dashboard ◄── REST API ◄──┘
```

| Component | Technology | Purpose |
|-----------|------------|---------|
| **Agent** | .NET Framework 4.8 (Windows) | Runs on devices during enrollment, collects telemetry |
| **SummaryDialog** | .NET Framework 4.8 (WPF) | Post-enrollment summary UI (outcome, app timeline, auto-close) |
| **Backend** | Azure Functions .NET 8 (Isolated Worker) | REST API, event processing, rule engine, storage |
| **Web** | Next.js 15 + TypeScript + React 18 | Dashboard, settings, analytics, real-time UI |
| **Shared** | .NET Standard 2.0 | DTOs, models, enums, constants shared between Agent & Backend |
| **MCP Server** | Azure Container App | Model Context Protocol server for AI-assisted analysis |

---

## Table of Contents

1. [Solution Structure](#solution-structure)
2. [Agent](#agent)
3. [Backend](#backend)
4. [Web Frontend](#web-frontend)
5. [Shared Library](#shared-library)
6. [Data Model](#data-model)
7. [Security Architecture](#security-architecture)
8. [Real-Time Communication](#real-time-communication)
9. [Rules Engine](#rules-engine)
10. [Session Lifecycle](#session-lifecycle)
11. [Configuration Hierarchy](#configuration-hierarchy)
12. [Diagnostics & Upload](#diagnostics--upload)
13. [Vulnerability Management](#vulnerability-management)
14. [Notification System](#notification-system)
15. [Testing](#testing)
16. [Infrastructure & Deployment](#infrastructure--deployment)
17. [Build & Development](#build--development)

---

## Solution Structure

```
AutopilotMonitor.sln
├── src/
│   ├── Shared/AutopilotMonitor.Shared/          (netstandard2.0)
│   ├── Agent/
│   │   ├── AutopilotMonitor.Agent/              (net48, Exe – entry point)
│   │   ├── AutopilotMonitor.Agent.Core/         (net48, Lib – core logic)
│   │   ├── AutopilotMonitor.Agent.Core.Tests/   (net48, xUnit – agent unit tests)
│   │   └── AutopilotMonitor.SummaryDialog/      (net48, WPF – enrollment summary UI)
│   ├── Backend/
│   │   ├── AutopilotMonitor.Functions/          (net8.0, Azure Functions v4)
│   │   └── AutopilotMonitor.Functions.Tests/    (net8.0, xUnit)
│   └── Web/
│       └── autopilot-monitor-web/               (Next.js 15, TypeScript)
├── rules/
│   ├── gather/                                   Individual gather rule JSONs
│   ├── analyze/                                  Individual analyze rule JSONs
│   ├── ime-log-patterns/                         IME regex pattern JSONs
│   ├── schema/                                   JSON Schema definitions
│   ├── scripts/                                  combine.js
│   └── dist/                                     Combined output (embedded in Functions)
├── infra/                                        Bicep templates (MCP server)
├── scripts/
│   ├── Bootstrap/                                Intune deployment scripts
│   └── Deployment/                               CI/CD build scripts
└── .github/workflows/                            CI/CD pipelines
```

**Project References:**
```
Shared ◄── Agent.Core ◄── Agent
                         ◄── Agent.Core.Tests
                         ◄── SummaryDialog
Shared ◄── Functions ◄── Functions.Tests
Web (independent – communicates via REST + SignalR)
```

---

## Agent

### Entry Point & Modes

**File:** `src/Agent/AutopilotMonitor.Agent/Program.cs`

Four execution modes:
1. **Normal mode** (default) – Main enrollment monitoring loop
2. **`--install` mode** – Deploys agent via Scheduled Task (Intune package)
3. **`--run-gather-rules` mode** – One-shot offline data collection, then exits
4. **`--run-ime-matching` mode** – Offline IME log parsing, writes `ime_pattern_matching.log`

Partial class files: `Program.Configuration.cs`, `Program.InstallMode.cs`, `Program.GatherRulesMode.cs`, `Program.ImeMatchingMode.cs`

### Startup Sequence (Normal Mode)

1. Single-instance check (prevent duplicate agent processes)
2. Self-update: `SelfUpdater.CheckAndApplyUpdateAsync()` (downloads from Azure Blob)
3. Load configuration
4. Check enrollment-complete marker (handles post-reboot cleanup retry)
5. Check session age emergency break (zombie agent kill-switch)
6. Detect previous exit type: `clean` / `exception_crash` / `hard_kill` / `reboot_kill` / `first_run`
7. `FetchRemoteConfig()` – Backend config with 15s timeout, disk cache fallback
8. `RegisterSessionAsync()` – Register session (5 retries, exponential backoff)
9. `StartWatching()` – Enable FileSystemWatcher for event spool
10. `StartEventCollectors()` – EspAndHelloTracker (always on)
11. `StartOptionalCollectors()` – PerformanceCollector, AgentSelfMetrics, EnrollmentTracker, DesktopArrivalDetector
12. `StartGatherRuleExecutor()` – Backend-defined data collection rules
13. `InitializeAnalyzers()` + `RunStartupAnalyzers()` – Security baseline

### Key Services

| Service | Location | Responsibility |
|---------|----------|----------------|
| `MonitoringService` | `Core/Monitoring/Core/` | Main orchestrator: starts/stops collectors, manages upload loop (3 partial files) |
| `BackendApiClient` | `Core/Monitoring/Network/` | HTTP client with mTLS cert + hardware headers + bootstrap token |
| `EventSpool` | `Core/Monitoring/Core/` | Offline event storage (JSON files), FileSystemWatcher-based |
| `EnrollmentTracker` | `Core/Monitoring/Tracking/` | Central enrollment state machine, 3 completion paths (4 partial files) |
| `ImeLogTracker` | `Core/Monitoring/Tracking/` | Parses IME logs with backend-provided regex patterns (3 partial files) |
| `EspAndHelloTracker` | `Core/Monitoring/Collectors/` | ESP state + Hello provisioning monitoring (4 partial files) |
| `DesktopArrivalDetector` | `Core/Monitoring/Collectors/` | Polls for explorer.exe under real user (non-SYSTEM, non-DefaultUser*) |
| `PerformanceCollector` | `Core/Monitoring/Collectors/` | CPU/Memory/Disk/Network metrics |
| `AgentSelfMetricsCollector` | `Core/Monitoring/Collectors/` | Agent process self-telemetry |
| `GatherRuleExecutor` | `Core/Monitoring/Collectors/` | Executes backend-defined data collection rules |
| `DeviceInfoCollector` | `Core/Monitoring/Collectors/` | Hardware spec + network/security info (3 partial files) |
| `DiagnosticsPackageService` | `Core/Monitoring/Core/` | Creates ZIP + uploads via short-lived SAS URL |
| `RemoteConfigService` | `Core/Configuration/` | Fetches & caches backend config with disk fallback |
| `SessionPersistence` | `Core/Monitoring/Core/` | Persists session ID, sequence counter, WhiteGlove state |
| `DistressReporter` | `Core/Monitoring/Network/` | Pre-auth distress signals (no cert required) |
| `EmergencyReporter` | `Core/Monitoring/Network/` | Posts AgentErrorReport via BackendApiClient |
| `GeoLocationService` | `Core/Monitoring/Network/` | IP-based device location lookup |
| `NtpTimeCheckService` | `Core/Monitoring/Network/` | UDP NTP query to detect clock skew |
| `TimezoneService` | `Core/Monitoring/Network/` | Sets Windows timezone via `tzutil /s` |

### Analyzers

| Analyzer | Purpose |
|----------|---------|
| `LocalAdminAnalyzer` | Enumerates local admin accounts at start+shutdown, flags unexpected accounts |
| `SoftwareInventoryAnalyzer` | Captures installed software from registry at start+shutdown, emits `software_inventory_analysis` events |

### Gather Rule Collectors

Each implements `IGatherRuleCollector`:

| Collector | Purpose |
|-----------|---------|
| `RegistryCollector` | Read registry values |
| `WmiCollector` | WMI queries |
| `EventLogCollector` | Windows Event Log queries |
| `FileCollector` | Collect file contents |
| `CommandCollector` | Run allowlisted commands |
| `LogParserCollector` | Parse log files |
| `JsonCollector` | Collect JSON from file/registry |
| `XmlCollector` | Parse XML files |

Security enforced by `GatherRuleGuards` (allowed targets) and `DiagnosticsPathGuards` (path validation, respects `UnrestrictedMode`).

### Directory Layout (Agent.Core)

```
Configuration/       Config loading, remote config service
Logging/             AgentLogger (file + optional console)
Monitoring/
├── Analyzers/       Security checks (LocalAdminAnalyzer, SoftwareInventoryAnalyzer)
├── Collectors/      Data collectors (ESP, Hello, Performance, GatherRules, Diagnostics, Desktop)
│   └── GatherCollectors/  Per-type gather rule collectors
├── Core/            Orchestration (MonitoringService, EventSpool, SessionPersistence, CleanupService)
├── Interop/         P/Invoke declarations (process creation, registry change notifications)
├── Network/         API client, distress/emergency reporters, geo-location, NTP, network metrics
├── Replay/          Log replay for testing/simulation
└── Tracking/        Enrollment state machine, IME parser, script/app tracking, state persistence
Security/            Certificate helper, enrollment awaiter, hardware info
```

### Event Collection & Upload

1. Collector emits `EnrollmentEvent` → sequence number auto-assigned (thread-safe Interlocked)
2. Event saved to spool as JSON file: `event_{timestamp}_{sequence}.json`
3. FileSystemWatcher triggers debounce timer (configurable `UploadIntervalSeconds`, default 30s)
4. Batch upload: NDJSON + gzip compression, max 100 events per batch
5. Response handling: `DeviceKillSignal` → self-destruct; `DeviceBlocked` → stop uploads

### Idle Timeout & Lifetime

- **Collector Idle Timeout**: Default 15min (`CollectorIdleTimeoutMinutes`). Tracks `_lastRealEventTime`. "Real" events = everything except `performance_snapshot`, `agent_metrics_snapshot`, and `*_stopped` variants. Idle collectors auto-restart on new activity.
- **Agent Max Lifetime**: Default 360min/6h (`AgentMaxLifetimeMinutes`). Emits `enrollment_failed` with `failureType="agent_timeout"`.
- **Session Age Emergency Break**: 48h absolute max. Checked at startup, triggers cleanup.

### Agent Data Paths

```
%ProgramData%\AutopilotMonitor\
├── session.id, session.seq, session.created
├── whiteglove.complete
├── bootstrap-config.json
├── Logs/agent.log
├── Spool/event_*.json
├── Config/remote-config.json
└── State/
    ├── enrollment-state.json
    ├── ime-tracker-state.json
    └── enrollment-complete.marker
```

---

## Backend

### Azure Functions Setup

- **Runtime:** .NET 8 Isolated Worker, Azure Functions v4
- **Route Prefix:** `/api`
- **Monitoring:** Application Insights with sampling
- **Entry Point:** `src/Backend/AutopilotMonitor.Functions/Program.cs`

### Middleware Pipeline

Registered in this order:
1. `RequestTelemetryMiddleware` – Wraps all requests for Application Insights telemetry
2. `CorrelationIdMiddleware` – Propagates or generates `X-Correlation-Id`
3. `AuthenticationMiddleware` – Validates JWT Bearer tokens via Azure AD OIDC metadata; caches per-tenant config managers (max 100); tracks MCP usage via `X-Client-Source: mcp` header
4. `PolicyEnforcementMiddleware` – Catalog-driven authorization (fail-closed: unregistered routes get 403); sets `RequestContext` (IsGlobalAdmin, IsTenantAdmin, UserRole)
5. `UserRateLimitMiddleware` – Per-user API rate limiting for human users

### Endpoints (~140 Functions)

**Agent-to-Cloud (device auth via cert/bootstrap):**

| Route | Method | Purpose |
|-------|--------|---------|
| `/agent/register-session` | POST | Register new enrollment session |
| `/agent/ingest` | POST | Upload events (NDJSON+gzip) |
| `/agent/config` | GET | Fetch agent configuration |
| `/agent/upload-url` | POST | Get short-lived SAS URL for diagnostics |
| `/agent/error` | POST | Report agent errors |
| `/agent/distress` | POST | Pre-auth distress signal (no cert required) |

**Bootstrap (pre-MDM auth):**

| Route | Method | Purpose |
|-------|--------|---------|
| `/bootstrap/sessions` | POST/GET | Create/list bootstrap sessions |
| `/bootstrap/sessions/{code}` | DELETE | Revoke bootstrap session |
| `/bootstrap/validate/{code}` | GET | Validate bootstrap code (public) |
| `/bootstrap/register-session` | POST | Register via bootstrap token |
| `/bootstrap/ingest` | POST | Ingest via bootstrap token |
| `/bootstrap/config` | POST | Config via bootstrap token |
| `/bootstrap/error` | POST | Error report via bootstrap token |

**Web Portal (JWT auth):**

| Category | Key Routes |
|----------|------------|
| **Sessions** | `GET /sessions`, `GET /sessions/{id}`, `GET /sessions/{id}/events`, `DELETE /sessions/{id}`, `POST /sessions/{id}/mark-failed`, `POST /sessions/{id}/mark-succeeded` |
| **Search** | `GET /search/quick`, `GET /search/sessions`, `GET /search/sessions-by-event`, `GET /search/sessions-by-cve` |
| **Rules** | CRUD for `/rules/gather`, `/rules/analyze`, `/rules/ime-log-patterns`, `POST /rules/reseed-from-github`, `GET /rules/results` |
| **Config** | `GET/PUT /global/config`, `GET/PUT /config/{tenantId}`, `GET /config/all`, `GET /global/config/plan-tiers` |
| **Auth** | `GET /auth/me` |
| **Tenants** | CRUD `/tenants/{id}/admins`, `POST /tenants/{id}/offboard` |
| **Devices** | `POST /devices/block`, `DELETE /devices/block/{serial}`, `GET /devices/blocked`, `GET /global/devices/blocked` |
| **Versions** | `POST /versions/block`, `GET /versions/blocked`, `DELETE /versions/block/{pattern}` |
| **Reports** | `POST /sessions/{id}/report`, `GET/POST /global/session-reports`, distress reports |
| **Metrics** | `GET /metrics/usage`, `GET /metrics/app`, `GET /metrics/summary`, `GET /metrics/geographic`, `GET /metrics/geographic/sessions`, `GET /stats/platform` |
| **Global** | `/global/metrics/*`, `/global/audit/logs`, `/global/session-reports`, `/global/notifications` (CRUD + dismiss), `/global/mcp-users` |
| **Vulnerability** | `GET /sessions/{id}/vulnerability-report`, CPE mapping CRUD, `POST /vulnerability/sync`, software inventory |
| **Progress** | `GET /progress/sessions` |
| **Diagnostics** | `GET /diagnostics/download-url` |
| **SignalR** | `POST /realtime/negotiate`, `POST /realtime/groups/join`, `POST /realtime/groups/leave` |
| **Health** | `GET /health`, `GET /health/detailed` |
| **Feedback** | `POST /feedback` |
| **MCP** | `GET/POST/DELETE /global/mcp-users`, MCP usage metrics |
| **Raw/Debug** | AppInsights query proxy, raw events/sessions query, table query |
| **Autopilot Validation** | Consent URL + status for Graph API access |

**Timer Triggers:**
- `MaintenanceFunction` – Every 2 hours (`0 0 */2 * * *`): stale session detection, metrics aggregation, data cleanup
- `VulnerabilityDataSyncFunction` – Periodic CVE data sync from NVD/MSRC/CISA KEV

### Key Backend Services

| Service | Responsibility |
|---------|----------------|
| `TableStorageService` | Core data access for all 33 Azure Tables (split across 5 partial files) |
| `TenantConfigurationService` | Per-tenant config with 5-min cache |
| `AdminConfigurationService` | Global config with 5-min cache, syncs rate limits to tenants |
| `RateLimitService` | In-memory sliding window rate limiting (1-min window) |
| `DistressRateLimitService` | Rate limiter for the unauthenticated distress endpoint |
| `SecurityValidator` | Centralized request validation (cert → rate limit → hardware → APV) |
| `RuleEngine` | Server-side analyze rule evaluation with confidence scoring |
| `MaintenanceService` | Cleanup, metrics aggregation, stale session detection (+ Aggregation partial) |
| `BootstrapSessionService` | Bootstrap token lifecycle (create, validate, revoke) |
| `BlockedDeviceService` | Device block/kill signal management |
| `BlockedVersionService` | Version-based block/kill rules with wildcard and ceiling patterns |
| `SessionReportService` | Report ZIP generation + Blob upload |
| `BlobStorageService` | Azure Blob Storage (diagnostics upload SAS URLs) |
| `GraphTokenService` | MS Graph token acquisition for Autopilot device validation |
| `AutopilotDeviceValidator` | Validates serial against Intune Autopilot device list |
| `CorporateIdentifierValidator` | Validates against Intune Corporate Device Identifiers |
| `GlobalNotificationService` | Persistent in-app notifications for Global Admins |
| `HealthCheckService` | Health checks for Storage, Processing, and Agent binary availability |
| `PreviewWhitelistService` | Private Preview tenant whitelist with 5-min cache |
| `McpUserService` | MCP API user management + access control |
| `TenantAdminsService` | Tenant member/admin management (roles: Admin, Operator, Viewer) |
| `GlobalAdminService` | Global admin lookup |
| `UsageMetricsService` | Daily/rolling usage metrics |
| `PlatformMetricsService` | Platform-wide stats |
| `VulnerabilityCorrelationService` | Matches installed software against CVE data |
| `WebhookNotificationService` | Dispatches enrollment notifications via webhook (Teams/Slack) |
| `TelegramNotificationService` | Telegram bot notifications |
| `ResendEmailService` | Transactional emails via Resend API |
| `SignalRNotificationService` | Pushes real-time updates to connected web clients |
| `EventTimestampValidator` | Clamps/validates event timestamps (preserves originals) |
| `GitHubRuleRepository` | Fetches rules from GitHub for reseed |

### Data Access Layer

Repository pattern via interfaces in `AutopilotMonitor.Shared/DataAccess/`:

| Repository | Purpose |
|------------|---------|
| `TableSessionRepository` | Sessions + events + indexes |
| `TableConfigRepository` | Tenant/admin configuration |
| `TableRuleRepository` | Gather/analyze rules, IME patterns, rule results |
| `TableMetricsRepository` | Usage metrics, app install summaries, platform stats |
| `TableAdminRepository` | Audit logs, global/tenant admins, MCP users |
| `TableBootstrapRepository` | Bootstrap sessions |
| `TableDeviceSecurityRepository` | Blocked devices, blocked versions |
| `TableDistressReportRepository` | Distress reports |
| `TableMaintenanceRepository` | Maintenance operations |
| `TableNotificationRepository` | Global notifications |
| `TableUserUsageRepository` | Per-user API usage tracking |
| `TableVulnerabilityRepository` | CVE data, CPE mappings, software inventory |

### Event Processing Pipeline

```
Agent POST /api/agent/ingest (NDJSON+gzip)
    │
    ├─ SecurityValidator.ValidateRequestAsync()
    │   ├─ Tenant existence & suspension
    │   ├─ Bootstrap token gate (if present)
    │   ├─ Certificate validation against Intune CAs
    │   ├─ Rate limiting (sliding window)
    │   ├─ Hardware whitelist
    │   └─ Autopilot device validation (optional)
    │
    ├─ BlockedDeviceService.IsBlockedAsync() → kill/block signal
    │
    ├─ ParseNdjsonGzipRequest() → decompress + parse events
    │
    ├─ StampServerFields() → ReceivedAt, TenantId, SessionId
    │
    ├─ EventTimestampValidator → clamp/validate timestamps
    │
    ├─ TableStorageService.StoreEventsBatchAsync()
    │
    ├─ ClassifyEvents()
    │   ├─ Extract geo-location
    │   ├─ Track app installs → AppInstallSummaries table
    │   └─ Detect enrollment completion/failure
    │
    ├─ UpdateSessionStatusAsync() → merge session row
    │
    ├─ RuleEngine.AnalyzeSessionAsync() (on enrollment end)
    │   └─ StoreRuleResultAsync() → RuleResults table
    │
    ├─ VulnerabilityCorrelation (on software inventory events)
    │
    ├─ WebhookNotificationService (Teams/Slack on enrollment complete/fail)
    │
    └─ SignalR broadcasts:
        ├─ "eventReceived" → tenant-{tenantId}
        ├─ "sessionStatusChanged" → tenant-{tenantId}
        └─ "ruleResultReceived" → tenant-{tenantId}
```

---

## Web Frontend

### Technology Stack

- **Framework:** Next.js 15.1.6 (App Router)
- **Language:** TypeScript 5.7.3
- **UI:** React 18.2.0 + Tailwind CSS 3.4.17 (dark mode via `class` strategy)
- **Auth:** MSAL.js 3.28 (`@azure/msal-browser` + `@azure/msal-react`)
- **Real-time:** `@microsoft/signalr` 10.0.0
- **Maps:** Leaflet 1.9.4 + react-leaflet
- **Compression:** fflate 0.8.2
- **Telemetry:** Application Insights Web 3.3.11

### Page Routes

| Path | Purpose | Auth | Role |
|------|---------|------|------|
| `/` | Landing page with platform stats | Public | — |
| `/dashboard` | Session list, real-time updates | Yes | Any |
| `/sessions/[sessionId]` | Session detail, event timeline, analysis, vulnerability report | Yes | Any |
| `/diagnosis/[sessionId]` | Simplified diagnosis with badges | Yes | Any |
| `/fleet-health` | App metrics, fleet analytics | Yes | Member |
| `/geographic-performance` | Geo map + session drill-down | Yes | Member |
| `/analyze-rules` | Analyze rule CRUD | Yes | Tenant Admin |
| `/gather-rules` | Gather rule CRUD | Yes | Tenant Admin |
| `/ime-log-patterns` | IME pattern management | Yes | Tenant Admin |
| `/audit` | Audit log viewer | Yes | Member |
| `/usage-metrics` | Tenant usage analytics | Yes | Member |
| **Settings** | | | |
| `/settings/tenant/[section]` | Access management, Autopilot validation, bootstrap, hardware whitelist, notifications | Yes | Tenant Admin |
| `/settings/agent/[section]` | Agent analyzers, agent settings, diagnostics, unrestricted mode | Yes | Tenant Admin |
| `/settings/management/[section]` | Data management, offboarding | Yes | Tenant Admin |
| `/settings/reporting/[section]` | MCP usage | Yes | Tenant Admin |
| **Admin (Global)** | | | |
| `/admin/metrics/[section]` | Agent metrics, MCP usage, platform usage | Yes | Global Admin |
| `/admin/reports/[section]` | Distress reports, session export, session reports, user feedback | Yes | Global Admin |
| `/admin/security/[section]` | Device block, version block, vulnerability data | Yes | Global Admin |
| `/admin/settings/[section]` | Config reseed, diagnostics log paths, global settings, MCP users, usage plans | Yes | Global Admin |
| `/admin/tenants/[section]` | Tenant config report, tenant management | Yes | Global Admin |
| `/admin/ops` | Operations page | Yes | Global Admin |
| `/admin/software` | Software inventory | Yes | Global Admin |
| **Public** | | | |
| `/progress` | Real-time enrollment progress (end users) | Public | — |
| `/docs/[section]` | Documentation (12 sections) | Public | — |
| `/changelog` | Platform change log & known issues | Public | — |
| `/roadmap` | Planned features & current focus areas | Public | — |
| `/about` | Platform introduction & quick links | Public | — |
| `/privacy` | Privacy policy & data handling | Public | — |
| `/terms` | Terms of use & legal disclaimers | Public | — |
| `/preview` | Private Preview waitlist/approval | Yes | Unapproved |
| `/health-check` | Backend health status | Public | — |
| `/go/[code]` | Bootstrap short-link redirector | Public | — |

### State Management

React Context API (no Redux/Zustand):

| Context | Purpose |
|---------|---------|
| `AuthContext` | MSAL + user info + role detection (global/tenant admin, operator, viewer, MCP access, bootstrap manager) |
| `SignalRContext` | WebSocket connection, group subscriptions, auto-reconnect |
| `TenantContext` | Current tenant ID (persisted to localStorage) |
| `NotificationContext` | Toast notifications with auto-dismiss + deduplication |
| `GlobalNotificationContext` | In-app persistent notifications for Global Admins |
| `SidebarContext` | Sidebar expanded/collapsed state |
| `ThemeContext` | Dark mode toggle (localStorage) |

### API Communication

- `lib/authenticatedFetch.ts` – Wraps `fetch()` with Bearer token, 401 retry with token refresh
- `lib/api.ts` – Typed URL builder for all backend endpoints
- `hooks/useAuthenticatedFetch.ts` – React hook with loading/error state
- Tenant isolation: All endpoints append `?tenantId={id}`

### Key UI Components

| Component | Location | Purpose |
|-----------|----------|---------|
| `GlobalSidebar` | `components/` | Main navigation sidebar |
| `Navbar` | `components/` | Top navigation bar |
| `GlobalSearch` | `components/` | Cross-session search overlay |
| `ProtectedRoute` | `components/` | Auth guard with role-based access |
| `FeedbackBubble` | `components/` | In-app feedback button |
| `SessionTable` | `dashboard/components/` | Paginated, filterable, sortable session list |
| `EventTimeline` | `sessions/[id]/components/` | Phase-grouped event visualization |
| `PhaseTimeline` | `sessions/[id]/components/` | Visual phase progress with live activity |
| `VulnerabilityReportSection` | `sessions/[id]/components/` | CVE findings display |
| `AnalysisResultsSection` | `sessions/[id]/components/` | Analyze rule results |
| `ScriptExecutions` | `components/` | Script output viewer |
| `PerformanceChart` | `components/` | Time-series metrics chart |
| `ValidationIndicator` | `components/` | Device validation status |
| `AppInsightsInit` | `components/` | Azure Application Insights initialization |

### UI Patterns

- Settings pages use `error` (string|null) + `successMessage` (string|null) state for notifications
- Notifications rendered at top of `<main>` content area
- `"use client"` directive on interactive components
- Expand/collapse sections with guard blocks
- `UnsavedChangesModal` prevents navigation with unsaved form state

---

## Shared Library

**Target:** .NET Standard 2.0 (compatible with both net48 Agent and net8.0 Backend)

### Model Categories

| Namespace | Key Classes | Purpose |
|-----------|-------------|---------|
| `Models/Enrollment/` | `EnrollmentEvent`, `SessionRegistration`, `EnrollmentPhase`, `EventSeverity`, `BootstrapSession` | Core enrollment data |
| `Models/Config/` | `AgentConfigResponse`, `CollectorConfiguration`, `AnalyzerConfiguration`, `TenantConfiguration`, `AdminConfiguration`, `McpAccessPolicy`, `PlanTierDefinition` | Configuration hierarchy |
| `Models/Rules/` | `GatherRule`, `AnalyzeRule`, `ImeLogPattern`, `RuleResult` | Rules engine |
| `Models/Metrics/` | `UsageMetrics`, `UsageMetricsSnapshot`, `PlatformStats`, `AppInstallSummary` | Analytics |
| `Models/Diagnostics/` | `DiagnosticsLogPath`, `AgentErrorReport`, `DistressReport` | Diagnostics & error reporting |
| `Models/Notifications/` | `NotificationAlert`, `NotificationSeverity`, `WebhookProviderType` | Notification system |
| `DataAccess/` | 13 repository interfaces (`ISessionRepository`, `IConfigRepository`, etc.) | Data access contracts |
| `ApiModels.cs` | Request/Response pairs for all endpoints | API contracts |
| `Constants.cs` | Table names (33), API endpoints, event types (50+), defaults | Shared constants |

### Key Enums

- **`EnrollmentPhase`**: Unknown(-1), Start(0), DevicePreparation(1), DeviceSetup(2), AppsDevice(3), AccountSetup(4), AppsUser(5), FinalizingSetup(6), Complete(7), Failed(99)
- **`EventSeverity`**: Trace(-1), Debug(0), Info(1), Warning(2), Error(3), Critical(4)
- **`WebhookProviderType`**: None(0), TeamsLegacyConnector(1), TeamsWorkflowWebhook(2), Slack(10)
- **50+ Event Types**: `phase_transition`, `app_install_completed`, `enrollment_complete`, `enrollment_failed`, `esp_state_change`, `performance_snapshot`, `script_completed`, `gather_result`, `software_inventory_analysis`, etc.

---

## Data Model

### Azure Table Storage (33 Tables)

| Table | PartitionKey | RowKey | Purpose |
|-------|-------------|--------|---------|
| `Sessions` | TenantId | SessionId | Enrollment sessions |
| `SessionsIndex` | TenantId | IndexKey | Session search indexes |
| `Events` | SessionId | Timestamp_Sequence | Individual events |
| `EventTypeIndex` | TenantId | EventType_Timestamp | Event type search index |
| `AdminConfiguration` | "GlobalConfig" | "config" | Platform-wide settings |
| `TenantConfiguration` | TenantId | "config" | Per-tenant settings |
| `GatherRules` | TenantId | RuleId | Data collection rules |
| `AnalyzeRules` | TenantId | RuleId | Issue detection rules |
| `ImeLogPatterns` | TenantId | PatternId | IME log regex patterns |
| `RuleResults` | TenantId | SessionId_RuleId | Analysis findings |
| `RuleStates` | TenantId | RuleId | Rule enable/disable state |
| `UsageMetrics` | TenantId | MetricDate | Daily usage snapshots |
| `AppInstallSummaries` | TenantId | SessionId_AppName | Per-app install data |
| `PlatformStats` | "Global" | "stats" | Cumulative platform stats |
| `AuditLogs` | TenantId | Timestamp_Id | Admin action audit trail |
| `UserActivity` | TenantId | UserId | User login tracking |
| `UserUsageLog` | TenantId | UserId_Timestamp | Per-user API usage tracking |
| `BootstrapSessions` | TenantId / "CodeLookup" | ShortCode | OOBE bootstrap tokens |
| `BlockedDevices` | TenantId | SerialNumber | Blocked devices |
| `BlockedVersions` | "BlockedVersions" | Pattern | Version block/kill rules |
| `SessionReports` | TenantId | ReportId | User-submitted reports |
| `DistressReports` | TenantId | Timestamp_Id | Pre-auth distress signals |
| `GlobalAdmins` | "GlobalAdmins" | UPN | Platform-level admins |
| `TenantAdmins` | TenantId | UPN | Tenant-level admins (Admin, Operator, Viewer) |
| `McpUsers` | "McpUsers" | UPN | MCP API users |
| `PreviewWhitelist` | "Preview" | TenantId | Preview access gate |
| `PreviewConfig` | "Preview" | "config" | Preview feature config |
| `GlobalNotifications` | "GlobalNotifications" | InvertedTicks_Id | Persistent in-app notifications for Global Admins |
| `DeviceSnapshot` | TenantId | DeviceSerial | Device hardware/network snapshots |
| `VulnerabilityCache` | CacheType | CacheKey | Cached NVD/MSRC/KEV CVE data |
| `VulnerabilityReports` | TenantId | SessionId | Per-session vulnerability findings |
| `SoftwareInventory` | TenantId | SoftwareName | Aggregated software inventory |
| `CveIndex` | CveId | TenantId_SessionId | CVE → session cross-reference |

### Azure Blob Storage

- **Diagnostics container**: Agent-uploaded ZIP packages (`AgentDiagnostics-{sessionId}-{ts}.zip`)
- **Session reports container**: User-submitted report ZIPs
- **Platform stats blob**: Cached JSON for landing page

### Entity Relationships

```
Session (1) ──► (N) EnrollmentEvent
Session (1) ──► (N) RuleResult
Session (1) ──► (N) AppInstallSummary
Session (1) ──► (0..1) SessionReport
Session (1) ──► (0..1) VulnerabilityReport
Session (N) ◄── (1) TenantConfiguration
TenantConfiguration (N) ◄── (1) AdminConfiguration (inherits defaults)
GatherRule ──► (agent executes) ──► EnrollmentEvent (gather_result)
AnalyzeRule ──► (backend evaluates) ──► RuleResult
ImeLogPattern ──► (agent matches) ──► EnrollmentEvent (various types)
SoftwareInventory ──► VulnerabilityCorrelation ──► VulnerabilityReport
```

---

## Security Architecture

### Authentication Layers

**Agent → Backend (device auth):**

1. **MDM Client Certificate** (primary)
   - Agent discovers Intune MDM cert in LocalMachine\My store
   - Sent via TLS (mTLS), forwarded as `X-ARR-ClientCert` by Azure App Service
   - Validated against embedded Intune intermediate + root CA certificates

2. **Bootstrap Token** (pre-MDM, OOBE)
   - Admin creates time-limited token+code via web UI
   - Agent sends as `X-Bootstrap-Token` header
   - Bypasses cert/rate/hardware validation

3. **Hardware Headers** (supplementary)
   - `X-Device-SerialNumber`, `X-Device-Manufacturer`, `X-Device-Model`
   - Used for whitelist validation and device identification

4. **Agent Version Header**
   - `X-Agent-Version` – used for version block enforcement

**Web → Backend (user auth):**
- Microsoft Entra ID multi-tenant JWT via `AuthenticationMiddleware`
- Dynamic OIDC metadata per tenant (cached 24h)
- Claims: `tid` (tenant), `upn` (user), `oid` (object ID)

**MCP → Backend:**
- JWT auth + `X-Client-Source: mcp` header for usage tracking
- Access controlled via `McpUserService`

### Validation Pipeline (per agent request)

```
ValidateSecurityAsync()  (SecurityValidationExtensions.cs)
├─ 1. Tenant existence & suspension check (cheapest first)
├─ 2. Bootstrap token gate (if present → short-circuit)
├─ 3. Certificate validation against Intune CA chain
├─ 4. Rate limiting (sliding window, 1-min, per-device)
├─ 5. Hardware whitelist check (optional per tenant)
└─ 6. Autopilot device validation via MS Graph (optional per tenant)
```

### Agent Binary Integrity Verification

Agent downloads are verified using SHA-256 hashes through two independent channels:

**Channel 1: version.json (Blob Storage)**
- CI/CD computes SHA-256 of the agent ZIP after build
- Hash is written to `version.json`: `{ "version": "1.0.x", "sha256": "..." }`
- Bootstrapper and Self-Updater verify the downloaded ZIP against this hash

**Channel 2: Backend Hash-Oracle (AdminConfiguration)**
- CI/CD writes the SHA-256 hash to `AdminConfiguration.LatestAgentSha256` in Table Storage
- Backend delivers the hash via `AgentConfigResponse.LatestAgentSha256`
- Self-Updater uses the backend hash with priority over the version.json hash
- Separate trust channel: an attacker would need to compromise both Blob Storage AND the backend

**Verification flow:**
```
Self-Updater:
  1. Fetch version.json → get sha256 field
  2. Download ZIP
  3. Verify SHA-256: backend hash (priority) > version.json hash > skip (backward compat)

Bootstrapper:
  1. Fetch version.json → get sha256 field
  2. Download ZIP
  3. Verify SHA-256: version.json hash > legacy Content-MD5 header > skip (backward compat)
```

### Authorization Model

#### Endpoint Access Policies (EndpointAccessPolicyCatalog)

Six policy tiers (fail-closed: unregistered routes get 403):

| Policy | Description |
|--------|-------------|
| `PublicAnonymous` | No auth required |
| `DeviceOrBootstrapAuth` | mTLS cert or bootstrap token (via `ValidateSecurityAsync`) |
| `AuthenticatedUser` | Valid JWT, any tenant |
| `MemberRead` | Admin + Operator + Viewer roles |
| `TenantAdminOrGA` | Tenant Admin or Global Admin |
| `BootstrapManagerOrGA` | Bootstrap Manager permission or Global Admin |
| `GlobalAdminOnly` | Platform-wide admin |

#### Roles

| Role | Scope | Capabilities |
|------|-------|-------------|
| **Global Admin** | Platform-wide | Global config, all tenants, platform metrics, health checks, MCP users |
| **Tenant Admin** | Single tenant | Tenant config, rules, admin management, device blocking, notifications |
| **Operator** | Single tenant | Write access, optionally Bootstrap Manager permission |
| **Viewer** | Single tenant | Read-only dashboard, session detail view |

### Tenant Data Isolation

- All Table Storage queries filtered by `PartitionKey = TenantId`
- SignalR groups: `tenant-{tenantId}` for scoped broadcasts
- JWT `tid` claim determines tenant for web requests

---

## Real-Time Communication

### SignalR Integration

- **Hub:** `autopilotmonitor`
- **Transport:** WebSocket with auto-reconnect (0s, 2s, 10s, 30s backoff)
- **Token factory:** Fresh JWT per connection attempt

### Groups & Events

| Group | Events | Triggered By |
|-------|--------|-------------|
| `tenant-{tenantId}` | `newSession`, `sessionStatusChanged`, `eventReceived`, `ruleResultReceived` | Event ingestion, session registration |
| `global-admins` | `newSession` (all tenants), platform stats updates | Cross-tenant broadcasts |

### Client-Side (SignalRContext)

- Auto-rejoin groups after reconnect
- Components subscribe on mount, unsubscribe on unmount
- Connection state exposed for UI indicators

---

## Rules Engine

### Three Rule Types

| Type | Execution | Purpose |
|------|-----------|---------|
| **Gather Rules** | Agent-side | Collect data from registry, WMI, event logs, files, commands |
| **Analyze Rules** | Backend-side | Detect issues in enrollment events with confidence scoring |
| **IME Log Patterns** | Agent-side | Parse IME/AppWorkload/HealthScripts logs with regex |

### Gather Rules

- **Collector Types:** `registry`, `wmi`, `eventlog`, `file`, `command_allowlisted`, `logparser`, `json`, `xml`
- **Triggers:** `startup`, `interval`, `phase_change`, `on_event`
- **Security:** Command allowlist enforced by `GatherRuleGuards`
- **Output:** Emits `gather_result` event with collected data

### Analyze Rules

- **Conditions:** Match events by source, signal, operator, value, with event correlation
- **Confidence Scoring:** `BaseConfidence` + `ConfidenceFactors` (signal × weight), threshold at 40
- **Output:** `RuleResult` with explanation, remediation steps, related docs
- **Trigger:** Evaluated after enrollment completion/failure

### IME Log Patterns

- **Categories:** `always` (all phases), `currentPhase`, `otherPhases`
- **Actions:** 50+ (e.g., `setCurrentApp`, `updateStateInstalled`, `espPhaseDetected`)
- **Regex:** Named capture groups, `{GUID}` placeholder for Intune policy IDs
- **Tracked Logs:** `IntuneManagementExtension*.log`, `AppWorkload*.log`, `AgentExecutor*.log`, `HealthScripts*.log`

### Rule Distribution

1. Individual JSON files in `rules/gather/`, `rules/analyze/`, `rules/ime-log-patterns/`
2. GitHub Actions validates against JSON Schema + combines into `rules/dist/`
3. Combined files embedded as resources in Functions assembly
4. Served to agents via `/api/agent/config`, manageable via web UI per tenant
5. `ReseedFromGitHub` function can re-fetch from GitHub on demand

---

## Session Lifecycle

### Three Completion Paths

```
Path 1: IME Pattern Completion
  IME logs show all apps completed → Hello wait → enrollment_complete

Path 2: ESP Exit + Hello (Composite)
  ESP final exit (event 62407) → Hello wait (300s) → enrollment_complete

Path 3: Desktop Arrival (No-ESP / WDP v2)
  explorer.exe detected under real user → Hello wait → enrollment_complete
```

### ESP & Hello Tracking

- **ESP Events:** Shell-Core event log (62404=Hello wizard start, 62407=ESP exit/WhiteGlove)
- **Hello Events:** User Device Registration log (300=NGC success, 301=NGC failure)
- **Hello Wait:** 30s for wizard start → 300s for completion → timeout
- **HelloOutcome:** Tracked property recording Hello result
- **Policy Check:** WHfB policy registry poll every 10s; skip Hello wait if not configured

### Failure Detection

- **Terminal failures** (Failure, Abort, WhiteGlove_Failed) → immediate `enrollment_failed`
- **Recoverable failures** (Timeout) → 60s grace period before marking failed
- **Auth failures** → circuit breaker (max 5 attempts or configurable timeout)
- **Device-Only ESP:** 5-min timer after DeviceSetup exit; if no AccountSetup → device-only classification
- **WDP v2 gate skip:** `_enrollmentType == "v2"` → desktop arrival gate skipped (no ESP in WDP v2)

### WhiteGlove (Pre-Provisioning)

- Part 1: `whiteglove_complete` → persist state, exit gracefully (no self-destruct, session preserved)
- Part 2: Agent restarts on next boot, detects `whiteglove.complete` marker → `whiteglove_resumed`
- Session survives across reboot; sequence counter persisted

### State Persistence (Crash Recovery)

| File | Purpose |
|------|---------|
| `enrollment-state.json` | ESP flags, Hello state, completion signals |
| `ime-tracker-state.json` | Phase order, seen apps, file positions |
| `session.id` / `session.seq` | Session identity + event sequence counter |
| `enrollment-complete.marker` | Cleanup retry flag if previous cleanup failed |

### Signal Audit Trail

Terminal events (`enrollment_complete`/`enrollment_failed`) include `signalsSeen` and `signalTimestamps` for full state machine transparency. Completion check events are throttled (1x/min/source) with full state machine snapshot.

### Cleanup & Self-Destruct

1. Stop collectors, run shutdown analyzers
2. Drain event spool, final upload
3. Upload diagnostics ZIP (if configured)
4. Remove Scheduled Task, delete binaries/config/spool
5. Optionally reboot device (`RebootOnComplete`)

---

## Configuration Hierarchy

```
AdminConfiguration (global, single row in Azure Table)
    │   GlobalRateLimitRequestsPerMinute (default 100)
    │   CollectorIdleTimeoutMinutes (default 15)
    │   AgentMaxLifetimeMinutes (default 360)
    │   DiagnosticsGlobalLogPathsJson
    │   LatestAgentSha256
    │
    └──► TenantConfiguration (per tenant, inherits/overrides)
            │   Rate limiting (override or inherit global)
            │   Hardware whitelist, Autopilot device validation
            │   Collector intervals, Hello timeout
            │   Diagnostics: UploadEnabled, LogPathsJson
            │   Auth circuit breaker settings
            │   Teams/Slack/Telegram notifications
            │   Bootstrap token enablement
            │   UnrestrictedMode
            │
            └──► AgentConfigResponse (delivered to agent via /api/agent/config)
                    │   ConfigVersion (currently 21)
                    │   CollectorConfiguration (nested)
                    │   AnalyzerConfiguration (nested)
                    │   GatherRules[] (merged built-in + tenant)
                    │   ImeLogPatterns[] (merged built-in + tenant)
                    │   DiagnosticsLogPaths[] (merged global + tenant)
                    │   LatestAgentSha256
                    └   Various flags and intervals
```

**Caching:** Both admin and tenant configs cached 5 minutes in-memory (`IMemoryCache`).

---

## Diagnostics & Upload

### Architecture (Post-Refactor)

- **Old:** Long-lived SAS URL stored in agent config → device stores in `remote-config.json`
- **New:** `DiagnosticsUploadEnabled` boolean; agent calls `POST /api/agent/upload-url` just before upload
- SAS URL never stored on device or in config, kept in memory only

### Upload Flow

1. Agent creates ZIP: `sessioninfo.txt` + agent logs + IME logs + configured paths
2. Agent calls `POST /api/agent/upload-url` → receives short-lived SAS URL (1h)
3. Agent uploads via `PUT {blobUrl}` with `x-ms-blob-type: BlockBlob`
4. 3 retries with exponential backoff (2s, 4s, 8s); 401/403 = non-retryable

### Diagnostics Log Paths

- Global paths: `AdminConfiguration.DiagnosticsGlobalLogPathsJson` (built-in, `IsBuiltIn=true`)
- Tenant paths: `TenantConfiguration.DiagnosticsLogPathsJson` (custom, `IsBuiltIn=false`)
- Merged list delivered to agent; security validated by `DiagnosticsPathGuards`

### Upload Modes

| Mode | Behavior |
|------|----------|
| `Off` (default) | Disabled |
| `Always` | Upload on both success and failure |
| `OnFailure` | Upload only on enrollment failure |

---

## Vulnerability Management

### Architecture

Software inventory collected by agent → correlated with CVE databases on backend → vulnerability reports per session.

### Data Sources

| Source | Service | Purpose |
|--------|---------|---------|
| NIST NVD | `NvdApiClient` | CVE database with CPE matching |
| Microsoft MSRC | `MsrcApiClient` | Microsoft-specific security advisories |
| CISA KEV | `KevDataService` | Known Exploited Vulnerabilities feed |

### Flow

1. `SoftwareInventoryAnalyzer` captures installed software at agent start+shutdown
2. Events uploaded via normal ingest pipeline
3. `VulnerabilityCorrelationService` matches software against CVE data using CPE identifiers
4. `VersionComparer` handles semantic version comparison for affected version ranges
5. Results stored in `VulnerabilityReports` table, viewable in session detail UI
6. Custom CPE mappings configurable via admin UI for unmatched software
7. `VulnerabilityDataSyncFunction` periodically refreshes CVE cache

### Search

Sessions can be searched by CVE ID via `GET /api/search/sessions-by-cve`, backed by the `CveIndex` table.

---

## Notification System

### Webhook Notifications

Enrollment completion/failure events trigger webhook notifications via `WebhookNotificationService`.

| Provider | Renderer | Format |
|----------|----------|--------|
| Teams Legacy Connector | `LegacyTeamsConnectorRenderer` | Office 365 Connector card |
| Teams Workflow Webhook | `TeamsWorkflowAdaptiveCardRenderer` | Adaptive Card |
| Slack | `SlackRenderer` | Block Kit |

Configuration per tenant via `TenantConfiguration.WebhookUrl` + `WebhookProviderType`.

### Telegram Notifications

`TelegramNotificationService` sends enrollment notifications via Telegram bot API. Configured per tenant.

### Email Notifications

`ResendEmailService` handles transactional emails via Resend.com API (used for Preview notifications).

### In-App Notifications

`GlobalNotificationService` provides persistent notifications for Global Admins, stored in `GlobalNotifications` table. Survives page reloads, dismissable per user.

---

## Testing

### Backend Tests (`src/Backend/AutopilotMonitor.Functions.Tests/`)

**Target:** .NET 8.0, xUnit

| Test File | Coverage |
|-----------|---------|
| `IngestCriticalPathTests` | Regression guard for `StampServerFields()` — ensures `ReceivedAt`, `TenantId`, `SessionId` are stamped |
| `SecurityValidatorTests` | Certificate validation, rate limiting, hardware whitelist, device validation flows |
| `DistressValidationTests` | Distress report validation |
| `DistressRateLimitServiceTests` | Distress rate limiting |
| `EventTimestampValidationTests` | Timestamp clamping and validation |
| `NdjsonParserTests` | NDJSON parsing |
| `BuiltInRulesTests` | Built-in rule logic |
| `EndpointPolicyCatalogCompletenessTests` | Ensures every HTTP route has a catalog entry |

### Agent Tests (`src/Agent/AutopilotMonitor.Agent.Core.Tests/`)

**Target:** .NET Framework 4.8, xUnit

| Test File | Coverage |
|-----------|---------|
| `DesktopArrivalDetectorTests` | Desktop arrival detection |
| `DiagnosticsPathGuardsTests` | Path guard validation |
| `GatherRuleGuardsTests` | Gather rule path/target guards |
| `AppPackageStateListTests` | App package state tracking |
| `AppPackageStateTests` | Individual package state machine |
| `CmTraceLogParserTests` | CMTrace log format parsing |
| `EnrollmentStatePersistenceTests` | State file persistence |
| `LogFilePositionTrackerTests` | Log file tail position bookmarking |

---

## Infrastructure & Deployment

### Azure Resources

| Resource | Purpose |
|----------|---------|
| Azure Functions (Isolated Worker) | REST API, event processing, timer triggers |
| Azure Table Storage | 33 tables for sessions, events, config, rules, metrics, vulnerability data |
| Azure Blob Storage | Diagnostics ZIPs, session reports, platform stats cache, agent binaries |
| Azure SignalR Service | WebSocket hub for real-time updates |
| Azure Static Web Apps | Next.js frontend hosting |
| Azure Container App | MCP server (provisioned via `infra/mcp-server.bicep`) |
| Azure Container Registry | MCP server container images |
| Application Insights | Logging, telemetry, performance monitoring |
| Microsoft Entra ID | Multi-tenant OIDC authentication |

### MCP Server

Provisioned via Bicep (`infra/mcp-server.bicep`):
- Azure Container Registry (Basic SKU)
- Log Analytics Workspace
- Container App Environment
- Container App (`autopilotmonitor-mcp`) running from ACR image

### Bootstrap Deployment

`scripts/Bootstrap/Install-AutopilotMonitor.ps1` — Intune Platform Script (.ps1):
- Deployed early in Autopilot enrollment via Intune
- Downloads agent ZIP from blob storage, verifies SHA-256 integrity
- Runs `AutopilotMonitor.Agent.exe --install`
- Multi-signal guard: registry marker, OOBE state, WMI/filesystem user profile, 12h bootstrap window (prevents ghost sessions)
- `Test-ShouldBootstrapAgent.ps1` — standalone test for guard logic
- `Uninstall-AutopilotMonitor.ps1` — manual cleanup

### Agent Build & Upload

`scripts/Deployment/build_and_upload_release_agent_build.ps1`:
- Builds agent solution in Release mode
- Creates ZIP package
- Computes SHA-256 hash
- Uploads ZIP + `version.json` (with hash) to Azure Blob Storage

### CI/CD (GitHub Actions)

| Workflow | Trigger | Purpose |
|----------|---------|---------|
| `azure-static-web-apps-*.yml` | Push to main (Web changes) | Build + deploy Next.js to Azure Static Web Apps |
| `combine-rules.yml` | Changes to `rules/`, `schema/`, `scripts/` | Validate rules against JSON Schema, combine into `dist/`, auto-commit |

### Environment Variables (Web)

```
NEXT_PUBLIC_ENTRA_CLIENT_ID                 # App registration client ID
NEXT_PUBLIC_ENTRA_REDIRECT_URI              # Auth redirect (localhost:3000 dev)
NEXT_PUBLIC_ENTRA_POST_LOGOUT_REDIRECT_URI
NEXT_PUBLIC_API_BASE_URL                    # Backend URL (localhost:7071 dev)
NEXT_PUBLIC_PLATFORM_STATS_MANIFEST_URL
```

---

## Build & Development

### Build Commands

```bash
# .NET solution (Agent + Backend + Shared)
dotnet build AutopilotMonitor.sln --nologo -v quiet

# Backend tests
dotnet test src/Backend/AutopilotMonitor.Functions.Tests/

# Agent tests
dotnet test src/Agent/AutopilotMonitor.Agent.Core.Tests/

# Web frontend
cd src/Web/autopilot-monitor-web
npm install
npm run dev          # Development server (localhost:3000)
npm run build        # Production build
npx tsc --noEmit     # Type checking only

# Rules validation + combine
cd rules && node scripts/combine.js
```

### Agent CLI Arguments

```
--install                           Deploy via Scheduled Task (Intune package)
--run-gather-rules                  One-shot data collection, then exit
--run-ime-matching                  Offline IME log parsing, write ime_pattern_matching.log
--console                           Enable console output
--log-level {level}                 Set log level (debug/info/warning/error)
--api-url {url}                     Override API endpoint (alias: --backend-api)
--bootstrap-token {token}           Pre-MDM bootstrap auth
--ime-log-path {path}               Override IME log folder
--ime-match-log {path}              Write matched IME log lines to file (debug)
--replay-log-dir {path}             Enable log replay mode
--replay-speed-factor {n}           Compression factor (default 50)
--no-cleanup                        Disable self-destruct
--reboot-on-complete                Trigger reboot after enrollment
--new-session                       Start fresh session
--keep-logfile                      Preserve logs after cleanup
--await-enrollment                  Wait for MDM certificate before starting
--await-enrollment-timeout {min}    MDM cert wait timeout (default 480min)
--disable-geolocation               Skip geo-location detection
```

### Key Design Conventions

| Convention | Details |
|------------|---------|
| Agent endpoint security | All agent endpoints use `req.ValidateSecurityAsync()` from `SecurityValidationExtensions.cs` |
| Route policy catalog | Every HTTP route MUST be registered in `EndpointAccessPolicyCatalog` (fail-closed → 403) |
| ConfigVersion | Tracks agent capability level (currently 21 = NTP time check + timezone auto-set) |
| Phase progression | Forward-only: DeviceSetup(1) → AccountSetup(2), no backward transitions |
| Phase isolation | App IDs seen in earlier phases are ignored in later phases (IME tracker) |
| Completion throttling | Max 1 `completion_check` event per source per minute |
| Sequence persistence | Saved every 50 events + on critical events; crash recovery uses spool ceiling |
| Settings UI | `error` + `successMessage` state for notifications at top of `<main>` |
| Maintenance timer | Runs every 2 hours (not daily, despite function name) |
| Agent versioning | Auto-incremented: 1.0.{BuildNumber} |
| Timestamp clamping | Preserves originals + flags for troubleshooting |
| Bootstrap scripts | Must be pure ASCII (no em-dashes/Unicode) — PS 5.1 compatibility |
