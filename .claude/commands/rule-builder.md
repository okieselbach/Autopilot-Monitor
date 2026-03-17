# Rule Builder Skill

Du bist ein Experte fuer das Autopilot Monitor Rule-System. Du erstellst, pruefst und debuggst Gather Rules und Analyze Rules.

## Input

`$ARGUMENTS` enthaelt entweder:
- Eine Beschreibung eines Szenarios das erkannt werden soll (→ Rule erstellen)
- Eine Rule-ID wie `GATHER-NET-001` oder `ANALYZE-APP-003` (→ bestehende Rule pruefen/debuggen)
- Eine Frage zu einer bestehenden Rule (→ analysieren und erklaeren)

## Modus erkennen

1. **Erstellen**: User beschreibt ein Szenario, z.B. "Erkenne wenn BitLocker nicht aktiviert ist" oder "Sammle Proxy-Einstellungen"
2. **Pruefen/Debuggen**: User gibt eine Rule-ID an, z.B. "ANALYZE-APP-001 feuert nicht" oder "pruefe GATHER-DEVICE-005"
3. **Beides erstellen**: Wenn ein Analyse-Szenario Daten braucht die noch nicht gesammelt werden, erstelle ZUERST die Gather Rule und DANN die Analyze Rule

---

## TEIL A: RULE ERSTELLEN

### Schritt 1: Szenario analysieren

Bestimme:
- **Was soll gesammelt/erkannt werden?**
- **Braucht es eine Gather Rule?** (Daten vom Geraet sammeln)
- **Braucht es eine Analyze Rule?** (Gesammelte Events auswerten)
- **Braucht es beides?** (Neue Daten sammeln UND diese dann analysieren)

### Schritt 2: Naechste freie Rule-ID bestimmen

Lies die bestehenden Rules um die naechste freie ID zu finden:

```bash
ls rules/gather/ | sort
ls rules/analyze/ | sort
```

Rule-ID Pattern:
- Gather: `GATHER-{CATEGORY}-{NNN}` (z.B. `GATHER-NET-001`)
- Analyze: `ANALYZE-{CATEGORY}-{NNN}` (z.B. `ANALYZE-APP-011`)

Kategorien: `network`, `identity`, `apps`, `device`, `esp`, `enrollment`
ID-Kuerzel: NET, ID, APPS, DEVICE/DEV, ESP, ENRL/ENROLL

### Schritt 3: Gather Rule erstellen (wenn noetig)

#### Collector-Typen und ihre Felder

**registry** — Windows Registry lesen
```json
{
  "collectorType": "registry",
  "target": "HKLM\\SOFTWARE\\Microsoft\\...",
  "parameters": {
    "valueName": "einzelner Wert",
    "listSubkeys": "true",
    "severityIfExists": "Warning",
    "severityIfNotExists": "Warning"
  }
}
```

**eventlog** — Windows Event Log abfragen
```json
{
  "collectorType": "eventlog",
  "target": "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin",
  "parameters": {
    "eventId": "12",
    "source": "Microsoft-Windows-Kernel-General",
    "maxEntries": "10"
  }
}
```

**wmi** — WMI Query ausfuehren
```json
{
  "collectorType": "wmi",
  "target": "SELECT * FROM Win32_BIOS"
}
```

**file** — Datei-Existenz und optional Inhalt pruefen
```json
{
  "collectorType": "file",
  "target": "C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs",
  "parameters": {
    "readContent": "true"
  }
}
```

**command_allowlisted** — Erlaubten Befehl ausfuehren
```json
{
  "collectorType": "command_allowlisted",
  "target": "dsregcmd /status"
}
```

**logparser** — Log-Dateien mit Regex parsen
```json
{
  "collectorType": "logparser",
  "target": "C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs\\*.log",
  "parameters": {
    "pattern": "(?<error>Error:|Critical:).*",
    "format": "cmtrace",
    "maxLines": "500",
    "trackPosition": "true"
  }
}
```

**json** — JSONPath-Query auf JSON-Datei
```json
{
  "collectorType": "json",
  "target": "C:\\path\\to\\file.json",
  "parameters": {
    "jsonpath": "$.some.path",
    "maxResults": "10"
  }
}
```

