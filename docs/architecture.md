# Architecture Overview

This document describes the technical architecture of Autopilot Monitor.

## High-Level Architecture

Autopilot Monitor consists of four main components:

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           DEVICE SIDE                                    │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  ┌──────────────────┐     ┌──────────────────────────────────────────┐  │
│  │ Bootstrap.ps1    │────▶│ Monitoring Agent (.NET 4.8 Service)      │  │
│  │ (Intune Script)  │     │                                          │  │
│  │                  │     │  • Log Watchers                          │  │
│  │ • Deploy agent   │     │  • Event Collectors                      │  │
│  │ • Create session │     │  • Performance Monitors                  │  │
│  │ • Set correlation│     │  • Upload Manager                        │  │
│  └──────────────────┘     └─────────────┬────────────────────────────┘  │
│                                         │                               │
└─────────────────────────────────────────┼───────────────────────────────┘
                                          │ HTTPS (mTLS)
                                          ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                          AZURE BACKEND                                   │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │              Azure Functions (.NET 8 Isolated)                      │ │
│  │                                                                     │ │
│  │  /api/sessions/register  │  /api/events/ingest  │  /api/bundles/*  │ │
│  └────────────────┬────────────────────┬──────────────────┬───────────┘ │
│                   │                    │                  │             │
│                   ▼                    ▼                  ▼             │
│  ┌─────────────────────┐  ┌──────────────────┐  ┌──────────────────┐   │
│  │  Azure Table        │  │  Azure Table     │  │  Azure Blob      │   │
│  │  (Sessions)         │  │  (Events)        │  │  (Bundles/Logs)  │   │
│  └─────────────────────┘  └──────────────────┘  └──────────────────┘   │
│                                                                          │
│  ┌────────────────────────┐           ┌───────────────────────────┐     │
│  │ Azure SignalR Service  │──────────▶│  Next.js Web App          │     │
│  │ (Real-time updates)    │           │  (Static Web App)         │     │
│  └────────────────────────┘           └───────────────────────────┘     │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘
```

## Component Details

### 1. Bootstrap Script (PowerShell)

**Purpose**: Deploy and configure the monitoring agent early in Autopilot enrollment.

**Responsibilities**:
- Generate unique session ID
- Collect device information
- Store configuration in registry
- Install/start monitoring agent
- Register session with backend

**Deployment**: Intune PowerShell Script (required, runs in Device ESP)

**Key Files**:
- [Install-AutopilotMonitor.ps1](../scripts/Bootstrap/Install-AutopilotMonitor.ps1)

### 2. Monitoring Agent (.NET Framework 4.8)

**Purpose**: Continuously monitor enrollment progress and collect telemetry.

**Architecture**:

```
AutopilotMonitor.Agent (Exe)
├── Program.cs (Entry point, console or service mode)
└── AutopilotMonitorService.cs (Windows Service wrapper)
    │
    └── AutopilotMonitor.Agent.Core (Library)
        ├── Monitoring/
        │   ├── MonitoringService.cs (Main orchestrator)
        │   ├── LogWatcher.cs (Tail IME logs)
        │   ├── EventMonitor.cs (Watch Windows Event Log)
        │   └── PerformanceMonitor.cs (CPU, memory, disk)
        │
        ├── Storage/
        │   └── EventSpool.cs (Offline queue)
        │
        ├── Api/
        │   └── BackendApiClient.cs (HTTP client for API calls)
        │
        ├── Configuration/
        │   └── AgentConfiguration.cs (Settings)
        │
        └── Logging/
            └── AgentLogger.cs (Simple file logger)
```

**Key Features**:
- Runs as Windows Service (LocalSystem account)
- Offline spool for resilient upload
- Batched uploads (configurable interval)
- Exponential backoff on failures
- Low memory footprint (<50MB)

**Data Flow**:
1. Watchers detect events (logs, event log, performance)
2. Events normalized into `EnrollmentEvent` objects
3. Events added to local spool (file-based queue)
4. Periodic upload timer sends batches to backend
5. On success, events removed from spool

**Key Files**:
- [MonitoringService.cs](../src/Agent/AutopilotMonitor.Agent.Core/Monitoring/MonitoringService.cs)
- [EventSpool.cs](../src/Agent/AutopilotMonitor.Agent.Core/Storage/EventSpool.cs)
- [BackendApiClient.cs](../src/Agent/AutopilotMonitor.Agent.Core/Api/BackendApiClient.cs)

### 3. Backend API (Azure Functions)

**Purpose**: Ingest telemetry, store data, and serve web UI.

**Functions**:

#### RegisterSession
- **Route**: `POST /api/sessions/register`
- **Purpose**: Register a new enrollment session
- **Input**: `SessionRegistration` (device info, session ID)
- **Output**: `RegisterSessionResponse` (success, registered timestamp)
- **Storage**: Azure Table Storage (`sessions` table)

#### IngestEvents
- **Route**: `POST /api/events/ingest`
- **Purpose**: Receive batched events from agents
- **Input**: `IngestEventsRequest` (session ID, list of events)
- **Output**: `IngestEventsResponse` (events processed count)
- **Storage**: Azure Table Storage (`events` table)
- **Features**:
  - Compression support (gzip)
  - Batch processing
  - Deduplication (by event ID)

#### UploadBundle
- **Route**: `POST /api/bundles/upload`
- **Purpose**: Get SAS URL for uploading troubleshooting bundle
- **Input**: `UploadBundleRequest` (session ID, file name)
- **Output**: `UploadBundleResponse` (SAS URL, expiry)
- **Storage**: Azure Blob Storage (`bundles` container)

**Authentication** (Future):
- Phase 1: Function key authentication
- Phase 2+: Client certificate (mTLS) using Intune MDM cert

**Key Files**:
- [RegisterSessionFunction.cs](../src/Backend/AutopilotMonitor.Functions/Functions/RegisterSessionFunction.cs)
- [IngestEventsFunction.cs](../src/Backend/AutopilotMonitor.Functions/Functions/IngestEventsFunction.cs)

### 4. Web Dashboard (Next.js)

**Purpose**: Real-time monitoring and troubleshooting UI.

**Technology Stack**:
- Next.js 15 (React 19)
- TypeScript
- Tailwind CSS
- Azure Static Web Apps (deployment)

**Key Pages** (Planned):

```
/ (Dashboard)
├── Active sessions grid
├── Fleet health metrics
└── Recent failures

/sessions/[id] (Session Detail)
├── Phase timeline
├── Event stream
├── Diagnosis panel
└── Download bundle

/sessions (Session List)
├── Filterable table
├── Search by serial/device name
└── Status indicators

/fleet (Fleet Analytics)
├── Success rate trends
├── Failure reason distribution
├── Model/OS breakdown
└── App install leaderboard
```

**Key Files**:
- [app/page.tsx](../src/Web/autopilot-monitor-web/app/page.tsx) (Dashboard)
- [app/layout.tsx](../src/Web/autopilot-monitor-web/app/layout.tsx) (Root layout)

### 5. Shared Library

**Purpose**: Common models and constants across all .NET projects.

**Key Models**:
- `EnrollmentEvent` - Single telemetry event
- `EnrollmentPhase` - Enrollment phase enumeration
- `SessionRegistration` - Device and session info
- `SessionSummary` - Session overview for UI
- `ApiModels` - Request/response contracts

**Key Files**:
- [Models/](../src/Shared/AutopilotMonitor.Shared/Models/)
- [Constants.cs](../src/Shared/AutopilotMonitor.Shared/Constants.cs)

## Data Models

### Azure Table Storage Schema

#### Sessions Table

| Property | Type | Description |
|----------|------|-------------|
| PartitionKey | string | `{TenantId}` |
| RowKey | string | `{SessionId}` |
| SerialNumber | string | Device serial number |
| Manufacturer | string | Device manufacturer |
| Model | string | Device model |
| DeviceName | string | Computer name |
| StartedAt | DateTime | Enrollment start time (UTC) |
| CompletedAt | DateTime? | Enrollment end time (UTC) |
| CurrentPhase | int | Current enrollment phase |
| Status | string | InProgress, Succeeded, Failed |
| EventCount | int | Total events received |

#### Events Table

| Property | Type | Description |
|----------|------|-------------|
| PartitionKey | string | `{TenantId}_{SessionId}` |
| RowKey | string | `{Timestamp}_{Sequence}` |
| EventId | string | Unique event identifier (GUID) |
| EventType | string | Type of event |
| Severity | int | Debug, Info, Warning, Error, Critical |
| Source | string | Where the event originated |
| Phase | int | Phase during which event occurred |
| Message | string | Human-readable message |
| Data | string | JSON-serialized additional data |

### Azure Blob Storage Structure

```
bundles/
  {tenant-id}/
    {session-id}/
      troubleshooting-bundle.zip
      logs/
        agent.log
        ime.log
      events/
        events.json
      diagnostics/
        dsregcmd.txt
        network.txt

screenshots/
  {tenant-id}/
    {session-id}/
      {timestamp}.jpg
```

## Security Architecture

### Phase 1 (Current)
- Function key authentication (keys in URL)
- No encryption at rest (using Azure defaults)
- HTTPS for transport

### Phase 2+ (Planned)
- Client certificate authentication (mTLS)
  - Leverage Intune MDM device certificate
  - No secrets on device
  - Per-device authentication
- Azure AD authentication for web UI
- Multi-tenant isolation
- Encryption at rest (Azure Storage encryption)
- PII redaction policies

## Scalability Considerations

### Current Capacity (Phase 1)
- ~1,000 devices/month: ~$15/month Azure cost
- ~10,000 devices/month: ~$50-100/month Azure cost

### Storage Scaling

**Azure Table Storage**:
- Massively scalable (billions of entities)
- Auto-sharding by PartitionKey
- PartitionKey strategy: `{TenantId}` for sessions, `{TenantId}_{SessionId}` for events
- Supports 20,000 entities/sec per table

**Azure Blob Storage**:
- Unlimited storage
- Auto-tiering (Hot → Cool → Archive)
- Lifecycle policies for cost optimization

### Compute Scaling

**Azure Functions**:
- Consumption plan: Auto-scale to demand
- Isolated worker process model (better performance)
- Can handle 1000s of concurrent requests

**Web UI (Static Web Apps)**:
- Global CDN distribution
- Scales automatically
- Near-zero cost for read operations

## Monitoring and Observability

### Application Insights Integration

All components log to Azure Application Insights:

- **Agent**: Custom telemetry via API
- **Functions**: Built-in integration
- **Web**: Custom events and page views

### Metrics to Track

- Event ingestion rate (events/sec)
- API response times (p50, p95, p99)
- Failed uploads (count, reasons)
- Storage utilization (GB, transactions)
- Active sessions (count)
- Session success rate (%)

## Cost Optimization Strategies

1. **Use Azure Table Storage** instead of Cosmos DB (99% cheaper)
2. **Batch events** before upload (reduce transactions)
3. **Compress payloads** (reduce bandwidth)
4. **Lifecycle policies** for blob storage (hot → cool → archive)
5. **Summarize aggressively** (don't store every raw log line)
6. **Cache static content** (CDN for web UI)
7. **Use consumption plans** (pay per use, not reserved capacity)

## Future Architecture Enhancements

### Phase 2: Intelligence
- Rule engine (YAML-based, server-side evaluation)
- Real-time SignalR for live updates
- Troubleshooting bundle generation

### Phase 3: Advanced
- Pre-provisioning support
- Custom rule authoring UI
- REST API for integrations
- Webhook notifications
- Teams/Slack integration

### Phase 4: Enterprise
- Multi-tenant SaaS model
- Tenant-level isolation
- RBAC and permissions
- Audit logging
- Compliance controls (GDPR, etc.)

## Development Best Practices

### Code Organization
- Keep shared models in `AutopilotMonitor.Shared`
- Agent logic in `AutopilotMonitor.Agent.Core` (testable)
- Thin wrappers for service/console in `AutopilotMonitor.Agent`
- Stateless Azure Functions (no local state)

### Error Handling
- Agent: Never crash, always spool locally
- Functions: Return proper HTTP status codes
- Web: Graceful degradation, show cached data

### Testing
- Unit tests for core logic
- Integration tests for API endpoints
- End-to-end tests for critical flows
- Load testing for scalability validation

### Deployment
- CI/CD with GitHub Actions
- Separate environments (dev, staging, prod)
- Infrastructure as Code (Bicep/ARM templates)
- Automated rollback on failures
