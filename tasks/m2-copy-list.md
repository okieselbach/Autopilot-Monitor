# M2 — V2.Core Copy List (Scan-Ergebnis, vor User-Signoff)

**Status:** Scan abgeschlossen am 2026-04-20 (Explore-Subagent). **User-Signoff erhalten 2026-04-20: alle Empfehlungen (A1a, A2a, A3a, A4a, A5 SKIP) + A6 mit expliziter COPY-Direktive für alle fünf Helper.** Leitprinzip vom User festgelegt (siehe `feedback_v2_kernel_only_refactor.md`): **V2-Refactor ist kernel-only, keine Funktionalität verlieren.**

Diese Datei ist das Vorab-Staging; nach Signoff wird der Inhalt in
`src/Agent/AutopilotMonitor.Agent.V2.Core/README.md` final übernommen und hier nur ein Verweis stehen bleiben.

---

## Zusammenfassung

| Kategorie | Anzahl | Entscheidung |
|---|---|---|
| COPY (M2, verbatim) | ~70 Dateien | Infrastruktur + 8 Decision-Collectors + 4 Telemetrie-Collectors + Gather/Analyzer |
| COPY + Mark for M4 Removal | ~4 Dateien | EventSpool, EventUploadOrchestrator, MonitoringService, EnrollmentCompletionHandler (transitional) |
| SKIP (V2 hat native Impl) | ~15 Dateien | CompletionStateMachine, CompletionGuards, EnrollmentTracker.CompletionLogic, IEnrollmentFlowHandler + Flows, WhiteGloveSignals |
| AMBIGUOUS | 6 Einträge | User-Entscheidung nötig (siehe Abschnitt unten) |

---

## 1. Startup / Orchestrierung

| Datei | Flag | Begründung |
|---|---|---|
| `Agent/Program.cs` (Exe) | **SKIP** — neu schreiben | V2 bekommt eigene `Program.V2.cs` |
| `Core/Monitoring/Runtime/MonitoringService.cs` | **COPY + Mark for M4 Removal** | Transitional; wird durch V2Orchestrator + SignalIngress + EffectRunner ersetzt |
| `Core/Monitoring/Runtime/CollectorCoordinator.cs` | **COPY** | Transitional skeleton; M4 ersetzt durch V2-Orchestrator |
| `Core/Monitoring/Runtime/PeriodicCollectorManager.cs` | **COPY** | Periodic telemetry layer |
| `Core/Monitoring/Enrollment/EnrollmentCompletionHandler.cs` | **COPY + Mark for M4 Removal** | M4 ersetzt durch V2 Terminal-Logik |
| `Core/Monitoring/Enrollment/EnrollmentTracker.cs` + alle Partials | **SKIP** | §2.11 — Reducer ersetzt komplett |

## 2. Infrastruktur (Pflicht-Kopie)

- `Core/Logging/AgentLogger.cs`
- `Core/Configuration/AgentConfiguration.cs`
- `Core/Configuration/RemoteConfigService.cs`
- `Core/Security/CertificateHelper.cs`
- `Core/Security/HardwareInfo.cs`
- `Core/Security/SystemPaths.cs` (+ V2-spezifische Pfad-Konstanten anpassen)
- `Core/Security/BootstrapConfigCleanup.cs` (security-kritisch)
- `Core/Security/EnrollmentAwaiter.cs` (security-kritisch)
- `Agent/SelfUpdater.cs` → in V2.Core (+ V2-Integrity-Pfade)
- `Core/Monitoring/Transport/BackendApiClient.cs` (ganze Klasse)
- `Core/Monitoring/Runtime/CleanupService.cs` (+ V2-Scheduled-Task-Name anpassen)
- `Core/Monitoring/Runtime/DiagnosticsPackageService.cs`
- `Core/Monitoring/Runtime/SessionPersistence.cs`
- `Core/Monitoring/Telemetry/DeviceInfo/DeviceInfoCollector.cs` (+ Partials: HardwareSpec, NetworkAndSecurity)
- `Core/Monitoring/Telemetry/DeviceInfo/DeviceInfoProvider.cs`
- `Core/Monitoring/Interop/RegistryNativeMethods.cs`, `ProcessNativeMethods.cs`, `MemoryNativeMethods.cs`
- `Core/Monitoring/Transport/EventSpool.cs` → **COPY + Mark for M4 Removal**
- `Core/Monitoring/Transport/EventUploadOrchestrator.cs` → **COPY + Mark for M4 Removal**

