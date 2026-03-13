# Session Table Redesign — Dual-Table mit SessionsIndex

## Problem
- `Sessions`-Tabelle: `RowKey = SessionId` (GUID) → zufällige Reihenfolge
- `GetSessionsAsync` muss ALLE Sessions laden und in-memory sortieren
- Hardcoded `maxResults: 100` — kein "Load More" möglich
- Skaliert nicht bei wachsender Session-Anzahl

## Lösung: Dual-Table Architektur

### Tabellen-Layout

```
┌──────────────────────────────────────────────────────┐
│  Sessions Table (UNVERÄNDERT)                        │
│  PartitionKey = TenantId                             │
│  RowKey       = SessionId                            │
│  + NEU: IndexRowKey Property (für Index-Referenz)    │
│                                                      │
│  → Alle 12 Point-Query-Lookups bleiben O(1)          │
│  → Alle Updates/Merges bleiben wie bisher            │
└──────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────┐
│  SessionsIndex Table (NEU)                           │
│  PartitionKey = TenantId                             │
│  RowKey       = "{invertedTicks:D19}_{SessionId}"    │
│  Properties   = Alle SessionSummary-Felder           │
│                                                      │
│  → $top=100 liefert sofort die 100 neuesten Sessions │
│  → RowKey gt '{cursor}' + $top=100 = Load More       │
│  → Kein In-Memory-Sort nötig                         │
└──────────────────────────────────────────────────────┘
```

### RowKey-Berechnung
```csharp
string ComputeIndexRowKey(DateTime startedAt, string sessionId)
    => $"{(DateTime.MaxValue.Ticks - startedAt.Ticks):D19}_{sessionId}";
```
Neueste Sessions = kleinster invertierter Tick = werden zuerst zurückgegeben.

### Performance-Übersicht

| Operation | Tabelle | Methode | Performance |
|-----------|---------|---------|-------------|
| **Neueste 100 laden** | SessionsIndex | `$top=100` | O(1) nativ sortiert |
| **Load More** | SessionsIndex | `RowKey gt '{cursor}' + $top=100` | O(1) |
| **Session Detail** | Sessions | `GetEntityAsync(tenantId, sessionId)` | O(1) Point Query |
| **Status Update** | Sessions + Index | Merge auf beide | 2× O(1) |
| **Event Count++** | Sessions + Index | Merge auf beide | 2× O(1) |
| **Delete** | Sessions + Index | Delete auf beide | 2× O(1) |
| **Galactic Listing** | SessionsIndex | Query alle Partitions + in-memory sort | wie bisher |

**Kein Trade-off.** Listing UND Lookups sind optimal.

---

## Migrations-Strategie: Vollautomatisch

### 1. Lazy Write-Through (ab Deploy)
Jeder Schreibvorgang schreibt automatisch in **beide** Tabellen.
Neue und aktualisierte Sessions sind sofort im Index.

### 2. Startup-Backfill (einmalig beim ersten Deploy)
`TableInitializerService.StartAsync()` prüft ob `SessionsIndex` leer ist.
Wenn ja → alle bestehenden Sessions aus `Sessions` in den Index kopieren.
Läuft einmalig automatisch.

### 3. Maintenance-Backfill (Sicherheitsnetz, alle 2h)
`MaintenanceService.RunAllAsync()` prüft ob Sessions ohne `IndexRowKey` existieren.
Wenn ja → nachmigrieren. Idempotent, fängt Edge Cases ab.

**Ergebnis:** Deploy = fertig. Kein manuelles Script, kein Downtime.

---

## Implementation Plan

### Phase 1: Shared Models + Konstanten

- [ ] **1. `SessionPage` Model** (`ApiModels.cs`)
  ```csharp
  public class SessionPage {
      public List<SessionSummary> Sessions { get; set; } = new();
      public bool HasMore { get; set; }
  }
  ```

- [ ] **2. Tabellen-Konstante** (`Constants.cs`)
  ```csharp
  public const string SessionsIndex = "SessionsIndex";
  ```

### Phase 2: TableStorageService — Index-Infrastruktur

