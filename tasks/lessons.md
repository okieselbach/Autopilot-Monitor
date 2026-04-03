# Lessons Learned

<!-- Updated after every correction from the user. Review at session start. -->

## Rules

<!-- Pattern: What went wrong → What to do instead -->

## Code Style

## Architecture

## Testing

## Common Mistakes

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
