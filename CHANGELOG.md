# Changelog

## 2026-03-29 — Agent Integrity Verification (SHA-256)

### Security Enhancement

The bootstrapper script and agent self-updater now verify the SHA-256 hash of downloaded
agent packages against `version.json`. Additionally, the backend serves the expected hash
via the agent config endpoint as a second trust channel.

**What changed:**
- `version.json` now includes a `sha256` field with the SHA-256 hash of the agent ZIP
- Bootstrapper (`Install-AutopilotMonitor.ps1`) verifies the hash before installation
- Self-Updater (`SelfUpdater.cs`) verifies the hash before applying updates
- Backend delivers `LatestAgentSha256` via the agent config response (defense in depth)
- CI/CD pipeline and local build scripts compute and publish the hash automatically

**Action required:** Update your Intune bootstrapper script to the latest version
to benefit from SHA-256 integrity verification. Existing deployments continue to work
without changes (fully backward compatible).

The updated bootstrap script is available at:
- Blob Storage: `https://autopilotmonitor.blob.core.windows.net/agent/Install-AutopilotMonitor.ps1`
- GitHub: `scripts/Bootstrap/Install-AutopilotMonitor.ps1`

**Backward compatibility:**
- Old bootstrapper scripts (without hash check) continue to work unchanged
- Old agents (without hash check) continue to self-update normally
- New agents with old `version.json` (no `sha256` field) skip the check gracefully