## 3. Decision-relevante Collectors (Pflicht)

- `EspAndHelloTracker.cs`
- `ImeLogTracker.cs` + `.LogProcessing.cs` + `.Handlers.cs`
  - transitive: `ImeProcessWatcher`, `CmTraceLogParser`, `LogFilePositionTracker`, `ImeTrackerStatePersistence`, `LogReplayService`, `AppPackageState`, `AppPackageStateList`, `ScriptExecutionState`
- `ShellCoreTracker.cs`
- `HelloTracker.cs` (ist in `EspAndHelloTracker` eingehängt, eigene Datei)
- `DesktopArrivalDetector.cs`
- `ProvisioningStatusTracker.cs`
- `ModernDeploymentTracker.cs`
- `AadJoinWatcher.cs` + `AadJoinInfo.cs`
- `StallProbeCollector.cs`

## 4. Telemetrie-only Collectors (damit EventTimeline weiter Daten bekommt)

- `NetworkChangeDetector.cs`
- `DeliveryOptimizationCollector.cs`
- `AgentSelfMetricsCollector.cs`
- `PerformanceCollector.cs`
- `Core/Monitoring/Telemetry/CollectorBase.cs`

## 5. Gather-Rules + Analyzers (Infrastruktur)

- `Core/Monitoring/Telemetry/Gather/GatherRuleExecutor.cs`
- `Core/Monitoring/Telemetry/Gather/GatherRuleContext.cs`
- `Core/Monitoring/Telemetry/Gather/GatherRuleGuards.cs`
- `Core/Monitoring/Telemetry/Gather/DiagnosticsPathGuards.cs`
- `Core/Monitoring/Telemetry/Gather/IGatherRuleCollector.cs`
- `Core/Monitoring/Telemetry/Gather/Collectors/*.cs` (alle 8: Command, EventLog, File, Json, Registry, Wmi, Xml, LogParser)
- `Core/Monitoring/Runtime/AnalyzerManager.cs`
- `Core/Monitoring/Telemetry/Analyzers/IAgentAnalyzer.cs`
- `Core/Monitoring/Telemetry/Analyzers/LocalAdminAnalyzer.cs`, `SoftwareInventoryAnalyzer.cs`, `IntegrityBypassAnalyzer.cs`

## 6. M2-Scope-Anpassung (nach vollständigem Inventur-Scan 2026-04-20)

**Entdeckung:** 9+ "Leaf"-Collectors (EspAndHelloTracker, ShellCoreTracker, HelloTracker, ImeLogTracker, LogReplayService, ProvisioningStatusTracker, DeliveryOptimizationCollector, AgentSelfMetricsCollector, DeviceInfoCollector, LogParserCollector, GatherRuleContext, GatherRuleExecutor, UserProfileResolver, DiagnosticsPackageService, PeriodicCollectorManager) referenzieren `EnrollmentTracker`/`IEnrollmentFlowHandler`/etc. aus dem §2.11-SKIP-Pool — als Callback-Sinks. Das ist die Stelle, an der M4 später `SignalAdapter` einzieht.