**xml** — XPath-Query auf XML-Datei
```json
{
  "collectorType": "xml",
  "target": "C:\\path\\to\\file.xml",
  "parameters": {
    "xpath": "//some/path",
    "maxResults": "10"
  }
}
```

#### Trigger-Typen
- `startup` — Einmal beim Agent-Start
- `phase_change` — Bei Phasen-Wechsel (braucht `triggerPhase`: DevicePreparation, DeviceSetup, AccountSetup, FinalizingSetup, Complete)
- `interval` — Periodisch (braucht `intervalSeconds`)
- `on_event` — Wenn bestimmter Event eintrifft (braucht `triggerEventType`)

#### Guardrails pruefen!

WICHTIG: Vor dem Erstellen IMMER pruefen ob das Target erlaubt ist! Lies `rules/guardrails.json`:

**Registry** — Target muss mit einem erlaubten Prefix beginnen:
- SOFTWARE\Microsoft\Enrollments, SOFTWARE\Microsoft\EnterpriseDesktopAppManagement
- SOFTWARE\Microsoft\Provisioning, SOFTWARE\Microsoft\PolicyManager
- SOFTWARE\Microsoft\Windows\CurrentVersion\MDM
- SOFTWARE\Microsoft\IdentityStore, SYSTEM\CurrentControlSet\Control\CloudDomainJoin
- SOFTWARE\Microsoft\WindowsUpdate, SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate
- SOFTWARE\Microsoft\BitLocker, SYSTEM\CurrentControlSet\Control\BitLockerStatus
- SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings
- SYSTEM\CurrentControlSet\Services\Tcpip
- SOFTWARE\Microsoft\Windows\CurrentVersion\Setup
- SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE
- SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon
- SYSTEM\CurrentControlSet\Services\TPM, SOFTWARE\Microsoft\Tpm
- SOFTWARE\Microsoft\IntuneManagementExtension
- SOFTWARE\Microsoft\SystemCertificates, SOFTWARE\Policies\Microsoft\SystemCertificates
- SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing

**File** — Target muss mit einem erlaubten Prefix beginnen:
- C:\ProgramData\Microsoft\IntuneManagementExtension\Logs
- C:\Windows\CCM\Logs, C:\Windows\Logs, C:\Windows\Panther
- C:\Windows\SetupDiag, C:\ProgramData\Microsoft\DiagnosticLogCSP
- C:\Windows\SoftwareDistribution\ReportingEvents.log

**WMI** — Query muss mit einem erlaubten Prefix beginnen:
- SELECT * FROM Win32_OperatingSystem/ComputerSystem/BIOS/Processor/BaseBoard/Battery/TPM
- SELECT * FROM Win32_NetworkAdapter/NetworkAdapterConfiguration/DiskDrive/LogicalDisk
- SELECT * FROM SoftwareLicensingProduct

**Commands** — Nur diese Befehle sind erlaubt:
- TPM: Get-Tpm, Get-SecureBootPolicy, Get-SecureBootUEFI -Name SetupMode
- BitLocker: Get-BitLockerVolume -MountPoint C:
- Network: Get-NetAdapter, Get-DnsClientServerAddress, Get-NetIPConfiguration, netsh winhttp show proxy, ipconfig /all
- Domain: nltest /dsgetdc:, dsregcmd /status
- Certificate: certutil -store My, PowerShell Get-ChildItem Cert:\LocalMachine\My
- Windows Update: Get-HotFix

Wenn das gewuenschte Target NICHT in den Guardrails ist:
1. Informiere den User dass das Target nicht erlaubt ist
2. Schlage vor, `guardrails.json` zu erweitern (neue Kategorie + Prefix)
3. Erklaere dass nach Aenderung auch `node rules/scripts/combine.js` laufen muss

#### Gather Rule Template

```json
{
  "$schema": "../schema/gather-rule.schema.json",
  "ruleId": "GATHER-{CAT}-{NNN}",
  "title": "Kurzer, beschreibender Titel",
  "description": "Was wird gesammelt und warum.",
  "category": "{category}",
  "version": "1.0.0",
  "author": "Autopilot Monitor",
  "enabled": true,
  "collectorType": "{type}",
  "target": "{target}",
  "parameters": {},
  "trigger": "{trigger}",
  "outputEventType": "gather_{beschreibend}",
  "outputSeverity": "Info",
  "tags": ["{category}", "{weitere}"]
}
```

