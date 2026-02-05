# Bootstrap Scripts

## Install-AutopilotMonitor.ps1

This PowerShell script is designed to be deployed via Microsoft Intune as a PowerShell Script during the Autopilot enrollment process.

### Purpose

The bootstrap script:
1. Generates a unique session ID for tracking this enrollment
2. Collects device information (serial number, manufacturer, model, etc.)
3. Stores configuration in the registry
4. Installs and starts the Autopilot Monitor agent service

### Deployment via Intune

1. **Navigate to Intune Portal**
   - Go to **Devices** > **Scripts** > **Add** > **Windows 10 and later**

2. **Script Settings**
   - **Name**: `Autopilot Monitor Bootstrap`
   - **Description**: `Deploys and configures the Autopilot Monitor agent`
   - **Script location**: Upload `Install-AutopilotMonitor.ps1`
   - **Run this script using the logged-on credentials**: `No` (run as SYSTEM)
   - **Enforce script signature check**: `No` (or sign the script if required)
   - **Run script in 64-bit PowerShell**: `Yes`

3. **Script Parameters**
   You can modify the script to include your API URL and Tenant ID, or use Intune's parameter feature:
   ```powershell
   -ApiBaseUrl "https://your-api.azurewebsites.net" -TenantId "your-tenant-id"
   ```

4. **Assignment**
   - Assign to your Autopilot device group
   - **Important**: Scripts run during the **Device ESP** phase, which is early enough for monitoring

### Manual Testing

To test the script locally:

```powershell
# Run as Administrator
.\Install-AutopilotMonitor.ps1 -ApiBaseUrl "http://localhost:7071" -TenantId "test-tenant"
```

### Log Location

The script logs all activities to:
```
C:\ProgramData\AutopilotMonitor\Bootstrap.log
```

### Registry Configuration

The script stores configuration in:
```
HKLM:\SOFTWARE\AutopilotMonitor
```

Values stored:
- `SessionId` - Unique GUID for this enrollment session
- `TenantId` - Your tenant identifier
- `ApiBaseUrl` - Backend API endpoint
- `SerialNumber` - Device serial number
- `InstallDate` - When the bootstrap ran

### Phase 1 Limitations

In Phase 1, the script assumes the agent binaries are already present or manually deployed. Automatic download and installation will be added in a future phase.

For now, you can:
1. Pre-deploy the agent to `C:\Program Files\AutopilotMonitor\`
2. Manually install the service before running the bootstrap
3. Or modify the script to download from your own CDN/blob storage

### Service Installation

To manually install the agent as a Windows Service:

```powershell
# Build the agent first
cd src\Agent\AutopilotMonitor.Agent
dotnet build -c Release

# Install service (run as Administrator)
sc.exe create "AutopilotMonitor" binPath= "C:\Program Files\AutopilotMonitor\AutopilotMonitor.Agent.exe" start= auto
sc.exe description "AutopilotMonitor" "Monitors and reports Windows Autopilot enrollment progress"
sc.exe start "AutopilotMonitor"
```

### Troubleshooting

1. **Script fails to run in Intune**
   - Check the Intune script execution logs
   - Verify the device is running Windows 10/11
   - Ensure no conflicting policies block script execution

2. **Service doesn't start**
   - Check `C:\ProgramData\AutopilotMonitor\Logs\agent_*.log`
   - Verify the agent executable exists at the expected path
   - Check Windows Event Viewer for service startup errors

3. **Configuration not saved**
   - Verify the script ran with SYSTEM privileges
   - Check if registry path creation succeeded
   - Review the bootstrap log for errors