**Revidierte M2-Strategie (pragmatisch, vom User-Prinzip „kernel-only, keine Funktionalität verlieren" gedeckt):**

- Kopiere das **gesamte** `Agent.Core`-Tree (inkl. §2.11-Klassen wie CompletionStateMachine, CompletionGuards, EnrollmentTracker*, Flows/*, WhiteGloveClassifier/Signals, EnrollmentStatePersistence) nach `Agent.V2.Core` **als Transitional-Copy**.
- Markiere alle §2.11-Klassen mit `[Obsolete("V2 M3/M4 replacement pending — see plans/REFACTOR_AGENT_V2.md §2.11")]`.
- V2-`Program.cs` **instantiiert nichts davon** — M2-Gate „baut grün + terminiert sauber" bleibt erfüllt, keine transitional code paths werden zur Laufzeit betreten.
- M3 schreibt den Reducer in `DecisionCore` (separates Projekt, kein Einfluss auf V2.Core).
- M4 schreibt V2-Orchestrator + SignalAdapters, wired Main darauf um; danach werden die §2.11-transitional-Kopien in V2.Core gelöscht (Cleanup-PR).

**Begründung:** M2 ist Scaffolding, nicht final shipping. Die Alternative (chirurgisches Decoupling der Collector-Callbacks in M2) wäre 9+ Files surgisch anpassen mit Risiko. Kopie-im-Ganzen + Obsolete-Markierung ist mechanisch, fehlerfreundlich, und die Cleanup-Arbeit ist in M4 ohnehin nötig (SignalAdapter-Rewire).

**Trennungsregel bleibt hart:** `grep -r "using AutopilotMonitor\.Agent\.Core" src/Agent/AutopilotMonitor.Agent.V2*` = 0 Matches (Namespace-Umschreibung auf `AutopilotMonitor.Agent.V2.Core` bei der Kopie).

---

## 7. SKIP — V2 hat native Implementierung (Plan §2.11) — **finale Löschung in M4**

- `Core/Monitoring/Enrollment/Completion/CompletionStateMachine.cs`
- `Core/Monitoring/Enrollment/Completion/CompletionGuards.cs`
- `Core/Monitoring/Enrollment/EnrollmentTracker.cs` + `.CompletionLogic.cs` + `.EventHandlers.cs` + `.Diagnostics.cs`
- `Core/Monitoring/Enrollment/Flows/IEnrollmentFlowHandler.cs`, `ClassicAutopilotFlow.cs`, `DevicePreparationFlow.cs`, `EnrollmentFlowFactory.cs`
- `Core/Monitoring/Enrollment/Completion/WhiteGloveClassifier.cs` (Scoring wird in M3 in `WhiteGloveSealingClassifier : IClassifier` portiert — kein Code-Copy)
- `Core/Monitoring/Enrollment/Completion/WhiteGloveSignals.cs`
- `Core/Monitoring/Enrollment/EnrollmentStatePersistence.cs` (V2 hat eigene Persistence-Layer §2.7)

---

## AMBIGUOUS — User-Entscheidung nötig

### A1. `BackendApiClient` — Event-Upload-Methoden verbatim kopieren?

`BackendApiClient.cs` enthält sowohl generisches Plumbing (HTTP, Auth, Retry, NetworkMetrics) als auch event-spezifische Methoden (`UploadEventsAsync`, später ergänzt durch `UploadTelemetryBatchAsync` in M4).

**Optionen:**
- **A1a** Komplette Klasse in M2 kopieren (inkl. `UploadEventsAsync`); M4 fügt `UploadTelemetryBatchAsync` hinzu + markiert Event-Methoden `[Obsolete]`.
- **A1b** In M2 nur Baseline kopieren (HTTP/Auth/Retry); Event-Methoden nicht, V2 nutzt ab M2 nur neu entwickelte Transport-Aufrufe.

**Empfehlung:** A1a — weniger Churn, da `CollectorCoordinator` + `EventUploadOrchestrator` in M2 noch die alten Aufrufe brauchen.

### A2. `DistressReporter.cs` / `EmergencyReporter.cs` — mitkopieren?

Legacy out-of-band-Signalisierung (Pre-Auth-Distress-Endpoint, Emergency-Break). Plan sagt dazu nichts explizit.

**Optionen:**
- **A2a** COPY — V2 nutzt dieselben Backend-Endpoints (Distress/Emergency bleiben).
- **A2b** SKIP — V2 macht alles via `TelemetryTransport`, keine Out-of-Band-Kanäle mehr.
- **A2c** COPY + Mark for M4 Removal, Entscheidung später.

**Empfehlung:** A2a — diese Kanäle sind sicherheits-/reliability-kritische Fallbacks (wenn Token abgelaufen / Backend down); sie orthogonal zum Decision-Refactor behalten.

### A3. `EventSpool` / `EventUploadOrchestrator` — wirklich in M2 mitnehmen?

Plan §2.7a sagt: „TelemetrySpool **ersetzt** den bestehenden EventSpool, nicht parallel." Aber in M2 hat V2 noch keinen `TelemetrySpool` (kommt erst M4).

**Optionen:**
- **A3a** COPY Legacy-Spool + Orchestrator in M2, V2-Exe kann laufen und Events hochschicken. M4 ersetzt gesamte Transport-Schicht.
- **A3b** SKIP in M2, V2-Exe startet in M2 ohne Event-Upload (nur Harness-fähig, keine lokalen VM-Läufe bis M4).

**Empfehlung:** A3a — erfüllt M2-Gate „V2-Agent läuft lokal in Test-VM ein komplettes Enrollment durch" (M4-Gate-Text, aber basal schon in M2 angedeutet).

### A4. `IEnrollmentFlowHandler` + Flows — minimal-Stub oder komplett weg?

Plan §2.11 sagt Flow-Handler existieren nicht im V2. Aber `CollectorCoordinator` (transitional copy) instanziiert einen Flow-Handler im Legacy. Ohne Flows müsste `CollectorCoordinator`-Kopie angepasst werden.

**Optionen:**
- **A4a** SKIP Flows komplett, dafür `CollectorCoordinator` in V2.Core so stutzen, dass der Flow-Aufruf entfällt (minimaler Eingriff in die Kopie).
- **A4b** COPY Flow-Klassen als „Transitional Dead Code", damit Kopie unverändert kompiliert; M4 räumt sie mit dem Orchestrator-Rewrite auf.

**Empfehlung:** A4a — V2 soll von Anfang an ohne Flow-Infrastruktur leben. Plan-konform zu §2.11. Der Eingriff in `CollectorCoordinator`-Kopie ist lokal und dokumentierbar.

### A5. `EnrollmentStatePersistence.cs` — wirklich SKIP?

Persistiert heute Legacy-`EnrollmentTracker`-Flags. V2 hat eigene Persistence (SignalLog + Journal + Snapshot). Aber wenn `EnrollmentTracker` transitional kopiert würde (was wir nicht tun), wäre das auch nötig.

**Empfehlung:** SKIP bestätigen — da `EnrollmentTracker` SKIP ist, ist auch diese Persistence-Klasse obsolet. V2.Core hat eigene Persistence in `Persistence/` (M4).

### A6. Registry/Process-Watcher-Helfer — ALLE FÜNF COPY (User-Direktive)

Der User hat explizit klargestellt: Diese fünf Helper sind load-bearing für essentielle Features und **MÜSSEN** im V2-Agent vorhanden sein.

| Datei | Zweck (User-Begründung) | Flag |
|---|---|---|
| `RegistryWatcher.cs` | ESP-Status-Tracking ohne Polling (Basis für Echtzeit-Signale) | **COPY** |
| `ServerActionDispatcher.cs` | Server-seitig getriebene Actions die an den Client zurückkommen (admin override, force-shutdown, …) | **COPY** |
| `UserSessionProcessLauncher.cs` | Startet den SummaryDialog in der interaktiven User-Session | **COPY** |
| `UserProfileResolver.cs` | Env-Var-Resolution für Gather-Rule-Pfade | **COPY** |
| `VersionCheckEventBuilder.cs` | Version-Check-Telemetrie-Pipeline | **COPY** |

**Leitprinzip (User-Direktive, gespeichert als `feedback_v2_kernel_only_refactor.md`):** V2-Refactor ist kernel-only. Default-Entscheidung für jede Helper-Klasse in `Agent.Core` = **COPY**, außer Plan §2.11 flaggt sie explizit als ersetzt. SKIP-Liste ist geschlossen.

---

## Nach Signoff

1. Erstelle Projekt-Skelette (`Agent.V2`, `Agent.V2.Core`, `DecisionCore.Tests`)
2. Übernehme diese Liste (mit A1–A6 Entscheidungen eingearbeitet) als `src/Agent/AutopilotMonitor.Agent.V2.Core/README.md`
3. Kopiere Dateien einzeln, passe V2-spezifische Konstanten an (Pfade, Task-Namen, Integrity-Files)
4. Prüfe nach jedem Kopier-Schritt: `grep AutopilotMonitor.Agent.Core src/Agent/AutopilotMonitor.Agent.V2*` = 0 Matches
5. Build-Gate: Legacy + V2 beide grün
