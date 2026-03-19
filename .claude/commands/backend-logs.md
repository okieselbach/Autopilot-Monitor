# Backend Logs Skill

Du analysierst Azure Functions Backend-Logs (Application Insights) um Probleme zu debuggen. Du baust KQL-Queries, fuehrst sie aus und korrelierst die Ergebnisse mit dem Source Code.

## Input

`$ARGUMENTS` enthaelt entweder:
- Eine Beschreibung des Problems (z.B. "warum schlagen die ingest calls fehl")
- Einen Funktionsnamen (z.B. "IngestEventsFunction" oder "IngestEvents")
- Einen Zeitraum und/oder spezifischen Fehler (z.B. "errors letzte Stunde")
- Leer = Health-Overview der letzten Stunde

## Hilfsskript

Nutze das Helper-Script `.claude/scripts/query-appinsights.sh` fuer alle Abfragen:

```bash
.claude/scripts/query-appinsights.sh "<KQL-Query>" [timespan]
```

- Timespan-Format: ISO 8601 Duration (z.B. PT1H, PT24H, P7D)
- Default-Timespan: PT1H (letzte Stunde)
- Das Script nutzt `$AUTOPILOT_MONITOR_APPINSIGHTS_ID` fuer die App Insights App-ID
- Ergebnis ist ein JSON-Array mit Objekten (Spaltenname → Wert)

## Wichtig: Datenmodell

Die `requests`-Tabelle ist bei diesem Projekt leer (.NET isolated worker Eigenheit). Alle Function-Ausfuehrungsdaten liegen in der `traces`-Tabelle:
- **FunctionStarted**: `message` beginnt mit `Executing 'Functions.<Name>'`
- **FunctionCompleted**: `message` beginnt mit `Executed 'Functions.<Name>'` und enthaelt `(Succeeded, ...)` oder `(Failed, ...)`
- `customDimensions` enthaelt JSON mit `prop__functionName`, `prop__status`, `prop__executionDuration`, `prop__invocationId`
- `operation_Name` = Function-Name (ohne "Functions." Prefix)

## Schritt 1: Problem verstehen und Strategie waehlen

Analysiere `$ARGUMENTS` und waehle die passende Strategie:

### Strategie A: Fehler-Analyse (User beschreibt ein Problem)
→ Weiter mit Schritt 2a

### Strategie B: Funktions-Analyse (User nennt eine Function)
→ Weiter mit Schritt 2b

### Strategie C: Allgemeine Uebersicht (Input leer oder User will Status sehen)
→ Weiter mit Schritt 2c

## Schritt 2a: Fehler-Uebersicht

### Exceptions pruefen:

```bash
.claude/scripts/query-appinsights.sh "exceptions | where timestamp > ago(1h) | summarize count() by outerMessage, problemId | order by count_ desc | take 20"
```

Falls leer, Zeitraum auf 24h erweitern:

```bash
.claude/scripts/query-appinsights.sh "exceptions | where timestamp > ago(24h) | summarize count() by outerMessage, problemId | order by count_ desc | take 20" PT24H
```

### Details zum relevantesten Fehler:

```bash
.claude/scripts/query-appinsights.sh "exceptions | where timestamp > ago(1h) | where outerMessage contains '<fehler_text>' | project timestamp, outerMessage, innermostMessage, details[0].rawStack, operation_Id | order by timestamp desc | take 5"
```

### Fehlgeschlagene Functions:

```bash
.claude/scripts/query-appinsights.sh "traces | where timestamp > ago(1h) | where message startswith 'Executed' | where message contains 'Failed' | project timestamp, message, operation_Name, operation_Id, customDimensions | order by timestamp desc | take 20"
```

### Warnings und Errors in Traces:

```bash
.claude/scripts/query-appinsights.sh "traces | where timestamp > ago(1h) | where severityLevel >= 2 | summarize count() by message, severityLevel | order by count_ desc | take 20"
```

## Schritt 2b: Function-Requests analysieren

Uebersicht einer Function (Erfolge vs Fehler):

```bash
.claude/scripts/query-appinsights.sh "traces | where timestamp > ago(1h) | where message startswith 'Executed' | where operation_Name == '<function_name>' | where message contains 'Succeeded' or message contains 'Failed' | extend status = iif(message contains 'Succeeded', 'Succeeded', 'Failed') | extend durationMs = toint(extract('Duration=([0-9]+)ms', 1, message)) | summarize total=count(), succeeded=countif(status == 'Succeeded'), failed=countif(status == 'Failed'), avgMs=avg(durationMs), p95Ms=percentile(durationMs, 95) by operation_Name"
```

Letzte fehlgeschlagene Ausfuehrungen:

```bash
.claude/scripts/query-appinsights.sh "traces | where timestamp > ago(1h) | where message startswith 'Executed' | where operation_Name == '<function_name>' | where message contains 'Failed' | project timestamp, message, operation_Id | order by timestamp desc | take 10"
```

Alle Traces einer Function (letzte Ausfuehrungen):

```bash
.claude/scripts/query-appinsights.sh "traces | where timestamp > ago(1h) | where operation_Name == '<function_name>' | project timestamp, message, severityLevel, operation_Id | order by timestamp desc | take 50"
```