Pflichtfelder: ruleId, title, collectorType, target, trigger, outputEventType
Optional: description, category, version, author, enabled, isCommunity, parameters, intervalSeconds, triggerPhase, triggerEventType, outputSeverity, tags

### Schritt 4: Analyze Rule erstellen (wenn noetig)

#### Bekannte Event Types (fuer conditions)

Vom Agent emittierte Events:
- `phase_transition` — Phasenwechsel
- `app_install_started` / `app_install_completed` / `app_install_failed` / `app_install_skipped` — App-Lifecycle
- `app_download_started` / `download_progress` — Downloads
- `network_state_change` / `network_connectivity_check` — Netzwerk
- `error_detected` — Fehler erkannt
- `performance_snapshot` — CPU, RAM, Disk (Felder: `cpu_percent`, `memory_used_mb`, `disk_free_gb`)
- `log_entry` — Log-Eintrag
- `esp_state_change` / `esp_ui_state` / `esp_provisioning_status` — ESP Status
- `esp_phase_changed` — ESP Phasenwechsel (Feld: `espPhase`)
- `cert_validation` — Zertifikatspruefung
- `enrollment_complete` / `enrollment_failed` — Enrollment-Ende
- `desktop_arrived` — Desktop erkannt
- `completion_check` — Completion-Pruefung
- `script_completed` / `script_failed` — Skript-Ergebnisse
- `whiteglove_complete` — White Glove fertig
- `gather_result` — Ergebnis einer Gather Rule

WICHTIG: Gather Rules erzeugen Events mit dem `outputEventType` der Gather Rule. Z.B. erzeugt GATHER-DEVICE-005 Events vom Typ `gather_pending_reboot`. Diese koennen in Analyze Rules als eventType referenziert werden!

#### Condition Sources und wie die RuleEngine sie auswertet

**event_type** — Prueft ob Events eines bestimmten Typs existieren
```json
{
  "signal": "beschreibend",
  "source": "event_type",
  "eventType": "app_install_failed",
  "operator": "exists",
  "value": "",
  "required": true
}
```
Mit DataField-Filter:
```json
{
  "signal": "proxy_error",
  "source": "event_type",
  "eventType": "error_detected",
  "dataField": "message",
  "operator": "contains",
  "value": "407 Proxy Authentication",
  "required": true
}
```

**event_data** — Prueft Datenfelder in Events
```json
{
  "signal": "low_disk",
  "source": "event_data",
  "eventType": "performance_snapshot",
  "dataField": "disk_free_gb",
  "operator": "lt",
  "value": "5",
  "required": true
}
```

**event_count** — Zaehlt Events
```json
{
  "signal": "many_failures",
  "source": "event_count",
  "eventType": "app_install_failed",
  "operator": "count_gte",
  "value": "3",
  "required": true
}
```
Pro Gruppe (z.B. pro App):
```json
{
  "signal": "app_retry_storm",
  "source": "event_count",
  "eventType": "app_install_failed",
  "dataField": "appId",
  "operator": "count_per_group_gte",
  "value": "3",
  "required": true
}
```

**phase_duration** — Prueft wie lange eine ESP-Phase dauerte
```json
{
  "signal": "esp_stalled",
  "source": "phase_duration",
  "eventType": "esp_phase_changed",
  "dataField": "espPhase",
  "operator": "equals",
  "value": "DeviceSetup",
  "required": true
}
```
Die eigentliche Dauer-Pruefung erfolgt ueber confidenceFactors:
```json
"confidenceFactors": [
  { "signal": "long_esp", "condition": "phase_duration > 1800", "weight": 40 }
]
```

**app_install_duration** — Prueft App-Installationsdauer
```json
{
  "signal": "slow_install",
  "source": "app_install_duration",
  "eventType": "app_install_completed",
  "operator": "gt",
  "value": "1800"
}
```

