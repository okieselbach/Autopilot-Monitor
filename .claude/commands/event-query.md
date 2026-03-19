# Event Query Skill

Du suchst und analysierst Events aus Azure Table Storage fuer Debugging und Weiterentwicklung. Du kannst Events nach Typ suchen, Kontext um ein bestimmtes Event herum anzeigen, oder einzelne Events abrufen.

## Input

`$ARGUMENTS` enthaelt eine natuerlichsprachige Beschreibung was gesucht wird. Beispiele:

- `"finde app_tracking_summary events mit vielen app namen"` — Suche nach EventType mit Kriterien
- `"zeige mir die 5 events vor und nach event abc123... in session def456..."` — Kontext-Fenster
- `"event abc123... in session def456..."` — Einzelnes Event abrufen
- `"alle error_detected events in session def456..."` — Events nach Typ in einer Session
- `"finde phase_transition events"` — Globale Suche ueber alle Events

## Hilfsskript

Nutze das Helper-Script `.claude/scripts/query-table.sh` fuer alle Table-Abfragen:

```bash
.claude/scripts/query-table.sh <TableName> "<OData-Filter>" [select_fields] [top_count]
```

Das Script:
- Parst den Connection String aus `$AUTOPILOT_MONITOR_TABLE_CS`
- Baut die REST API URL zusammen
- Gibt JSON zurueck
- `top_count` begrenzt die Ergebnismenge serverseitig (optional)

## Schritt 1: Intent erkennen

Analysiere `$ARGUMENTS` und waehle die passende Strategie:

### Strategie A: Events nach Typ suchen (mit optionalen Kriterien)
User sucht Events eines bestimmten EventType, ggf. mit Inhaltskriterien.
→ Weiter mit Schritt 2a

### Strategie B: Kontext-Fenster (Events um ein bestimmtes Event herum)
User will Events VOR und NACH einem bestimmten Event sehen.
→ Weiter mit Schritt 2b

### Strategie C: Einzelnes Event abrufen
User will ein spezifisches Event mit allen Details sehen.
→ Weiter mit Schritt 2c

## TenantId ermitteln (nur wenn SessionId angegeben)

Wenn eine SessionId angegeben ist, zuerst TenantId aus der Sessions-Table holen:

```bash
.claude/scripts/query-table.sh Sessions "RowKey eq '${SESSION_ID}'" "PartitionKey,RowKey,Status"
```

- **PartitionKey** = TenantId
- Events-PartitionKey wird dann: `{TenantId}_{SessionId}`

Bei globaler Suche (kein SessionId) ist kein TenantId noetig.

## Schritt 2a: Events nach Typ suchen

### Standard: Globale Suche (ueber alle Events)

```bash
.claude/scripts/query-table.sh Events "EventType eq '${EVENT_TYPE}'" "" 20
```

Das ist ein Table Scan — in der Preview-Phase kein Problem wegen geringer Datenmenge. `$top=20` als Default-Limit, anpassen wenn User mehr oder weniger will.

### Optional: Eingeschraenkt auf eine Session

Wenn User eine SessionId angibt:

```bash
.claude/scripts/query-table.sh Events "PartitionKey eq '${TENANT_ID}_${SESSION_ID}' and EventType eq '${EVENT_TYPE}'"
```

### Optional: Eingeschraenkt auf einen Tenant

Alle Events des Typs holen und per `jq` nach TenantId filtern (Table Storage unterstuetzt kein `startswith`):

```bash
.claude/scripts/query-table.sh Events "EventType eq '${EVENT_TYPE}'" "" 50 | jq '[.value[] | select(.PartitionKey | startswith("'${TENANT_ID}'_"))]'
```

### Mit Inhaltskriterien (DataJson durchsuchen)

Wenn User nach Inhalten sucht (z.B. "mit vielen app namen"), erst Events holen, dann DataJson mit `jq` filtern:

```bash
# Beispiel: app_tracking_summary mit vielen Apps
.claude/scripts/query-table.sh Events "EventType eq 'app_tracking_summary'" "" 50 | jq '[.value[] | select(.DataJson | fromjson | .apps | length > 5)]'
```

Die `jq`-Filter muessen je nach Suchkriterium angepasst werden. Schau dir zuerst ein paar Beispiel-Events an um die DataJson-Struktur zu verstehen, bevor du filterst.

### Mit Severity-Filter

```bash
# Nur Warnings und hoeher
.claude/scripts/query-table.sh Events "EventType eq '${EVENT_TYPE}' and Severity ge 2" "" 20
```

## Schritt 2b: Kontext-Fenster

User will N Events vor und nach einem bestimmten Event sehen (Default: 5).

### 1. Ziel-Event finden

Per EventId:

```bash
.claude/scripts/query-table.sh Events "PartitionKey eq '${TENANT_ID}_${SESSION_ID}' and EventId eq '${EVENT_ID}'"
```

Oder per anderer Identifikation (z.B. EventType + ungefaehrer Zeitpunkt). Aus dem Ergebnis den **RowKey** merken.

### 2. Events davor holen

