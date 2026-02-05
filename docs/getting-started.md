# Getting Started with Autopilot Monitor

This guide will help you set up and run Autopilot Monitor locally for development and testing.

## Prerequisites

### Required
- Windows 10/11 (for agent development and testing)
- .NET Framework 4.8 SDK
- .NET 8 SDK
- Visual Studio 2022 or VS Code with C# extensions
- Node.js 18+ and npm
- Azure Storage Emulator or Azurite (for local storage)
- Azure Functions Core Tools v4

### Optional
- Git
- PowerShell 5.1 or later
- Microsoft Intune tenant (for production deployment)

## Quick Start (5 Minutes)

### 1. Clone and Build

```bash
# Clone the repository
git clone <your-repo-url>
cd Autopilot-Monitor

# Restore and build all .NET projects
dotnet restore
dotnet build
```

### 2. Start Local Storage

Install and start Azurite (Azure Storage Emulator):

```bash
npm install -g azurite
azurite --silent --location c:\azurite --debug c:\azurite\debug.log
```

Or use the Azure Storage Emulator if you have it installed.

### 3. Start Azure Functions Backend

```bash
cd src/Backend/AutopilotMonitor.Functions
func start
```

The API will be available at `http://localhost:7071`

### 4. Start Web UI

```bash
cd src/Web/autopilot-monitor-web

# Install dependencies (first time only)
npm install

# Copy environment template
cp .env.local.template .env.local

# Start development server
npm run dev
```

The web UI will be available at `http://localhost:3000`

### 5. Test the Agent

Open a new PowerShell window **as Administrator**:

```powershell
cd src/Agent/AutopilotMonitor.Agent/bin/Debug/net48
.\AutopilotMonitor.Agent.exe --console
```

You should see the agent start and begin sending telemetry to the backend.

## Detailed Setup

### Backend Configuration

The Azure Functions use `local.settings.json` for configuration. This file is created from the template:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AzureTableStorageConnectionString": "UseDevelopmentStorage=true",
    "AzureBlobStorageConnectionString": "UseDevelopmentStorage=true"
  }
}
```

For production, replace `UseDevelopmentStorage=true` with your Azure Storage connection strings.

### Agent Configuration

The agent reads configuration from the Windows Registry (set by the bootstrap script) or falls back to defaults. For testing, you can modify [Program.cs](../src/Agent/AutopilotMonitor.Agent/Program.cs):

```csharp
return new AgentConfiguration
{
    ApiBaseUrl = "http://localhost:7071",
    SessionId = Guid.NewGuid().ToString(),
    TenantId = "test-tenant",
    // ... other settings
};
```

### Web UI Configuration

Create `.env.local` in the web project:

```env
NEXT_PUBLIC_API_BASE_URL=http://localhost:7071
```

This tells the web UI where to find the backend API.

## Testing End-to-End

### 1. Verify Backend is Running

Open a browser and navigate to:
```
http://localhost:7071/api/sessions/register
```

You should see a response (even if it's an error, it confirms the function is running).

### 2. Run the Agent

Start the agent in console mode (as Administrator):

```powershell
cd src/Agent/AutopilotMonitor.Agent/bin/Debug/net48
.\AutopilotMonitor.Agent.exe --console
```

### 3. Check Logs

Agent logs are written to:
```
C:\ProgramData\AutopilotMonitor\Logs\agent_YYYYMMDD.log
```

Backend logs appear in the Functions console output.

### 4. View in Web UI

Open `http://localhost:3000` and you should see the dashboard (currently shows placeholder data in Phase 1).

## Common Issues

### Azure Functions won't start

**Error**: `Missing value for AzureWebJobsStorage`

**Solution**: Ensure Azurite or Azure Storage Emulator is running, or update `local.settings.json` with valid connection strings.

### Agent can't connect to API

**Error**: `Error uploading events: The remote name could not be resolved`

**Solution**:
1. Verify Azure Functions is running at `http://localhost:7071`
2. Check Windows Firewall isn't blocking the connection
3. Update agent configuration with correct API URL

### Web UI shows blank page

**Solution**:
1. Check browser console for errors
2. Verify `npm run dev` completed without errors
3. Check `.env.local` exists and has correct API URL

### Permission Errors

Many agent operations require Administrator privileges:
- Reading certain event logs
- Writing to Program Files
- Installing as a Windows Service

Always run the agent as Administrator during development.

## Development Workflow

### Typical Development Session

1. **Start infrastructure** (Azurite, Functions)
   ```bash
   # Terminal 1: Azurite
   azurite --silent --location c:\azurite

   # Terminal 2: Functions
   cd src/Backend/AutopilotMonitor.Functions
   func start

   # Terminal 3: Web UI
   cd src/Web/autopilot-monitor-web
   npm run dev
   ```

2. **Make changes** to agent, backend, or web code

3. **Test changes**
   - Agent: Rebuild and run in console mode
   - Backend: Hot reload should pick up changes
   - Web: Hot reload should pick up changes

4. **Debug**
   - Agent: Attach Visual Studio debugger or use console output
   - Backend: Use Functions console logs or attach debugger
   - Web: Use browser DevTools

### Building for Release

```bash
# Build all projects in Release mode
dotnet build -c Release

# Build web UI for production
cd src/Web/autopilot-monitor-web
npm run build
```

## Next Steps

- Read the [Architecture Overview](architecture.md)
- Learn about [Deploying to Azure](deployment.md)
- Explore the [API Documentation](api-reference.md)
- Understand the [Rule Engine](rule-engine.md) (Phase 2)

## Getting Help

- Check the [README](../README.md) for general information
- Review logs in `C:\ProgramData\AutopilotMonitor\Logs\`
- Check Azure Functions output for backend errors
- Use browser DevTools for web UI issues