- [ ] **3. Helper: `ComputeIndexRowKey`**
  ```csharp
  private static string ComputeIndexRowKey(DateTime startedAt, string sessionId)
      => $"{(DateTime.MaxValue.Ticks - startedAt.Ticks):D19}_{sessionId}";
  ```

- [ ] **4. Helper: `UpsertSessionIndexAsync`**
  Nimmt ein TableEntity aus der Sessions-Tabelle, berechnet IndexRowKey,
  schreibt/updated den Eintrag in SessionsIndex.
  Speichert `IndexRowKey` auch als Property im Sessions-Entity zurück.

- [ ] **5. Helper: `DeleteSessionIndexAsync`**
  Liest `IndexRowKey` aus Sessions-Entity, löscht den Index-Eintrag.

### Phase 3: Schreibende Methoden — Dual-Write

Jede Methode die in `Sessions` schreibt, schreibt zusätzlich in `SessionsIndex`.
Der `IndexRowKey` wird im Sessions-Entity als Property mitgespeichert,
sodass kein extra Lookup nötig ist.

- [ ] **6. `StoreSessionAsync`** (ProcessRegistration)
  - Nach `UpsertEntityAsync` auf Sessions: `UpsertSessionIndexAsync` aufrufen
  - `IndexRowKey` Property im Sessions-Entity setzen

- [ ] **7. `UpdateSessionStatusAsync`**
  - Entity wird ohnehin per `GetEntityAsync` gelesen → `IndexRowKey` ist verfügbar
  - Nach Merge auf Sessions: Merge auf SessionsIndex mit gleichem IndexRowKey

- [ ] **8. `IncrementSessionEventCountAsync`**
  - Gleich wie #7: Entity wird gelesen, IndexRowKey nutzen, Dual-Merge

- [ ] **9. `SetSessionPreProvisionedAsync`**
  - Aktuell: direkter Merge mit `ETag.All` (kein vorheriger Read)
  - Anpassung: `IndexRowKey` muss bekannt sein → entweder:
    a) Vorher kurzen Read machen um IndexRowKey zu holen, oder
    b) Index-Update in `UpdateSessionStatusAsync` mit abhandeln (wird meist zusammen aufgerufen)
  - Option (a) ist sicherer und konsistenter

- [ ] **10. `UpdateSessionGeoAsync`**
  - Liest Entity bereits (Line 905) → IndexRowKey verfügbar
  - Dual-Merge hinzufügen

- [ ] **11. `UpdateSessionDiagnosticsBlobAsync`**
  - Liest Entity bereits (Line 839) → IndexRowKey verfügbar
  - Dual-Merge hinzufügen

- [ ] **12. `DeleteSessionAsync`**
  - VOR dem Delete: Entity lesen um IndexRowKey zu holen
  - Dann Delete auf Sessions + Delete auf SessionsIndex

### Phase 4: Listing-Methoden — Index nutzen

- [ ] **13. `GetSessionsAsync` → Query auf SessionsIndex**
  ```csharp
  public async Task<SessionPage> GetSessionsAsync(
      string tenantId, int maxResults = 100, string? cursor = null)
  ```
  - Query auf `SessionsIndex` statt `Sessions`
  - Filter: `PartitionKey eq '{tenantId}'` + optional `RowKey gt '{cursor}'`
  - `maxPerPage: maxResults + 1` → `hasMore = (count > maxResults)`
  - Return: `SessionPage { Sessions (maxResults), HasMore }`
  - Kein In-Memory-Sort nötig — RowKey-Reihenfolge = zeitlich sortiert

- [ ] **14. `GetAllSessionsAsync` → Query auf SessionsIndex**
  ```csharp
  public async Task<SessionPage> GetAllSessionsAsync(
      int maxResults = 100, string? cursor = null)
  ```
  - Query auf SessionsIndex ohne PartitionKey-Filter
  - Weiterhin In-Memory-Sort nötig (cross-partition)
  - Cursor-basiert: `StartedAt < cursorStartedAt` Filter

### Phase 5: API-Funktionen