```bash
.claude/scripts/query-table.sh Events "PartitionKey eq '${TENANT_ID}_${SESSION_ID}' and RowKey lt '${TARGET_ROW_KEY}'" "" 200
```

Da Table Storage kein `$orderby` unterstuetzt und RowKeys aufsteigend zurueckkommen, kommen die Events chronologisch. Nutze `jq` um die letzten N zu extrahieren:

```bash
... | jq '[.value | sort_by(.RowKey) | .[-5:]]'
```

### 3. Events danach holen

```bash
.claude/scripts/query-table.sh Events "PartitionKey eq '${TENANT_ID}_${SESSION_ID}' and RowKey gt '${TARGET_ROW_KEY}'" "" 5
```

Hier funktioniert `$top=N` direkt, da RowKeys aufsteigend sind.

### 4. Kombinierte Timeline ausgeben

Zeige die Events in chronologischer Reihenfolge mit dem Ziel-Event deutlich markiert:

```
... (events davor)
>>> TARGET EVENT >>> EventType | Severity | Message
... (events danach)
```

## Schritt 2c: Einzelnes Event abrufen

```bash
.claude/scripts/query-table.sh Events "PartitionKey eq '${TENANT_ID}_${SESSION_ID}' and EventId eq '${EVENT_ID}'"
```

Wenn kein SessionId bekannt: globale Suche per EventId (Table Scan):

```bash
.claude/scripts/query-table.sh Events "EventId eq '${EVENT_ID}'" "" 1
```

Zeige ALLE Felder des Events, inklusive DataJson geparsed und formatiert.

## Ausgabe-Format

### Einzelnes Event
Alle Properties strukturiert anzeigen. DataJson IMMER parsen und als formatierten JSON-Block darstellen:

```
EventId:    abc123...
EventType:  app_tracking_summary
Severity:   Info (1)
Phase:      DeviceSetup (2)
Source:     Agent
Timestamp:  2024-03-15T10:23:45.123Z
Message:    App tracking summary collected
Session:    def456...
Tenant:     ghi789...

DataJson:
{
  "apps": [...],
  "totalApps": 12,
  ...
}
```

### Event-Liste (Suche/Kontext)
Kompakte Tabelle mit den wichtigsten Feldern:

```
RowKey (kurz)     | EventType              | Sev  | Phase        | Message (gekuerzt)
------------------|------------------------|------|--------------|--------------------
...0001           | phase_transition       | Info | DeviceSetup  | Phase changed to...
...0002           | app_install_started    | Info | DeviceSetup  | Installing App X
>>> ...0003       | error_detected         | Err  | DeviceSetup  | Failed to install  <<<
...0004           | app_install_failed     | Err  | DeviceSetup  | App X failed
...0005           | phase_transition       | Info | AccountSetup | Phase changed to...
```

Bei Interesse an einem bestimmten Event aus der Liste: DataJson auf Nachfrage anzeigen oder direkt wenn es fuer die Frage relevant ist.

## Bekannte EventTypes

Referenz der gaengigsten EventTypes:

**Phase & Lifecycle:** `phase_transition`, `esp_phase_changed`, `enrollment_complete`, `enrollment_failed`, `whiteglove_complete`, `whiteglove_resumed`, `completion_check`

**App Installation:** `app_install_started`, `app_install_completed`, `app_install_failed`, `app_install_skipped`, `app_download_started`, `download_progress`

**App Tracking:** `app_tracking_summary`, `app_tracking_started`, `app_tracking_completed`

**Network:** `network_state_change`, `network_connectivity_check`

**System:** `error_detected`, `log_entry`, `performance_snapshot`, `gather_result`, `gather_rules_collection_completed`, `agent_metrics_snapshot`

**ESP:** `esp_state_change`, `esp_ui_state`, `esp_failure`, `esp_provisioning_status`

**Andere:** `cert_validation`, `desktop_arrived`, `script_completed`, `script_failed`

## Enum-Mappings

**Severity:** Trace=-1, Debug=0, Info=1, Warning=2, Error=3, Critical=4

**Phase:** Unknown=0, DevicePreparation=1, DeviceSetup=2, AccountSetup=3, DeviceESP=4, UserESP=5, Complete=6, PreProvisioning=7

## Hinweise

- DataJson ist ein JSON-String als Table-Property — IMMER parsen und anzeigen
- `performance_snapshot` und `agent_metrics_snapshot` Events standardmaessig ausblenden (Hochfrequenz-Rauschen), ausser explizit angefragt
- RowKey-Format `yyyyMMddHHmmssfff_NNNNNNNNNN` — String-Vergleich funktioniert fuer chronologische Sortierung
- EventId ist eine Property, KEIN Key — Suche per EventId ohne PartitionKey ist ein Table Scan
- Bei grossen Ergebnismengen `$top` nutzen und User informieren wenn Ergebnisse abgeschnitten wurden
- Fuer cross-session Suchen ist Table Scan OK in der Preview — bei Wachstum spaeter einschraenken
- Die Env-Variable `AUTOPILOT_MONITOR_TABLE_CS` enthaelt den Connection String mit SAS Token
