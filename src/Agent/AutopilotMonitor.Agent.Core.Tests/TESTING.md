# Agent Unit Tests

## Overview

This test suite protects the Autopilot Monitor Agent against regressions in the enrollment state machine, security guards, log parsing, and crash recovery. The agent runs on real enrollment devices where debugging is difficult — these tests catch bugs before deployment.

**Framework:** xUnit 2.9.2 | **Target:** .NET Framework 4.8 | **Mocking:** None (pure logic tests with callbacks)

## Risk Matrix

| Risk Category | Concrete Bug | Test Suite | Impact |
|---|---|---|---|
| **Double enrollment events** | Two `enrollment_complete` = corrupted session data | CompletionTests | Session data corruption in backend |
| **Hybrid Join false-completion** | ESP exit during mid-enrollment reboot triggers premature completion | CompletionTests (Reboot Gate) | Agent self-destructs before enrollment finishes |
| **ESP resumption missed** | After hybrid join reboot: Hello state not reset, stale timers fire | EventHandlerTests | Wrong completion signals |
| **Phase bleed** | Device-phase apps appear in AccountSetup timeline | AppPackageStateListTests | Incorrect enrollment timeline |
| **Security bypass** | Path traversal in gather rules or diagnostics collection | GatherRuleGuardsTests, DiagnosticsPathGuardsTests | Data exfiltration from device |
| **Wrong device-only classification** | 3-boolean decision tree misclassifies deployments | CompletionTests | Thousands of deployments affected |
| **Premature completion** | Desktop signal triggers completion while ESP is still active | CompletionTests (ESP Gate) | Enrollment fails silently |
| **Endless enrollment wait** | Hello not configured but agent waits indefinitely | CompletionTests | Zombie agent until 6h timeout |
| **Log parsing crash** | Unexpected date format in IME log | CmTraceLogParserTests | Agent stops processing events |
| **State loss after crash** | Hybrid join flag, signal timestamps lost on restart | PersistenceTests | Duplicate events, wrong completion path |
| **Prefix spoofing** | `SOFTWARE\MicrosoftEvil` matches `SOFTWARE\Microsoft` prefix | GatherRuleGuardsTests | Unauthorized registry access |
| **Desktop false-positive** | SYSTEM account explorer.exe triggers desktop arrival | DesktopArrivalDetectorTests | Premature enrollment completion |

## Test Suites

### AppPackageStateTests

**File:** `Tracking/AppPackageStateTests.cs`
**Source:** `Monitoring/Tracking/AppPackageState.cs`

Tests app installation state transition guards. Prevents illegal state downgrades, broken auto-skip logic for inverse-detection apps, and incorrect upgrade-only protection from Win32AppState simplification.

### AppPackageStateListTests

**File:** `Tracking/AppPackageStateListTests.cs`
**Source:** `Monitoring/Tracking/AppPackageStateList.cs`

Tests phase isolation (ignore list), dependency cascade (Error/Postponed propagation), circular dependency protection, and completion detection including auto-skip of untouched dependency-only packages.

### CmTraceLogParserTests

**File:** `Tracking/CmTraceLogParserTests.cs`
**Source:** `Monitoring/Tracking/CmTraceLogParser.cs`

Tests CMTrace log format parsing used for IME log files. Covers single-digit months (M-d-yyyy), fractional second truncation (>7 digits), and graceful handling of non-CMTrace lines.

### LogFilePositionTrackerTests

**File:** `Tracking/LogFilePositionTrackerTests.cs`
**Source:** `Monitoring/Tracking/LogFilePositionTracker.cs`

Tests incremental log file reading: position tracking, log rotation detection (file shrinks), and crash recovery via position restoration.

### GatherRuleGuardsTests

**File:** `Collectors/GatherRuleGuardsTests.cs`
**Source:** `Monitoring/Collectors/GatherRuleGuards.cs`

Tests security guards for remote gather rules. Critical: segment-bounded matching prevents prefix spoofing, C:\Users always blocked (even unrestricted), hard-blocked command patterns (Invoke-WebRequest etc.) apply in all modes, max command length enforced.

### DiagnosticsPathGuardsTests

**File:** `Collectors/DiagnosticsPathGuardsTests.cs`
**Source:** `Monitoring/Collectors/DiagnosticsPathGuards.cs`

Tests path validation for configurable diagnostics collection. Path traversal prevention via GetFullPath normalization, C:\Users hard block, System32\config hard block, environment variable expansion.

### DesktopArrivalDetectorTests

**File:** `Collectors/DesktopArrivalDetectorTests.cs`
**Source:** `Monitoring/Collectors/DesktopArrivalDetector.cs`

Tests user exclusion logic. SYSTEM, LOCAL SERVICE, DefaultUser*, domain-prefixed accounts must be excluded. Real users (CONTOSO\john.doe) must not be filtered. Null/empty = excluded (fail-safe).

### EnrollmentStatePersistenceTests

**File:** `Tracking/EnrollmentStatePersistenceTests.cs`
**Source:** `Monitoring/Tracking/EnrollmentStatePersistence.cs`

Tests state serialization round-trip including all fields (IsHybridJoin, signal timestamps, SignalsSeen list). Verifies graceful handling of missing files and corrupt JSON.

## Architecture Notes

- **No mocking framework needed** — the agent uses `Action<EnrollmentEvent>` callbacks, making it easy to capture emitted events in tests
- **`InternalsVisibleTo`** allows access to `internal` members like `DesktopArrivalDetector.IsExcludedUser`
- **State machine tests** use `EnrollmentStatePersistence` with temp directories to pre-load state, then trigger completion paths
- All tests are **deterministic** (no timing, no OS calls) and **fast** (pure logic, minimal I/O)

## Running Tests

```bash
dotnet test src/Agent/AutopilotMonitor.Agent.Core.Tests/ --nologo -v quiet
```
