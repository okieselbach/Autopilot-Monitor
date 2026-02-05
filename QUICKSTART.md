# Quick Start Guide

Get Autopilot Monitor running in 10 minutes!

## Prerequisites Check

Before starting, ensure you have:
- [ ] Windows 10/11
- [ ] .NET Framework 4.8 SDK
- [ ] .NET 8 SDK
- [ ] Node.js 18+
- [ ] Visual Studio 2022 or VS Code

Verify installations:
```bash
dotnet --version   # Should show 8.x or higher
node --version     # Should show v18.x or higher
```

## Step 1: Build the .NET Projects (2 minutes)

```bash
# Open a terminal in the project root
cd C:\Code\GitHubRepos\Autopilot-Monitor

# Restore all packages
dotnet restore

# Build everything
dotnet build
```

Expected output: `Build succeeded`

## Step 2: Install Azure Storage Emulator (2 minutes)

### Option A: Azurite (Recommended)
```bash
npm install -g azurite

# Start Azurite
azurite --silent --location c:\azurite
```

### Option B: Azure Storage Emulator
If you have the legacy Azure Storage Emulator installed, start it:
```bash
AzureStorageEmulator.exe start
```

## Step 3: Start the Backend API (2 minutes)

Open a new terminal:
```bash
cd src\Backend\AutopilotMonitor.Functions

# Start Azure Functions
func start
```

Expected output:
```
Azure Functions Core Tools
...
Functions:
  IngestEvents: [POST] http://localhost:7071/api/events/ingest
  RegisterSession: [POST] http://localhost:7071/api/sessions/register
```

## Step 4: Start the Web UI (3 minutes)

Open another terminal:
```bash
cd src\Web\autopilot-monitor-web

# Install dependencies (first time only)
npm install

# Copy environment template
copy .env.local.template .env.local

# Start development server
npm run dev
```

Expected output: `Ready on http://localhost:3000`

## Step 5: Test the Agent (2 minutes)

Open PowerShell **as Administrator**:
```powershell
cd src\Agent\AutopilotMonitor.Agent\bin\Debug\net48

# Run in console mode
.\AutopilotMonitor.Agent.exe --console
```

Expected output:
```
Autopilot Monitor Agent - Console Mode
======================================

Session ID: <some-guid>
API URL: http://localhost:7071
Log Directory: C:\ProgramData\AutopilotMonitor\Logs

Agent is running. Press Enter to stop...
```

## Verify It's Working

1. **Check the Agent Console** - Should show "Agent started" event
2. **Check the Functions Console** - Should show incoming requests
3. **Open Browser** - Navigate to http://localhost:3000
4. **View Dashboard** - You should see the Autopilot Monitor homepage

## What's Next?

### Explore the Code
- [Agent Code](src/Agent/AutopilotMonitor.Agent.Core/) - Monitoring logic
- [Backend API](src/Backend/AutopilotMonitor.Functions/) - Azure Functions
- [Web UI](src/Web/autopilot-monitor-web/) - Next.js dashboard

### Read Documentation
- [Getting Started Guide](docs/getting-started.md) - Detailed setup
- [Architecture Overview](docs/architecture.md) - How it all works
- [Contributing Guide](CONTRIBUTING.md) - How to contribute

### Try It Out
1. Modify the agent to emit custom events
2. Add a new API endpoint
3. Create a new page in the web UI
4. Write a rule for the rule engine (Phase 2)

## Troubleshooting

### Agent won't start
- Run as Administrator
- Check .NET Framework 4.8 is installed
- View logs in `C:\ProgramData\AutopilotMonitor\Logs\`

### Functions won't start
- Ensure Azurite/Storage Emulator is running
- Check `local.settings.json` exists
- Install Azure Functions Core Tools v4

### Web UI shows errors
- Run `npm install` again
- Check `.env.local` exists
- Verify Functions are running on port 7071

### Can't connect to API
- Check Windows Firewall
- Verify Functions console shows the correct URLs
- Try `curl http://localhost:7071/api/sessions/register`

## Next Development Steps

1. **Phase 1 Completion**
   - Implement Azure Table Storage integration
   - Add real-time session monitoring
   - Create session detail page

2. **Phase 2: Intelligence**
   - Build rule engine
   - Add diagnosis panel
   - Implement troubleshooting bundles

3. **Phase 3: Production**
   - Deploy to Azure
   - Configure Intune deployment
   - Set up monitoring and alerts

## Getting Help

- Check [README.md](README.md) for overview
- Read [docs/getting-started.md](docs/getting-started.md) for details
- Open an issue on GitHub
- Review the code - it's well-commented!

---

**You're ready to go!** Start exploring and building. 🚀