## Schritt 2c: Health-Overview

Alle Functions mit Erfolgsrate:

```bash
.claude/scripts/query-appinsights.sh "traces | where timestamp > ago(1h) | where message startswith 'Executed' | where message contains 'Succeeded' or message contains 'Failed' | extend status = iif(message contains 'Succeeded', 'Succeeded', 'Failed') | extend durationMs = toint(extract('Duration=([0-9]+)ms', 1, message)) | summarize total=count(), failed=countif(status == 'Failed'), avgMs=avg(durationMs) by operation_Name | extend failRate=round(100.0 * failed / total, 1) | order by failed desc, total desc | take 30"
```

## Schritt 3: Tiefere Analyse mit Operation-ID

Wenn du eine operation_Id hast, hole ALLE Telemetrie dazu:

```bash
.claude/scripts/query-appinsights.sh "union traces, exceptions, dependencies | where operation_Id == '<op_id>' | order by timestamp asc | project timestamp, itemType, message, outerMessage, severityLevel, operation_Name, customDimensions" PT24H
```

## Schritt 4: Trace-Logs durchsuchen

Nach Suchbegriff:

```bash
.claude/scripts/query-appinsights.sh "traces | where timestamp > ago(1h) | where message contains '<suchbegriff>' | project timestamp, message, severityLevel, operation_Name, operation_Id | order by timestamp desc | take 30"
```

Severity-Level: 0=Verbose, 1=Information, 2=Warning, 3=Error, 4=Critical

## Schritt 5: Dependency-Analyse (externe Aufrufe)

```bash
.claude/scripts/query-appinsights.sh "dependencies | where timestamp > ago(1h) | where success == false | summarize count() by name, type, target, resultCode | order by count_ desc | take 20"
```

## Schritt 6: Zeitreihen-Analyse (Pattern erkennen)

```bash
.claude/scripts/query-appinsights.sh "traces | where timestamp > ago(24h) | where message startswith 'Executed' | where operation_Name == '<function_name>' | where message contains 'Succeeded' or message contains 'Failed' | extend status = iif(message contains 'Succeeded', 'Succeeded', 'Failed') | summarize total=count(), errors=countif(status == 'Failed') by bin(timestamp, 15m) | order by timestamp asc" PT24H
```

## Schritt 7: Code-Korrelation

Nachdem du die Fehler identifiziert hast, lies den relevanten Code:

### Backend Functions (nach Function-Name suchen)
- Alle Functions: `src/Backend/AutopilotMonitor.Functions/Functions/`
- Unterordner: Admin, Bootstrap, Config, Diagnostics, Feedback, Galactic, Infrastructure, Ingest, Metrics, Progress, Reports, Rules, Sessions
- Security: `src/Backend/AutopilotMonitor.Functions/Security/`
- Services: `src/Backend/AutopilotMonitor.Functions/Services/`
- Middleware: `src/Backend/AutopilotMonitor.Functions/Middleware/`

### Haeufige Fehlerquellen
- **401/403**: `SecurityValidationExtensions.cs`, `AuthenticationMiddleware.cs`, `PolicyEnforcementMiddleware.cs`
- **429 Rate Limiting**: `RateLimitService.cs`
- **Table Storage**: `TableStorageService*.cs`
- **Deserialization**: JSON-Parsing im Function Body
- **Tenant-Config**: `TenantConfigurationService.cs`

## Ausgabe-Format

1. **Problem-Zusammenfassung**: Was wurde gefunden (1-3 Saetze)
2. **Fehler-Details**: Exceptions, fehlgeschlagene Functions mit Kontext
3. **Timeline**: Wann das Problem begann/endet, ob es andauert
4. **Root-Cause Analyse**: Korrelation mit Code, was den Fehler verursacht
5. **Empfehlung**: Was getan werden sollte

## Hinweise

- Die Env-Variable `AUTOPILOT_MONITOR_APPINSIGHTS_ID` enthaelt die Application Insights App-ID
- `az monitor app-insights query` braucht aktiven Azure CLI Login (`az login`)
- Die `requests`-Tabelle ist leer — nutze immer `traces` fuer Function-Ausfuehrungen
- Bei grossen Ergebnismengen immer `| take N` verwenden
- `operation_Id` verbindet Traces → Exceptions → Dependencies einer einzelnen Ausfuehrung
- `operation_Name` = Function-Name (z.B. "IngestEvents", "GetAgentConfig")
- `cloud_RoleName` = `autopilotmonitor-api`
- Sampling ist aktiv — bei Traces koennen einzelne Eintraege fehlen
- Zeitraum-Mapping: PT1H=1h, PT6H=6h, PT24H=24h, P7D=7 Tage
- Wenn der User einen Zeitraum nennt (z.B. "letzte 6 Stunden"), passe sowohl den KQL-Filter (`ago(6h)`) als auch den Timespan-Parameter (`PT6H`) an
- **KQL-Escaping**: Vermeide `extract()` mit Regex-Backslashes — das Bash→az→KQL Escaping ist fragil. Nutze stattdessen `iif(message contains ..., ...)` oder `has`/`contains` Operatoren
- Nutze einfache Anfuehrungszeichen fuer die KQL-Query im Script-Aufruf, damit Bash keine Variablensubstitution macht
