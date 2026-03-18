# ADR-001: On-Demand SAS URL Delivery for Diagnostics Upload

**Status:** Accepted
**Date:** 2026-02
**Supersedes:** Original design with SAS URL in agent config response stored on disk

## Context

The agent needs to upload diagnostics ZIP packages to Azure Blob Storage. The original design included the long-lived SAS URL directly in the `agent/config` response, which was then persisted in `remote-config.json` on the device.

**Problems with the original approach:**
- SAS URL persisted on disk in plaintext (`remote-config.json`) — accessible to any local admin
- URL visible in agent logs
- No per-device access control — every device with the cached URL could upload at any time

## Decision

Instead of delivering the SAS URL via the config response, the agent calls `POST /api/diagnostics/upload-url` immediately before each upload. The backend returns the long-lived SAS URL (configured in tenant settings) in the response body. The agent uses it in-memory for the upload and discards it — it is never written to disk or cached in config.

**Config response changes:**
- Removed: `DiagnosticsBlobSasUrl` from agent config delivery
- Added: `DiagnosticsUploadEnabled` (bool) — controls whether the agent attempts upload
- Added: `POST /api/diagnostics/upload-url` endpoint (`GetDiagnosticsUploadUrlFunction`)

## Consequences

- SAS URL never persisted on device — only held in-memory for the duration of the upload
- Backend can deny the URL per-device (blocked devices, rate limiting, disabled tenants)
- Old agents that still read from `remote-config.json` fall back gracefully — they won't have a SAS URL and simply skip upload
- Slightly higher latency (one extra HTTP call before upload) — acceptable since uploads are infrequent
