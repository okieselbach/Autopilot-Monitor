# Autopilot Monitor

Advanced monitoring and troubleshooting solution for Windows Autopilot deployments.

## Overview

Autopilot Monitor provides real-time tracking, intelligent diagnostics, and automated troubleshooting for Windows Autopilot enrollment processes. It consists of:

- **Bootstrap Script** - PowerShell script deployed via Intune that starts monitoring early in the enrollment process
- **Monitoring Agent** - Lightweight .NET Windows Service that collects telemetry and evidence
- **Backend API** - Azure Functions-based ingestion and processing pipeline
- **Web Dashboard** - Next.js application for real-time monitoring and fleet analytics

## Architecture

```
Device Side                     Azure Backend                    Web UI
┌──────────────┐               ┌──────────────┐                ┌──────────┐
│  Bootstrap   │──deploys──▶   │              │                │          │
│  Script      │               │   Azure      │◀───────────▶   │  Next.js │
└──────┬───────┘               │   Functions  │                │  Web App │
       │                       │              │                │          │
       ▼                       │  • Ingest    │                └──────────┘
┌──────────────┐               │  • Store     │
│  Monitoring  │──telemetry──▶ │  • Analyze   │
│  Agent       │    (mTLS)     │              │
└──────────────┘               └──────┬───────┘
                                      │
                                      ▼
                               ┌──────────────┐
                               │ Azure Table  │
                               │ Azure Blob   │
                               │ Azure SQL    │
                               └──────────────┘
```

## Project Structure

```
Autopilot-Monitor/
├── src/
│   ├── Agent/                          # .NET monitoring agent
│   │   ├── AutopilotMonitor.Agent/              # Windows Service host
│   │   └── AutopilotMonitor.Agent.Core/         # Core monitoring logic
│   ├── Backend/                        # Azure Functions
│   │   └── AutopilotMonitor.Functions/
│   ├── Shared/                         # Shared models/contracts
│   │   └── AutopilotMonitor.Shared/
│   └── Web/                            # Frontend
│       └── autopilot-monitor-web/
├── scripts/
│   ├── Bootstrap/                      # Intune deployment scripts
│   └── Deployment/                     # Azure deployment scripts
├── rules/                              # YAML rule definitions
└── docs/                               # Documentation
```

## Phase 1 Goals (Current)

- [x] Project structure setup
- [ ] Bootstrap PowerShell script
- [ ] Basic .NET agent with log tailing
- [ ] Azure Functions ingestion API
- [ ] Session registration and event storage
- [ ] Minimal web UI (session list, timeline)

## Getting Started

### Prerequisites

- .NET Framework 4.8 SDK (for Agent)
- .NET 8 SDK (for Azure Functions)
- Node.js 18+ (for Web UI)
- Azure subscription
- Visual Studio 2022 or VS Code

### Development Setup

1. **Clone and restore packages**
   ```bash
   git clone <your-repo>
   cd Autopilot-Monitor
   dotnet restore
   ```

2. **Set up local Azure Functions**
   ```bash
   cd src/Backend/AutopilotMonitor.Functions
   cp local.settings.json.template local.settings.json
   # Edit local.settings.json with your Azure Storage connection string
   ```

3. **Set up web frontend**
   ```bash
   cd src/Web/autopilot-monitor-web
   npm install
   cp .env.local.template .env.local
   # Edit .env.local with your API endpoint
   ```

4. **Build the agent**
   ```bash
   cd src/Agent/AutopilotMonitor.Agent
   dotnet build
   ```

### Running Locally

1. **Start Azure Functions** (in one terminal)
   ```bash
   cd src/Backend/AutopilotMonitor.Functions
   func start
   ```

2. **Start Web UI** (in another terminal)
   ```bash
   cd src/Web/autopilot-monitor-web
   npm run dev
   ```

3. **Test agent** (as Administrator)
   ```bash
   cd src/Agent/AutopilotMonitor.Agent/bin/Debug
   .\AutopilotMonitor.Agent.exe --console
   ```

## Documentation

- [Architecture Overview](docs/architecture.md)
- [Agent Development](docs/agent-development.md)
- [Rule Engine](docs/rule-engine.md)
- [Deployment Guide](docs/deployment.md)

## Contributing

We welcome community contributions! See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## License

MIT License - see [LICENSE](LICENSE) for details

## Roadmap

### Phase 1: Foundation (Current)
- Bootstrap and basic agent
- Simple ingestion pipeline
- Minimal UI

### Phase 2: Intelligence
- Rule engine
- Diagnosis panel
- Troubleshooting bundles

### Phase 3: Polish
- Fleet dashboard
- User-facing portal
- Multi-tenant support

### Phase 4: Advanced
- Pre-provisioning support
- Custom rule authoring
- Integration APIs