- [ ] **15. `GetSessionsFunction`** — `cursor` Query-Parameter
  - Optional `cursor` aus Query-String parsen
  - Neue Response: `{ success, count, hasMore, sessions }`

- [ ] **16. `GetAllSessionsFunction`** — gleich
  - Optional `cursor` Query-Parameter
  - Neue Response: `{ success, count, hasMore, sessions }`

### Phase 6: Tabellen-Initialisierung + Migration

- [ ] **17. `InitializeTablesAsync`** — SessionsIndex Tabelle anlegen
  - `SessionsIndex` zur Tabellen-Liste hinzufügen

- [ ] **18. Startup-Backfill in `TableInitializerService`**
  - Nach Tabellen-Init: prüfe ob SessionsIndex leer ist
  - Wenn ja: alle Sessions aus Sessions-Tabelle lesen, Index befüllen
  - Sessions ohne `IndexRowKey` Property → IndexRowKey berechnen und zurückschreiben

- [ ] **19. Maintenance-Backfill in `MaintenanceService`**
  - Neue Methode: `BackfillSessionIndexAsync()`
  - Suche Sessions ohne `IndexRowKey` Property
  - Für jede: IndexRowKey berechnen, in Index schreiben, IndexRowKey in Session speichern
  - In `RunAllAsync()` einbinden
  - Idempotent

### Phase 7: Frontend

- [ ] **20. `dashboard/page.tsx` — Load-More-State + Logik**
  - Neue States: `hasMore: boolean`, `loadingMore: boolean`, `cursor: string | null`
  - `fetchSessions(cursor?: string)`:
    - Ohne cursor: Initial Load → `setSessions(newSessions)`
    - Mit cursor: Load More → `setSessions(prev => [...prev, ...newSessions])`
    - URL: `?tenantId=X` oder `?tenantId=X&cursor=...`
  - `loadMore()`:
    - Nimmt `startedAt` + `sessionId` der letzten Session
    - Berechnet cursor (oder benutzt server-seitigen cursor-Wert aus Response)
  - Stats Cards: basieren weiterhin nur auf geladenen Sessions (akzeptabel)

- [ ] **21. `SessionTable.tsx` — "Load More" Button**
  - Neue Props: `hasMore`, `loadingMore`, `onLoadMore`
  - Button am Ende der Tabelle, nach Pagination Controls
  - Zeigt "Load 100 more sessions..." wenn `hasMore` true
  - Loading-Spinner während `loadingMore`
  - Session Count Header: "Sessions (100)" → "Sessions (100+)" wenn hasMore

- [ ] **22. Cursor in API-Response**
  - Backend gibt `cursor` String in Response zurück (= RowKey des letzten Elements)
  - Frontend benutzt diesen opaken String direkt für den nächsten Request
  - Vorteil: Frontend muss IndexRowKey-Format nicht kennen

---

## Dateien die geändert werden

### Shared
1. `src/Shared/.../Models/ApiModels.cs` — `SessionPage` Model
2. `src/Shared/.../Constants.cs` — `SessionsIndex` Tabellen-Name

### Backend — Services
3. `src/Backend/.../Services/TableStorageService.Sessions.cs` — Dual-Write, Index-Helpers, neue Listing-Methoden
4. `src/Backend/.../Services/TableStorageService.Maintenance.cs` — Backfill-Methode
5. `src/Backend/.../Services/MaintenanceService.cs` — Backfill in RunAllAsync einbinden
6. `src/Backend/.../Services/TableInitializerService.cs` — Startup-Backfill

### Backend — Functions
7. `src/Backend/.../Functions/Sessions/GetSessionsFunction.cs` — cursor Param + neue Response
8. `src/Backend/.../Functions/Sessions/GetAllSessionsFunction.cs` — cursor Param + neue Response

### Frontend
9. `src/Web/.../app/dashboard/page.tsx` — Load More State + Handler
10. `src/Web/.../app/dashboard/components/SessionTable.tsx` — Load More Button UI

### Tabellen-Init
11. `src/Backend/.../Services/TableStorageService.cs` (oder wo InitializeTablesAsync lebt) — SessionsIndex anlegen