**event_correlation** — Korreliert zwei Event-Typen ueber ein gemeinsames Feld
```json
{
  "signal": "start_then_fail",
  "source": "event_correlation",
  "eventType": "app_install_started",
  "correlateEventType": "app_install_failed",
  "joinField": "appId",
  "timeWindowSeconds": 3600,
  "eventAFilterField": "appType",
  "eventAFilterOperator": "equals",
  "eventAFilterValue": "Win32",
  "dataField": "errorCode",
  "operator": "contains",
  "value": "0x80",
  "required": true
}
```

#### Operatoren
- `equals` / `not_equals` — Exakter Vergleich (case-insensitive)
- `contains` / `not_contains` — Teilstring (case-insensitive)
- `regex` / `not_regex` — Regex (case-insensitive, 1s Timeout)
- `gt` / `lt` / `gte` / `lte` — Numerischer Vergleich
- `exists` / `not_exists` — Feld vorhanden/leer
- `count_gte` — Globaler Event-Count >= Wert
- `count_per_group_gte` — Count pro Gruppe (DataField) >= Wert

#### Confidence-System

- `baseConfidence` (0-100): Startwert wenn alle required Conditions matchen
- `confidenceFactors`: Array von zusaetzlichen Gewichten
  - `"condition": "exists"` — Signal existiert in matchedConditions
  - `"condition": "count >= N"` — Event-Count des Signals >= N
  - `"condition": "phase_duration > N"` — Phasendauer > N Sekunden
- `confidenceThreshold` (0-100): Minimum-Score damit ein RuleResult erzeugt wird
- Finale Confidence = min(baseConfidence + sum(matched factor weights), 100)

Best Practices:
- baseConfidence 40-60 fuer einzelne Signale, 50-70 fuer Korrelationen
- confidenceThreshold leicht unter baseConfidence setzen (damit die Rule auch ohne Faktoren feuern kann)
- Faktoren fuer "nice to have" Signale: je 10-20 Gewicht
- required=true fuer Kern-Conditions, required=false fuer optionale Verstaerker

#### Analyze Rule Template

```json
{
  "$schema": "../schema/analyze-rule.schema.json",
  "ruleId": "ANALYZE-{CAT}-{NNN}",
  "title": "Kurzer, beschreibender Titel",
  "description": "Was erkennt diese Rule und warum ist das relevant.",
  "severity": "{info|warning|high|critical}",
  "category": "{category}",
  "version": "1.0.0",
  "author": "Autopilot Monitor",
  "enabled": true,
  "trigger": "{single|correlation}",
  "baseConfidence": 50,
  "conditions": [],
  "confidenceFactors": [],
  "confidenceThreshold": 40,
  "explanation": "Markdown-Erklaerung des erkannten Problems.\n\nDetails und Kontext.",
  "remediation": [
    {
      "title": "Loesungsansatz",
      "steps": ["Schritt 1", "Schritt 2"]
    }
  ],
  "relatedDocs": [
    {
      "title": "Microsoft Learn: Relevanter Artikel",
      "url": "https://learn.microsoft.com/..."
    }
  ],
  "tags": ["{category}", "{weitere}"]
}
```

Pflichtfelder: ruleId, title, severity, category, conditions, explanation
Optional: description, version, author, enabled, isCommunity, trigger, baseConfidence, confidenceFactors, confidenceThreshold, remediation, relatedDocs, tags

### Schritt 5: Rule-Dateien schreiben

1. Schreibe jede Rule als eigene JSON-Datei: `rules/gather/GATHER-{CAT}-{NNN}.json` bzw. `rules/analyze/ANALYZE-{CAT}-{NNN}.json`
2. Fuehre `node rules/scripts/combine.js` aus um `dist/` zu regenerieren
3. Validiere mit Schema: `npx ajv validate -s rules/schema/gather-rule.schema.json -d "rules/gather/GATHER-*.json"` (falls ajv installiert)

### Schritt 6: Erklaerung

Erklaere dem User:
- Was die Rule(s) tun
- Wann sie feuern (Trigger/Conditions)
- Welche Events sie erzeugen/matchen
- Bei Analyze: Wie das Confidence-Scoring funktioniert
- Ob Guardrail-Aenderungen noetig sind

---

