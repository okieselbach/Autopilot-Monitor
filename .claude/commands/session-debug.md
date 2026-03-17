# Session Debug Skill

Du analysierst eine Autopilot-Monitor Session anhand ihrer SessionId. Du holst dir selbststaendig alle noetigen Daten aus Azure Table Storage via REST API und korrelierst sie mit dem Code.

## Input

Der User gibt eine SessionId (GUID) an, optional gefolgt von einer Frage.
Beispiel: `/session-debug abc12345-1234-1234-1234-123456789abc warum ist die Session failed?`

Die gesamte Eingabe wird als `$ARGUMENTS` uebergeben. Die SessionId ist das erste Wort (GUID-Format), alles danach ist die Frage des Users.

## Hilfsskript

Nutze das Helper-Script `.claude/scripts/query-table.sh` fuer alle Table-Abfragen:

```bash
.claude/scripts/query-table.sh <TableName> "<OData-Filter>" [select_fields]
```

Das Script:
- Parst den Connection String aus `$AUTOPILOT_MONITOR_TABLE_CS`
- Baut die REST API URL zusammen
- Gibt JSON zurueck

## Schritt 1: Session-Metadaten holen

Zuerst die Session aus der **Sessions** Table holen um die TenantId zu bekommen:

```bash
.claude/scripts/query-table.sh Sessions "RowKey eq '${SESSION_ID}'"
```

Aus dem Ergebnis:
- **PartitionKey** = TenantId
- **RowKey** = SessionId
- Wichtige Felder: `Status`, `CurrentPhase`, `EnrollmentType`, `StartedAt`, `CompletedAt`, `FailureReason`, `EventCount`, `DurationSeconds`, `AgentVersion`, `IsPreProvisioned`, `IsHybridJoin`, `IsUserDriven`, `OsBuild`, `SerialNumber`, `DeviceName`, `Manufacturer`, `Model`

Fasse die Session-Metadaten kurz zusammen bevor du weiter machst.

## Schritt 2: Events holen

Mit TenantId und SessionId die Events aus der **Events** Table holen:

```bash
.claude/scripts/query-table.sh Events "PartitionKey eq '${TENANT_ID}_${SESSION_ID}'"
```

Events-Struktur:
- **PartitionKey**: `{TenantId}_{SessionId}`
- **RowKey**: `{Timestamp:yyyyMMddHHmmssfff}_{Sequence:D10}` (chronologisch sortiert)
- **EventType**: z.B. `phase_transition`, `app_install_started`, `app_install_completed`, `error_detected`, `esp_page_change`, `hello_screen_detected`, `desktop_arrival`, `enrollment_complete`, `enrollment_failed`, `performance_snapshot`, `agent_metrics_snapshot`, `collector_started`, `collector_stopped`, `completion_check`, etc.
- **Severity**: Trace=-1, Debug=0, Info=1, Warning=2, Error=3, Critical=4
- **Source**: Agent, IME, Registry, WMI, Network
- **Phase**: 0=Unknown, 1=DevicePreparation, 2=DeviceSetup, 3=AccountSetup, 4=DeviceESP, 5=UserESP, 6=Complete, 7=PreProvisioning
- **Message**: Menschenlesbarer Text
- **DataJson**: Strukturierte Metadaten als JSON String (oft die wichtigsten Details!)
- **Sequence**: Reihenfolge bei gleichem Timestamp

## Schritt 3: Bei Bedarf weitere Tables abfragen

Nur abfragen wenn fuer die Frage relevant:

### RuleResults (automatische Analyse-Ergebnisse)
```bash
.claude/scripts/query-table.sh RuleResults "PartitionKey eq '${TENANT_ID}_${SESSION_ID}'"
```

### AppInstallSummaries (App-Installationen)
```bash
.claude/scripts/query-table.sh AppInstallSummaries "PartitionKey eq '${TENANT_ID}'" "PartitionKey,RowKey,AppName,Status,DurationSeconds,StartedAt,CompletedAt"
```
Dann filtern: nur Eintraege deren RowKey mit der SessionId beginnt.

### TenantConfiguration
```bash
.claude/scripts/query-table.sh TenantConfiguration "PartitionKey eq '${TENANT_ID}' and RowKey eq 'config'"
```

## Schritt 4: Analyse

Nachdem du die Daten hast:

1. **Chronologie aufbauen**: Events nach Timestamp sortiert durchgehen
2. **Phasen-Uebergaenge**: phase_transition Events zeigen den Ablauf
3. **Fehler identifizieren**: Events mit Severity >= 2 (Warning) hervorheben
4. **DataJson parsen**: Oft stecken die Details in DataJson - immer reinschauen bei relevanten Events
5. **Code-Korrelation**: Wenn der User eine Frage zum Verhalten hat, lies den relevanten Code im Repo und erklaere anhand der Events was passiert ist
6. **Completion-Logik**: Bei Fragen zur Session-Completion, schau in:
   - `src/Agent/AutopilotMonitor.Agent.Core/Monitoring/Tracking/EnrollmentTracker*.cs`
   - Die 3 Completion-Pfade: IME pattern, ESP+Hello composite, Desktop+Hello (no-ESP)

## Wichtige Code-Pfade fuer Event-Analyse

- **Event-Ingestion**: `src/Backend/AutopilotMonitor.Functions/Functions/IngestEventsFunction.cs`
- **Session-Updates**: `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.Sessions.cs`
- **Enrollment Tracker**: `src/Agent/AutopilotMonitor.Agent.Core/Monitoring/Tracking/EnrollmentTracker*.cs`
- **ESP/Hello Tracker**: `src/Agent/AutopilotMonitor.Agent.Core/Monitoring/Collectors/EspAndHelloTracker*.cs`
- **Desktop Arrival**: `src/Agent/AutopilotMonitor.Agent.Core/Monitoring/Collectors/DesktopArrivalDetector.cs`
- **Collectors**: `src/Agent/AutopilotMonitor.Agent.Core/Monitoring/Collectors/`
- **Web Session View**: `src/Web/autopilot-monitor-web/app/sessions/[sessionId]/`
- **Shared Models/Enums**: `src/Shared/AutopilotMonitor.Shared/`

## Ausgabe-Format

1. **Session-Uebersicht**: Status, Typ, Dauer, Geraet (kompakt, tabellarisch)
2. **Event-Timeline**: Wichtigste Events chronologisch mit Timestamp, EventType, Message (nicht alle - nur relevante, keine performance_snapshot/agent_metrics_snapshot)
3. **Auffaelligkeiten**: Errors, Warnings, ungewoehnliche Muster
4. **Antwort auf User-Frage**: Falls der User eine spezifische Frage hat, diese gezielt beantworten mit Verweis auf Events UND Code

## Hinweise

- Die Env-Variable `AUTOPILOT_MONITOR_TABLE_CS` enthaelt den Connection String mit SAS Token
- Bei vielen Events (>100) kann die Ausgabe gross werden - fokussiere auf relevante Events, filtere performance_snapshot und agent_metrics_snapshot raus wenn sie nicht relevant sind
- DataJson Felder enthalten oft die wichtigsten Details - IMMER reinschauen bei relevanten Events
- Wenn ein Event unklar ist, schau im Code nach wo dieser EventType erzeugt wird (grep nach dem EventType-String)
- Phase-Enum: Unknown=0, DevicePreparation=1, DeviceSetup=2, AccountSetup=3, DeviceESP=4, UserESP=5, Complete=6, PreProvisioning=7
