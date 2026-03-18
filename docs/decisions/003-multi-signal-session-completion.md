# ADR-003: Multi-Signal Session Completion

**Status:** Accepted
**Date:** 2026-03

## Context

Windows Autopilot enrollment can follow multiple paths, and no single event reliably indicates "enrollment is done" across all scenarios:

- **ESP (Enrollment Status Page)** may or may not be present
- **Windows Hello** provisioning may be skipped, succeed, or time out
- **Windows Device Preparation (v2)** has no ESP at all
- **White Glove (Pre-Provisioning)** splits enrollment into two distinct phases
- Devices may reboot mid-enrollment, losing in-memory state

## Decision

Implement 3 independent completion paths, any of which can trigger `enrollment_complete`:

1. **IME Pattern Path** — The existing pattern-based detection from Intune Management Extension logs (legacy, most reliable for app-centric enrollments)

2. **ESP + Hello Composite Path** — ESP final exit event + Hello completion/skip/timeout. For device-only ESP (no Account Setup), a 5-minute timer after Device Setup exit classifies as device-only.

3. **Desktop + Hello Path (No-ESP)** — Desktop arrival (explorer.exe under real user) + Hello completion. Used when ESP is absent (e.g., WDP v2).

**Supporting infrastructure:**
- `EnrollmentStatePersistence` — crash recovery for completion signals (persists seen signals to disk)
- `DesktopArrivalDetector` — polls explorer.exe every 30s, validates user identity (excludes SYSTEM, DefaultUser*)
- Signal audit trail — terminal events include `signalsSeen` and `signalTimestamps`
- Completion check throttling — observability events at 1x/min/source
- WDP v2 gate skip — `enrollmentType == "v2"` skips desktop arrival gate

**Hello behavior:**
- `HelloCompletionTimeoutSeconds` = 300 (5 minutes)
- `HelloOutcome` tracks: `completed`, `skipped`, `timed_out`

## Consequences

- Covers all known Autopilot enrollment paths (Classic ESP, WDP v2, White Glove, device-only)
- State persists across reboots — no lost completions
- Each path is independently testable
- Adding new completion signals requires only extending `EnrollmentTracker.CompletionLogic.cs`
- Complexity concentrated in one partial class file (~789 lines) — acceptable given the domain complexity
