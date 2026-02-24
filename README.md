# Autopilot Monitor

Advanced monitoring and troubleshooting solution for Windows Autopilot deployments.

## Overview

Autopilot Monitor provides real-time tracking, intelligent diagnostics, and automated troubleshooting for Windows Autopilot enrollment processes. It consists of:

- **Bootstrap Script** — PowerShell script deployed via Intune that starts monitoring early in the enrollment process
- **Monitoring Agent** — Lightweight .NET application that collects telemetry and evidence during enrollment
- **Backend API** — Azure Functions-based ingestion and processing pipeline
- **Web Dashboard** — Next.js application for real-time monitoring and fleet analytics

## Architecture

For detailed information about the system architecture, components, and data flow, see [Architecture Documentation](architecture.md).

## Documentation

Full documentation is available at **[autopilotmonitor.com/docs](https://www.autopilotmonitor.com/docs)**

## License

MIT License — see [LICENSE](LICENSE) for details
