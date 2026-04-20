# Lessons Learned

<!-- Updated after every correction from the user. Review at session start. -->

## Rules

<!-- Pattern: What went wrong → What to do instead -->

## Code Style

## Architecture

## Testing

## Common Mistakes

### Peripheral capability classification: function over location (2026-04-20)
**What went wrong:** In M4.5.a legacy-cleanup ich habe `ImeTrackerStatePersistence.cs` (unter `Monitoring/Enrollment/Ime/`) als "enrollment-legacy" gelöscht. Tatsächlich persistiert sie collector-internal IME-Tracker-State: LogFilePositionTracker-Byte-Offsets, `SeenAppIds` (Phase-Isolation), komplette `AppPackageStateList` mit DO-Metriken + Install-Progress + AppType + AttemptNumber + DetectionResult + ErrorPatterns. Das ist peripheral capability per `feedback_v2_kernel_only_refactor` (App-Download-Monitor, Install-Monitor, Per-App-Summaries — Produkt-Kernfeatures), NICHT DecisionEngine-State. SignalLog-Replay deckt das NICHT ab. Ohne die Persistenz: nach Restart Byte-0-Re-Parse aller IME-Logs, Event-Duplikate, Phase-Bleed, verlorener In-Flight-Download-Progress. Ich hatte die Regel in meinem eigenen Plan-Doc zitiert und trotzdem angewandt — weil Kategorisierung nach Pfad ("Enrollment" im Ordner) statt nach Funktion (was persistiert die Datei?) erfolgte, Plan-Doc + Explore-Agent-Output ohne Kontrolle übernommen wurden, und weil `…` am Ende der Feedback-Liste nicht als "non-exhaustive" interpretiert wurde.
**Rule:** Vor jedem `rm`/`git rm` eines Files in V2.Core (insbesondere unter `Monitoring/Enrollment/`, `Monitoring/Transport/`, `Monitoring/Runtime/`): `Read` die Datei, frag "DecisionEngine-Concern (Stage, Hypothesis, Deadlines, Facts) oder Collector-Concern (Byte-Offsets, App-Progress, WMI-Cache, Phase-Isolation)?". Collector-Concern bleibt — egal wo die Datei im Pfad liegt. Zusatz-Check: "Was bricht nach Restart ohne diese Datei?" — wenn Re-Parse/Verlust-Progress/Phase-Bleed → Peripheral, bleibt. Plan-Doc + Explore-Agent sind Startpunkt, nicht Autorität.
**Severity:** PRODUCTION — hätte App-Tracking komplett zerschossen nach Agent-Restart. Gefangen durch User-Review vor Deploy.
**Reference:** [feedback_peripheral_classify_by_function.md](C:/Users/OliverKieselbach/.claude/projects/c--Code-GitHubRepos-Autopilot-Monitor/memory/feedback_peripheral_classify_by_function.md)

### Timeline sequence must NEVER regress in the curated UI view (2026-04-04)
**What went wrong:** `groupEventsByPhase` was called without `preventPhaseRegression: true` for normal sessions and WhiteGlove pre-provisioning. A mid-enrollment reboot emits a new `agent_started` (Phase 0 = "Start"), which regressed the active phase back to Start, dumping post-reboot events (seq 173+) into the Start section — breaking the sequential appearance.
**Rule:** ALL `groupEventsByPhase` calls MUST use `{ preventPhaseRegression: true }`. The curated timeline sequence must appear continuous within each phase section. Reboots and agent restarts must NOT create phase regression — events stay in whatever phase was active before the reboot.
**Severity:** UX — confuses users, violates core timeline principle.

### Table Storage mapping defaults must match domain defaults (2026-03-31)
**What went wrong:** Added `NtpServer` mapping with `?? ""` fallback. Existing tenants had no value stored → got empty string instead of `"time.windows.com"` → agent NTP check broke.
**Rule:** When adding missing Table Storage read mappings, the `??` fallback MUST match the domain/model default (check `AgentConfigResponse`, `GetAgentConfigFunction`, or the property initializer). Never use empty string as fallback for fields that have a meaningful default.
**Severity:** PRODUCTION — silently breaks agent functionality for all existing tenants.

### PowerShell scripts deployed via Intune MUST be pure ASCII (2026-03-30)
**What went wrong:** Em-dash characters `—` (U+2014) and Unicode symbols (✓, →, ✗) in Bootstrap .ps1 scripts caused cascading PowerShell parse errors in production. The IME (Intune Management Extension) uses PowerShell 5.1 which reads scripts without BOM as ANSI — multi-byte UTF-8 chars get corrupted.
**Rule:** ALL `.ps1` files under `scripts/Bootstrap/` (and any other scripts deployed via Intune) MUST contain only ASCII characters (0x00-0x7F). No em-dashes, no Unicode symbols, no special quotes. Use `-` instead of `—`, `[OK]` instead of `✓`, etc.
**How to check:** `LC_ALL=en_US.UTF-8 grep -Pn '[^\x00-\x7F]' <file>` — must return no matches.
**Severity:** PRODUCTION BREAKING — agent installation fails completely on all devices.
