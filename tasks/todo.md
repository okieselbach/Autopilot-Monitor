# Session Table Redesign — Inverted-Tick RowKey

## Kernidee

**Aktuell:**
```
PartitionKey = TenantId
RowKey       = SessionId (GUID) ← zufällige Reihenfolge!
```

**Neu:**
```
PartitionKey = TenantId
RowKey       = "{invertedTicks:D19}_{SessionId}"
SessionId    = property (für Lookups)
```

`invertedTicks = (DateTime.MaxValue.Ticks - StartedAt.Ticks).ToString("D19")`

### Warum das funktioniert
- Azure Table Storage gibt Rows in **RowKey-Reihenfolge** zurück (lexikographisch)
- Invertierte Ticks: neueste Sessions haben den **kleinsten** RowKey → kommen zuerst
- `$top=100` gibt direkt die 100 neuesten zurück — **kein In-Memory-Sort nötig!**

### Abfragen werden trivial

**Neueste 100 laden:**
```
PartitionKey eq 'tenant-123' → $top=100
```
→ Sofort die 100 neuesten, direkt aus dem Storage, sortiert.

**"Load More" (nächste 100):**
```
PartitionKey eq 'tenant-123' and RowKey gt '{lastRowKey}' → $top=100
```
→ Server-seitige Cursor-Pagination. Kein In-Memory-Sort, kein Fetch-All.

**Session Detail (by SessionId):**
```
PartitionKey eq 'tenant-123' and SessionId eq 'session-xyz' → $top=1
```
→ Partition-Scan mit Filter statt Point Query. Etwas langsamer als heute, aber bei einigen tausend Sessions pro Tenant immer noch schnell genug (<100ms).

**Galactic (alle Tenants, neueste 100):**
- Ohne PartitionKey-Filter, `$top=100` — da RowKeys über Partitions hinweg nicht global sortiert sind, hier weiterhin In-Memory-Sort nötig. Aber: nur für Galactic Admin, nicht performance-kritisch.

---

## Trade-offs

| Aspekt | Aktuell (RowKey=SessionId) | Neu (RowKey=InvertedTicks_SessionId) |
|--------|----------------------------|--------------------------------------|
| **Listing (neueste 100)** | Fetch ALL → sort in memory | `$top=100` direkt vom Storage |
| **Load More** | Unmöglich / hacky | `RowKey gt cursor` + `$top` |
| **Session Detail Lookup** | Point Query O(1) | Partition filter O(n) mit $top=1 |
| **Upsert (ProcessRegistration)** | Direct by RowKey | Erst lookup, dann upsert |
| **Skalierung** | Wird bei 1000+ Sessions langsam | Konstant schnell |

**Einziger Nachteil:** Detail-Lookups sind etwas langsamer (Partition-Scan statt Point-Query). Bei typischem Scale (< 10.000 Sessions/Tenant) ist das kein Problem.

---

## Implementation Plan

### Phase 1: Backend — Neue RowKey-Struktur

- [ ] **1. Helper-Methode `ComputeSessionRowKey(DateTime startedAt, string sessionId)`**
  - `$"{(DateTime.MaxValue.Ticks - startedAt.Ticks):D19}_{sessionId}"`
  - In TableStorageService als private helper

- [ ] **2. `ProcessRegistrationAsync` anpassen**
  - Lookup: Query mit `SessionId eq '{id}'` statt Point-Query
  - Bei existierender Session: bestehenden RowKey wiederverwenden
  - Bei neuer Session: RowKey aus StartedAt berechnen
  - `SessionId` als Property speichern

- [ ] **3. Alle Session-Lookup-Methoden anpassen**
  - `GetSessionAsync(tenantId, sessionId)` → Query mit SessionId-Property-Filter
  - `DeleteSessionAsync` → erst Lookup, dann Delete mit gefundenem RowKey
  - `UpdateSessionStatusAsync`, `SetSessionPreProvisionedAsync`, etc.

- [ ] **4. `GetSessionsAsync` — effiziente Pagination**
  ```csharp
  public async Task<SessionPage> GetSessionsAsync(
      string tenantId, int maxResults = 100, string? cursorRowKey = null)
  ```
  - Kein `since`-Parameter mehr nötig — RowKey-Reihenfolge reicht
  - `cursorRowKey` → `RowKey gt '{cursor}'` Filter
  - `maxPerPage: maxResults + 1` → wenn wir maxResults+1 bekommen, gibt's noch mehr
  - Return: `SessionPage { Sessions, HasMore }`

- [ ] **5. `GetAllSessionsAsync` — gleiche Pagination**
  - Wie oben, aber ohne PartitionKey-Filter
  - Hier weiterhin In-Memory-Sort nach StartedAt (cross-partition)

- [ ] **6. `SessionPage` Model in ApiModels.cs**
  ```csharp
  public class SessionPage {
      public List<SessionSummary> Sessions { get; set; }
      public bool HasMore { get; set; }
  }
  ```

### Phase 2: API-Funktionen

- [ ] **7. `GetSessionsFunction` — `cursor` Query-Parameter**
  - Optional `cursor` (= letzter RowKey) aus Query-String
  - Response: `{ success, count, hasMore, sessions }`

- [ ] **8. `GetAllSessionsFunction` — gleich**

### Phase 3: Frontend

- [ ] **9. `fetchSessions` mit Load-More-Logik**
  - State: `hasMore`, `loadingMore`, `cursor`
  - Initial: fetch ohne cursor → replace sessions
  - Load More: fetch mit cursor → append sessions

- [ ] **10. "Load More" Button in SessionTable**
  - Am Ende der Tabelle, wenn `hasMore` true ist
  - Loading-Spinner während Laden

### Phase 4: Migration

- [ ] **11. Migrations-Funktion schreiben**
  - Liest alle Sessions mit altem RowKey-Format (keine `_` im RowKey = alte Daten)
  - Schreibt neu mit `{invertedTicks}_{SessionId}` RowKey + `SessionId` Property
  - Löscht alten Eintrag
  - Kann als einmalige Azure Function oder Script laufen
  - Idempotent (kann mehrfach laufen)

---

## Dateien die geändert werden

### Backend
1. `src/Shared/AutopilotMonitor.Shared/Models/ApiModels.cs` — SessionPage model
2. `src/Backend/.../Services/TableStorageService.Sessions.cs` — RowKey-Logik, alle CRUD-Methoden
3. `src/Backend/.../Functions/Sessions/GetSessionsFunction.cs` — cursor param
4. `src/Backend/.../Functions/Sessions/GetAllSessionsFunction.cs` — cursor param

### Frontend
5. `src/Web/.../app/dashboard/page.tsx` — Load More state + handler
6. `src/Web/.../app/dashboard/components/SessionTable.tsx` — Load More UI

### Migration
7. Neues Script/Function für Datenmigration
