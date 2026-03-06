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
| Agent on Windows Devices  |                        | Teams / Webhooks ...      |
| Enrollment + app progress |                        +---------------------------+
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
| **Backend** | Azure Functions .NET 8 (Isolated Worker) | REST API, event processing, rule engine, storage |
| **Web** | Next.js 15 + TypeScript + React 18 | Dashboard, settings, analytics, real-time UI |
| **Shared** | .NET Standard 2.0 | DTOs, models, enums, constants shared between Agent & Backend |

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
13. [Infrastructure & Deployment](#infrastructure--deployment)
14. [Build & Development](#build--development)

---

## Solution Structure

```
AutopilotMonitor.sln
├── src/
│   ├── Shared/AutopilotMonitor.Shared/          (netstandard2.0)
│   ├── Agent/
│   │   ├── AutopilotMonitor.Agent/              (net48, Exe – entry point)
│   │   └── AutopilotMonitor.Agent.Core/         (net48, Lib – core logic)
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
└── .github/workflows/                            CI/CD pipelines
```

**Project References:**
```
Shared ◄── Agent.Core ◄── Agent
Shared ◄── Functions ◄── Functions.Tests
Web (independent – communicates via REST + SignalR)
```

---

## Agent

### Entry Point & Modes

**File:** `src/Agent/AutopilotMonitor.Agent/Program.cs`

Three execution modes:
1. **Normal mode** (default) – Main enrollment monitoring loop
2. **`--install` mode** – Deploys agent via Scheduled Task (Intune package)
3. **`--run-gather-rules` mode** – One-shot offline data collection, then exits

### Startup Sequence (Normal Mode)

1. Single-instance check (prevent duplicate agent processes)
2. `FetchRemoteConfig()` – Backend config with 15s timeout, disk cache fallback
3. `RegisterSessionAsync()` – Register session (5 retries, exponential backoff)
4. `StartWatching()` – Enable FileSystemWatcher for event spool
5. `StartEventCollectors()` – EspAndHelloTracker (always on)
6. `StartOptionalCollectors()` – PerformanceCollector, AgentSelfMetrics, EnrollmentTracker, DesktopArrivalDetector
7. `StartGatherRuleExecutor()` – Backend-defined data collection rules
8. `InitializeAnalyzers()` + `RunStartupAnalyzers()` – Security baseline

### Key Services

| Service | Location | Responsibility |
|---------|----------|----------------|
| `MonitoringService` | `Core/Monitoring/Core/` | Main orchestrator: starts/stops collectors, manages upload loop |
| `BackendApiClient` | `Core/Monitoring/Network/` | HTTP client with mTLS cert + hardware headers |
| `EventSpool` | `Core/Monitoring/Core/` | Offline event storage (JSON files), FileSystemWatcher-based |
| `EnrollmentTracker` | `Core/Monitoring/Tracking/` | Central enrollment state machine, 3 completion paths |
| `ImeLogTracker` | `Core/Monitoring/Tracking/` | Parses IME logs with backend-provided regex patterns |
| `EspAndHelloTracker` | `Core/Monitoring/Collectors/` | Windows event log monitoring (ESP exit, Hello provisioning) |
| `DesktopArrivalDetector` | `Core/Monitoring/Collectors/` | Polls for explorer.exe under real user (non-SYSTEM) |
| `PerformanceCollector` | `Core/Monitoring/Collectors/` | CPU/Memory/Disk metrics |
| `GatherRuleExecutor` | `Core/Monitoring/Collectors/` | Executes backend-defined data collection rules |
| `DiagnosticsPackageService` | `Core/Monitoring/Core/` | Creates ZIP + uploads via short-lived SAS URL |
| `RemoteConfigService` | `Core/Configuration/` | Fetches & caches backend config with disk fallback |
| `SessionPersistence` | `Core/Monitoring/Core/` | Persists session ID, sequence counter, markers |

### Directory Layout (Agent.Core)

```
Monitoring/
├── Analyzers/         Security checks (LocalAdminAnalyzer)
├── Collectors/        Data collectors (ESP, Hello, Performance, GatherRules, Diagnostics)
├── Core/              Orchestration (MonitoringService, EventSpool, SessionPersistence)
├── Network/           API client, emergency reporter, geo-location, network metrics
├── Replay/            Log replay for testing/simulation
└── Tracking/          Enrollment state machine, IME parser, script tracking
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

### Endpoints (~60 Functions)

**Agent-to-Cloud (device auth via cert/bootstrap):**

| Route | Method | Purpose |
|-------|--------|---------|
| `/agent/register-session` | POST | Register new enrollment session |
| `/agent/ingest` | POST | Upload events (NDJSON+gzip) |
| `/agent/config` | GET | Fetch agent configuration |
| `/agent/upload-url` | POST | Get short-lived SAS URL for diagnostics |
| `/agent/error` | POST | Report agent errors |

**Bootstrap (pre-MDM auth):**

| Route | Method | Purpose |
|-------|--------|---------|
| `/bootstrap/sessions` | POST/GET | Create/list bootstrap sessions |
| `/bootstrap/sessions/{code}` | DELETE | Revoke bootstrap session |
| `/bootstrap/validate/{code}` | POST | Validate bootstrap code |
| `/bootstrap/register-session` | POST | Register via bootstrap token |
| `/bootstrap/ingest` | POST | Ingest via bootstrap token |
| `/bootstrap/config` | GET | Config via bootstrap token |

**Web Portal (JWT auth):**

| Category | Key Routes |
|----------|------------|
| **Sessions** | `GET /sessions`, `GET /sessions/{id}`, `GET /sessions/{id}/events`, `POST /sessions/{id}/analysis`, `DELETE /sessions/{id}`, `POST /sessions/{id}/mark-failed` |
| **Rules** | CRUD for `/rules/gather`, `/rules/analyze`, `/rules/ime-log-patterns`, `POST /rules/reseed-from-github` |
| **Config** | `GET/POST /global/config`, `GET/POST /config/{tenantId}`, `POST /config/{tenantId}/security-bypass` |
| **Auth** | `GET /auth/me`, `GET /auth/is-galactic-admin`, CRUD `/auth/galactic-admins` |
| **Tenants** | CRUD `/tenants/{id}/admins`, `POST /tenants/{id}/offboard` |
| **Devices** | `POST /devices/block`, `DELETE /devices/block/{serial}`, `GET /devices/blocked` |
| **Reports** | `POST /sessions/{id}/report`, `GET /galactic/session-reports` |
| **Metrics** | `GET /metrics/usage`, `GET /metrics/app`, `GET /metrics/geographic`, `GET /stats/platform` |
| **Galactic** | `/galactic/metrics/*`, `/galactic/audit/logs`, `/galactic/session-reports` |
| **SignalR** | `POST /realtime/negotiate`, `POST /realtime/groups/join`, `POST /realtime/groups/leave` |
| **Health** | `GET /health`, `GET /health/detailed` |

**Timer Trigger:**
- `DailyMaintenanceFunction` – Every 2 hours (`0 0 */2 * * *`): stale session detection, metrics aggregation, data cleanup

### Key Backend Services

| Service | Responsibility |
|---------|----------------|
| `TableStorageService` | Core data access for all 21 Azure Tables (split across 5 files) |
| `TenantConfigurationService` | Per-tenant config with 5-min cache |
| `AdminConfigurationService` | Global config with 5-min cache, syncs rate limits to tenants |
| `RateLimitService` | In-memory sliding window rate limiting (1-min window) |
| `SecurityValidator` | Centralized request validation (cert → rate limit → hardware → APV) |
| `RuleEngine` | Server-side analyze rule evaluation with confidence scoring |
| `MaintenanceService` | Cleanup, metrics aggregation, stale session detection |
| `BootstrapSessionService` | Bootstrap token lifecycle (create, validate, revoke) |
| `BlockedDeviceService` | Device block/kill signal management |
| `SessionReportService` | Report ZIP generation + Blob upload |
| `GraphTokenService` | MS Graph token acquisition for Autopilot device validation |

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

### Page Routes

| Path | Purpose | Auth | Role |
|------|---------|------|------|
| `/` | Landing page with platform stats | Public | — |
| `/dashboard` | Session list, real-time updates | Yes | Any |
| `/sessions/[sessionId]` | Session detail, event timeline, analysis | Yes | Any |
| `/diagnosis/[sessionId]` | Rule analysis results | Yes | Any |
| `/settings` | Tenant configuration (10+ sections) | Yes | Tenant Admin |
| `/admin-configuration` | Global admin settings | Yes | Galactic Admin |
| `/gather-rules` | Gather rule CRUD | Yes | Tenant Admin |
| `/analyze-rules` | Analyze rule CRUD | Yes | Tenant Admin |
| `/ime-log-patterns` | IME pattern management | Yes | Tenant Admin |
| `/fleet-health` | App metrics, fleet analytics | Yes | Tenant Admin |
| `/usage-metrics` | Tenant usage analytics | Yes | Tenant Admin |
| `/platform-metrics` | Platform-wide metrics | Yes | Galactic Admin |
| `/audit` | Audit log viewer | Yes | Tenant Admin |
| `/health-check` | Backend health status | Yes | Galactic Admin |
| `/geographic-performance` | Geo map of deployments | Yes | Galactic Admin |
| `/docs/[section]` | Documentation | Public | — |

### State Management

React Context API (no Redux/Zustand):

| Context | Purpose |
|---------|---------|
| `AuthContext` | MSAL + user info + role detection (galactic/tenant admin) |
| `SignalRContext` | WebSocket connection, group subscriptions, auto-reconnect |
| `TenantContext` | Current tenant ID (persisted to localStorage) |
| `NotificationContext` | Toast notifications with auto-dismiss + deduplication |
| `ThemeContext` | Dark mode toggle (localStorage) |

### API Communication

- `lib/authenticatedFetch.ts` – Wraps `fetch()` with Bearer token, 401 retry with token refresh
- `hooks/useAuthenticatedFetch.ts` – React hook with loading/error state
- Tenant isolation: All endpoints append `?tenantId={id}`

### Key UI Components

| Component | Location | Purpose |
|-----------|----------|---------|
| `SessionTable` | `dashboard/components/` | Paginated, filterable, sortable session list |
| `EventTimeline` | `sessions/[id]/components/` | Phase-grouped event visualization |
| `PhaseTimeline` | `sessions/[id]/components/` | Visual phase progress with live activity |
| `ProtectedRoute` | `components/` | Auth guard with role-based access |
| `ScriptExecutions` | `components/` | Script output viewer |
| `PerformanceChart` | `components/` | Time-series metrics chart |

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
| `Models/Enrollment/` | `EnrollmentEvent`, `SessionRegistration`, `EnrollmentPhase`, `BootstrapSession` | Core enrollment data |
| `Models/Config/` | `AgentConfigResponse`, `CollectorConfiguration`, `TenantConfiguration`, `AdminConfiguration` | Configuration hierarchy |
| `Models/Rules/` | `GatherRule`, `AnalyzeRule`, `ImeLogPattern`, `RuleResult` | Rules engine |
| `Models/Metrics/` | `UsageMetrics`, `PlatformStats`, `AppInstallSummary` | Analytics |
| `Models/Diagnostics/` | `DiagnosticsLogPath`, `AgentErrorReport` | Diagnostics collection |
| `ApiModels.cs` | Request/Response pairs for all endpoints | API contracts |
| `Constants.cs` | Table names, API endpoints, event types, defaults | Shared constants |

### Key Enums

- **`EnrollmentPhase`**: Unknown(-1), Start(0), DevicePreparation(1), DeviceSetup(2), AppsDevice(3), AccountSetup(4), AppsUser(5), FinalizingSetup(6), Complete(7), Failed(99)
- **`Severity`**: Debug(0), Info(1), Warning(2), Error(3), Critical(4)
- **50+ Event Types**: `phase_transition`, `app_install_completed`, `enrollment_complete`, `enrollment_failed`, `esp_state_change`, `performance_snapshot`, `script_completed`, `gather_result`, etc.

---

## Data Model

### Azure Table Storage (21 Tables)

| Table | PartitionKey | RowKey | Purpose |
|-------|-------------|--------|---------|
| `Sessions` | TenantId | SessionId | Enrollment sessions |
| `EnrollmentEvents` | TenantId | Timestamp_EventId | Individual events |
| `AdminConfiguration` | "GlobalConfig" | "config" | Platform-wide settings |
| `TenantConfiguration` | TenantId | "config" | Per-tenant settings |
| `GatherRules` | TenantId | RuleId | Data collection rules |
| `AnalyzeRules` | TenantId | RuleId | Issue detection rules |
| `ImeLogPatterns` | TenantId | PatternId | IME log regex patterns |
| `RuleResults` | TenantId | SessionId_RuleId | Analysis findings |
| `UsageMetrics` | TenantId | MetricDate | Daily usage snapshots |
| `AppInstallSummaries` | TenantId | SessionId_AppName | Per-app install data |
| `PlatformStats` | "Global" | "stats" | Cumulative platform stats |
| `AuditLogs` | TenantId | Timestamp_Id | Admin action audit trail |
| `BootstrapSessions` | TenantId / "CodeLookup" | ShortCode | OOBE bootstrap tokens |
| `BlockedDevices` | TenantId | SerialNumber | Blocked devices |
| `SessionReports` | TenantId | ReportId | User-submitted reports |
| `GalacticAdmins` | "GalacticAdmins" | UPN | Platform-level admins |
| `TenantAdmins` | TenantId | UPN | Tenant-level admins |
| `PreviewWhitelist` | "Preview" | TenantId | Preview access gate |
| `UserActivity` | TenantId | UserId | User login tracking |
| `RuleStates` | TenantId | RuleId | Rule enable/disable state |
| `PreviewConfig` | "Preview" | "config" | Preview feature config |

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
Session (N) ◄── (1) TenantConfiguration
TenantConfiguration (N) ◄── (1) AdminConfiguration (inherits defaults)
GatherRule ──► (agent executes) ──► EnrollmentEvent (gather_result)
AnalyzeRule ──► (backend evaluates) ──► RuleResult
ImeLogPattern ──► (agent matches) ──► EnrollmentEvent (various types)
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

**Web → Backend (user auth):**
- Microsoft Entra ID multi-tenant JWT via `AuthenticationMiddleware`
- Dynamic OIDC metadata per tenant (cached 24h)
- Claims: `tid` (tenant), `upn` (user), `oid` (object ID)

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

### Tenant Data Isolation

- All Table Storage queries filtered by `PartitionKey = TenantId`
- SignalR groups: `tenant-{tenantId}` for scoped broadcasts
- JWT `tid` claim determines tenant for web requests

### Roles

| Role | Scope | Capabilities |
|------|-------|-------------|
| **Galactic Admin** | Platform-wide | Global config, all tenants, platform metrics, health checks |
| **Tenant Admin** | Single tenant | Tenant config, rules, admin management, device blocking |
| **User** | Single tenant | Read-only dashboard, session detail view |

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
| `galactic-admins` | `newSession` (all tenants), platform stats updates | Cross-tenant broadcasts |

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

- **Collector Types:** `registry`, `wmi`, `eventlog`, `file`, `command_allowlisted`, `logparser`
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
- **Policy Check:** WHfB policy registry poll every 10s; skip Hello wait if not configured

### Failure Detection

- **Terminal failures** (Failure, Abort, WhiteGlove_Failed) → immediate `enrollment_failed`
- **Recoverable failures** (Timeout) → 60s grace period before marking failed
- **Auth failures** → circuit breaker (max 5 attempts or configurable timeout)
- **Device-Only ESP:** 5-min timer after DeviceSetup exit; if no AccountSetup → device-only classification

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
    │
    └──► TenantConfiguration (per tenant, inherits/overrides)
            │   Rate limiting (override or inherit global)
            │   Hardware whitelist, Autopilot device validation
            │   Collector intervals, Hello timeout
            │   Diagnostics: UploadEnabled, LogPathsJson
            │   Auth circuit breaker settings
            │   Teams/Telegram notifications
            │   Bootstrap token enablement
            │
            └──► AgentConfigResponse (delivered to agent via /api/agent/config)
                    │   ConfigVersion (currently 14)
                    │   CollectorConfiguration (nested)
                    │   AnalyzerConfiguration (nested)
                    │   GatherRules[] (merged built-in + tenant)
                    │   ImeLogPatterns[] (merged built-in + tenant)
                    │   DiagnosticsLogPaths[] (merged global + tenant)
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

## Infrastructure & Deployment

### Azure Resources

| Resource | Purpose |
|----------|---------|
| Azure Functions (Isolated Worker) | REST API, event processing, timer triggers |
| Azure Table Storage | 21 tables for sessions, events, config, rules, metrics |
| Azure Blob Storage | Diagnostics ZIPs, session reports, platform stats cache |
| Azure SignalR Service | WebSocket hub for real-time updates |
| Azure Static Web Apps | Next.js frontend hosting |
| Application Insights | Logging, telemetry, performance monitoring |
| Microsoft Entra ID | Multi-tenant OIDC authentication |

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
--install                    Deploy via Scheduled Task (Intune package)
--run-gather-rules           One-shot data collection, then exit
--console                    Enable console output
--cert-thumbprint {thumb}    Override cert search
--tenant-id {id}             Override registry tenant
--api-url {url}              Override API endpoint
--bootstrap-token {token}    Pre-MDM bootstrap auth
--ime-log-path {path}        Override IME log folder
--replay-log-dir {path}      Enable log replay mode
--replay-speed-factor {n}    Compression factor (default 50)
--no-auth                    Disable cert auth
--no-cleanup                 Disable self-destruct
--reboot-on-complete         Trigger reboot after enrollment
--new-session                Start fresh session
--keep-logfile               Preserve logs after cleanup
```

### Key Design Conventions

| Convention | Details |
|------------|---------|
| Agent endpoint security | All agent endpoints use `req.ValidateSecurityAsync()` from `SecurityValidationExtensions.cs` |
| ConfigVersion | Tracks agent capability level (currently 14 = script execution tracking) |
| Phase progression | Forward-only: DeviceSetup(1) → AccountSetup(2), no backward transitions |
| Phase isolation | App IDs seen in earlier phases are ignored in later phases (IME tracker) |
| Completion throttling | Max 1 `completion_check` event per source per minute |
| Sequence persistence | Saved every 50 events + on critical events; crash recovery uses spool ceiling |
| Settings UI | `error` + `successMessage` state for notifications at top of `<main>` |
| Maintenance timer | Runs every 2 hours (not daily, despite function name) |
| Agent versioning | Auto-incremented: 1.0.{BuildNumber} |