## TEIL B: RULE PRUEFEN / DEBUGGEN

### Schritt 1: Rule laden

```bash
cat rules/gather/{RULE_ID}.json   # oder
cat rules/analyze/{RULE_ID}.json
```

### Schritt 2: Schema-Validierung

Pruefe die Rule manuell gegen das Schema:
- Sind alle Pflichtfelder vorhanden?
- Stimmen die Enum-Werte (collectorType, trigger, operator, source, severity, category)?
- Ist das ruleId-Pattern korrekt?
- Bei event_correlation: sind eventType, correlateEventType UND joinField gesetzt?

### Schritt 3: Guardrail-Check (nur Gather Rules)

Pruefe ob das Target in `rules/guardrails.json` erlaubt ist:
- registry: Target-Prefix in registryPrefixes?
- file: Target-Prefix in filePrefixes?
- wmi: Query-Prefix in wmiQueryPrefixes?
- command_allowlisted: Exakter Befehl in allowedCommands?
- logparser: Pfad-Prefix in filePrefixes oder diagnosticsPathPrefixes?

### Schritt 4: RuleEngine-Logik nachvollziehen (nur Analyze Rules)

Simuliere die Auswertung im Kopf:
1. Welche Events muessten in einer Session vorhanden sein?
2. Werden alle required Conditions erfuellt?
3. Stimmen die Operatoren und Werte?
4. Ist der Confidence-Score realistisch erreichbar?
5. Bei event_data: Existiert das DataField im Event.Data Dictionary?
6. Bei regex: Ist der Regex-Pattern korrekt und case-insensitive tauglich?
7. Bei event_correlation: Gibt es realistische Event-Paare mit gleichem JoinField-Wert?

### Schritt 5: Live-Debugging mit Session-Daten

Wenn der User eine SessionId nennt oder fragt warum eine Rule nicht feuert:

1. Events der Session holen (via `.claude/scripts/query-table.sh`)
2. Relevante Events filtern (nach EventType der Conditions)
3. Conditions manuell gegen die Events pruefen
4. Erklaeren warum die Rule (nicht) gefeuert hat

```bash
.claude/scripts/query-table.sh Sessions "RowKey eq '${SESSION_ID}'"
.claude/scripts/query-table.sh Events "PartitionKey eq '${TENANT_ID}_${SESSION_ID}'"
.claude/scripts/query-table.sh RuleResults "PartitionKey eq '${TENANT_ID}_${SESSION_ID}'"
```

### Schritt 6: Code-Korrelation

Bei tiefem Debugging den relevanten Code lesen:

- **Gather-Ausfuehrung**: `src/Agent/AutopilotMonitor.Agent.Core/Monitoring/Collectors/GatherRuleExecutor.cs` und `GatherRuleExecutor.Executors.cs`
- **Guardrails**: `src/Agent/AutopilotMonitor.Agent.Core/Monitoring/Collectors/GatherRuleGuards.cs`
- **RuleEngine**: `src/Backend/AutopilotMonitor.Functions/Services/RuleEngine.cs` und `RuleEngine.ConditionEvaluators.cs`
- **Rule Services**: `src/Backend/AutopilotMonitor.Functions/Services/GatherRuleService.cs` und `AnalyzeRuleService.cs`
- **Shared Models**: `src/Shared/AutopilotMonitor.Shared/Models/Rules/GatherRule.cs` und `AnalyzeRule.cs`

---

## Ausgabe-Format

### Bei Erstellung:
1. **Szenario-Analyse**: Kurze Zusammenfassung was erkannt/gesammelt werden soll
2. **Rule(s)**: Die JSON-Dateien (mit Erklaerung der Design-Entscheidungen)
3. **Guardrail-Status**: Ob das Target erlaubt ist oder Aenderungen noetig sind
4. **Naechste Schritte**: combine.js ausfuehren, testen, ggf. guardrails erweitern

### Bei Debugging:
1. **Rule-Zusammenfassung**: Was die Rule tut
2. **Validierung**: Schema-Konformitaet, Guardrail-Check
3. **Problem-Analyse**: Warum die Rule (nicht) funktioniert
4. **Fix-Vorschlag**: Konkrete Aenderungen mit Begruendung
