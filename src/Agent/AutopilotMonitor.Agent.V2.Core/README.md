# AutopilotMonitor.Agent.V2.Core

**Owner:** V2-Agent clean-slate library. Plan [REFACTOR_AGENT_V2.md](../../../plans/REFACTOR_AGENT_V2.md) §2.16.

## Strict Separation Rule (plan §2.16 — merge block if violated)

This project references **only**:
- `AutopilotMonitor.Shared`
- `AutopilotMonitor.DecisionCore`

It must **never** reference `AutopilotMonitor.Agent.Core` or `AutopilotMonitor.Agent`.
Required legacy code is **copied**, not referenced — see copy list below. Once copied,
a file belongs to V2.Core and is maintained here independently.

Verification (CI gate):
```
grep -r "AutopilotMonitor\.Agent\.Core" src/Agent/AutopilotMonitor.Agent.V2*
```
Must return zero matches.

## Guiding Principle — V2 is Kernel-Only

The V2 refactor replaces **only** the decision/completion kernel. Every other capability
(gather rules, analyzers, diagnostics upload, config polling, timezone, server-driven actions,
summary dialog launch, env-var resolution, version checks, registry-based signal tracking,
network / DO / performance telemetry, self-update, bootstrap→cert-auth handover, self-destruct
cleanup) is preserved verbatim. See user feedback memory
`feedback_v2_kernel_only_refactor.md` for the policy.

SKIP list is closed to plan §2.11 items:
- `CompletionStateMachine` + partials
- `CompletionGuards`
- `EnrollmentTracker.CompletionLogic`
- `IEnrollmentFlowHandler` + `ClassicAutopilotFlow` / `DevicePreparationFlow` / `EnrollmentFlowFactory`
- `WhiteGloveClassifier` (scoring ported to `DecisionCore.Classifiers.WhiteGloveSealingClassifier` in M3)
- `WhiteGloveSignals`
- `EnrollmentStatePersistence`
- Shadow infrastructure (`ShadowProcessTrigger`, `EmitShadowDiscrepancyEvent`, `shadow_discrepancy`)
- `CheckSignalCorrelatedWhiteGlove()` as direct completion invoker
- `TryEmitEnrollmentComplete` / related direct completion paths
- EnrollmentType string checks in guard code
- Event emissions: `decision_process_completion`, `completion_check`, `shadow_discrepancy`,
  decision-trace parts of `agent_trace`

Everything else from `Agent.Core` → **COPY** into V2.Core (this project).

## Copy List (source of truth: [tasks/m2-copy-list.md](../../../tasks/m2-copy-list.md))

After M2 copy phase: this README is updated with the full per-file inventory + adjustments
applied (namespace renames, V2-specific path constants, etc.). Until then,
`tasks/m2-copy-list.md` is the working document.

## Folder Structure

Target layout mirrors plan §7 R7 guidance:

```
Signals/            — V2-specific signal adapters (wrap copied collectors; M4)
State/              — V2 local state glue (not the pure DecisionState — that lives in DecisionCore)
Engine/             — (empty here; decision engine lives in AutopilotMonitor.DecisionCore)
Classifiers/        — (empty here; classifiers live in AutopilotMonitor.DecisionCore)
Transport/          — TelemetrySpool, TelemetryUploadOrchestrator, BackendApiClient (M4)
Persistence/        — SignalLogWriter, JournalWriter, SnapshotPersistence (M4)
SignalAdapters/     — one adapter per collector (M4)
Orchestration/      — V2-Orchestrator (replaces legacy MonitoringService in M4)
Collectors/         — Copies of the 8 decision-relevant + 4 telemetry-only collectors
Infrastructure/     — Logger, Config, Security, BackendApiClient baseline, CleanupService, …
```
