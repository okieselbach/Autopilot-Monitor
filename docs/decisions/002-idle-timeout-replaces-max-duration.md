# ADR-002: Activity-Aware Idle Timeout Replaces Static Max Duration

**Status:** Accepted
**Date:** 2026-02
**Supersedes:** Static `MaxCollectorDurationHours` (4h hard cutoff)

## Context

The original design stopped all collectors after a fixed 4-hour window (`MaxCollectorDurationHours`). This caused problems:
- Long enrollments with many apps (>50) were cut off mid-collection
- Sessions with slow network or large downloads lost data
- No way to distinguish "still actively enrolling" from "enrollment finished, agent idle"

## Decision

Replace the static cutoff with an activity-aware idle timeout:

- **`CollectorIdleTimeoutMinutes`** (default: 15) — stops collectors when no "real" events arrive for this duration
- **"Real" events** = everything except `performance_snapshot`, `agent_metrics_snapshot`, and their `_stopped` variants
- Collectors auto-restart when new real activity arrives after idle stop
- `AgentMaxLifetimeMinutes` (default: 360/6h) remains as an absolute safety net

**Implementation:**
- `MonitoringService` tracks `_lastRealEventTime`
- Idle check timer fires every 60 seconds
- Global setting in `AdminConfiguration.CollectorIdleTimeoutMinutes`, delivered via `CollectorConfiguration`

## Consequences

- Long enrollments run to completion — no arbitrary cutoff
- Idle devices stop wasting CPU/network within 15 minutes
- Old agents (not updated) fall back to their built-in 4h default — no breaking change
- The 6h max lifetime timer prevents runaway agents regardless
