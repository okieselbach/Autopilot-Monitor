/**
 * Integration tests for MCP raw data tools (tables, raw sessions/events, backend logs).
 * Tests against the live backend — requires AUTOPILOT_API_TOKEN env var.
 */
import { describe, it, expect, beforeAll } from 'vitest';
import { apiFetch, getToken } from './helpers.js';

let knownTenantId: string;

// Integration suite: only runs when a backend token is supplied. Without one it
// SKIPS (not errors), so an unattended CI run still goes green on the unit
// suites instead of failing the whole file. (review finding tests-7)
const RUN_INTEGRATION = !!process.env.AUTOPILOT_API_TOKEN;
const suite = describe.skipIf(!RUN_INTEGRATION);

beforeAll(async () => {
  if (!RUN_INTEGRATION) return;
  getToken();
  // Discover a tenant with data
  const data = await apiFetch<any>(`/api/global/search/sessions?limit=1`);
  knownTenantId = data.sessions[0]?.tenantId ?? '';
});

suite('list_tables', () => {
  it('should return list of Azure Table Storage tables', async () => {
    const data = await apiFetch<any>(`/api/global/raw/tables`);
    expect(data).toHaveProperty('tables');
    expect(Array.isArray(data.tables)).toBe(true);
    expect(data.tables.length).toBeGreaterThan(10);
    expect(data.tables).toContain('Sessions');
    expect(data.tables).toContain('Events');
    expect(data.tables).toContain('TenantConfiguration');
  });
});

suite('query_table', () => {
  it('should query TenantConfiguration table', async () => {
    const data = await apiFetch<any>(`/api/global/raw/tables/TenantConfiguration?limit=2`);
    expect(data).toHaveProperty('entities');
    expect(Array.isArray(data.entities)).toBe(true);
    if (data.entities.length > 0) {
      expect(data.entities[0]).toHaveProperty('PartitionKey');
      expect(data.entities[0]).toHaveProperty('RowKey');
    }
  });

  it('should return error for non-existent table', async () => {
    const data = await apiFetch<any>(
      `/api/global/raw/tables/ZZZ_NonExistent_Table`,
      { expectStatus: 404 },
    );
    // Should be a structured error
    expect(data).toBeDefined();
  });

  it('should filter by partitionKey', async () => {
    if (!knownTenantId) return;
    const data = await apiFetch<any>(
      `/api/global/raw/tables/Sessions?partitionKey=${knownTenantId}&limit=2`,
    );
    expect(data).toHaveProperty('entities');
    for (const entity of data.entities) {
      expect(entity.PartitionKey).toBe(knownTenantId);
    }
  });
});

suite('query_raw_sessions', () => {
  // Raw = literal SessionsIndex rows: PascalCase columns incl. PartitionKey/RowKey/Timestamp.
  it('should return raw rows with literal stored columns (cross-tenant)', async () => {
    const data = await apiFetch<any>(`/api/global/raw/sessions?pageSize=3`);
    expect(data).toHaveProperty('sessions');
    expect(Array.isArray(data.sessions)).toBe(true);
    expect(data.sessions.length).toBeLessThanOrEqual(3);
    if (data.sessions.length > 0) {
      const s = data.sessions[0];
      // Literal table shape — identity + status as stored.
      expect(s).toHaveProperty('PartitionKey');
      expect(s).toHaveProperty('RowKey');
      expect(s).toHaveProperty('Status');
    }
  });

  it('should filter by status (PascalCase Status column)', async () => {
    const data = await apiFetch<any>(`/api/global/raw/sessions?status=Succeeded&pageSize=2`);
    for (const s of data.sessions) {
      expect(s.Status).toBe('Succeeded');
    }
  });

  it('should expose columns the old whitelist dropped (e.g. OsEdition / ImeAgentVersion)', async () => {
    // The raw row carries every stored column; at least one of these should be present on real data.
    const data = await apiFetch<any>(`/api/global/raw/sessions?status=Succeeded&pageSize=5`);
    if (data.sessions.length === 0) return;
    const anyHasFormerlyDropped = data.sessions.some(
      (s: any) => 'OsEdition' in s || 'ImeAgentVersion' in s || 'FailureSource' in s || 'GeoCountry' in s,
    );
    expect(anyHasFormerlyDropped).toBe(true);
  });

  it('should support pass-through field projection (case-insensitive, keeps real columns)', async () => {
    const data = await apiFetch<any>(
      `/api/global/raw/sessions?fields=Status,OsEdition&pageSize=2`,
    );
    expect(data.sessions.length).toBeGreaterThan(0);
    const s = data.sessions[0];
    // Requested + always-kept identity columns present; non-requested narrowed away.
    expect(s).toHaveProperty('PartitionKey');
    expect(s).toHaveProperty('RowKey');
    expect(s).toHaveProperty('Status');
    expect(s).not.toHaveProperty('Manufacturer');
  });

  it('should accept the new device/hardware filters', async () => {
    // enrollmentType is a real SessionsIndex column — every returned row must match.
    const data = await apiFetch<any>(`/api/global/raw/sessions?enrollmentType=v2&pageSize=3`);
    for (const s of data.sessions) {
      expect(s.EnrollmentType).toBe('v2');
    }
  });
});

suite('query_raw_events', () => {
  it('should return literal raw event rows (unenriched)', async () => {
    if (!knownTenantId) return;
    const data = await apiFetch<any>(
      `/api/raw/events?tenantId=${knownTenantId}&eventType=enrollment_complete&pageSize=2`,
    );
    expect(data).toHaveProperty('events');
    expect(Array.isArray(data.events)).toBe(true);
    if (data.events.length > 0) {
      const e = data.events[0];
      // Raw shape: PascalCase columns, DataJson as a raw string (not parsed), Severity as an int.
      expect(e).toHaveProperty('EventType');
      expect(e).toHaveProperty('RowKey');
      if ('DataJson' in e && e.DataJson != null) expect(typeof e.DataJson).toBe('string');
      if ('Severity' in e && e.Severity != null) expect(typeof e.Severity).toBe('number');
    }
  });
});

suite('query_backend_logs', () => {
  it('should execute KQL query (POST)', async () => {
    const data = await apiFetch<any>(`/api/global/raw/logs`, {
      method: 'POST',
      body: JSON.stringify({
        query: 'traces | take 1',
        timespan: 'PT1H',
      }),
    });
    expect(data).toHaveProperty('tables');
    expect(Array.isArray(data.tables)).toBe(true);
    expect(data.tables[0]).toHaveProperty('columns');
    expect(data.tables[0]).toHaveProperty('rows');
  });
});

suite('read-only verification', () => {
  it('should reject PUT on session endpoint', async () => {
    // Verify the API doesn't accept mutation methods on read endpoints
    try {
      await apiFetch(`/api/sessions/00000000-0000-0000-0000-000000000001`, {
        method: 'PUT',
        body: JSON.stringify({ status: 'Failed' }),
      });
      // If it doesn't throw, the endpoint exists for PUT — that would be unexpected
      expect.fail('PUT should not succeed on a read endpoint');
    } catch (e: any) {
      // Expected: 404 (no route) or 405 (method not allowed) or similar
      expect(e.message).toMatch(/API (404|405|400)/);
    }
  });

  it('should reject DELETE on session events endpoint', async () => {
    try {
      await apiFetch(`/api/sessions/00000000-0000-0000-0000-000000000001/events`, {
        method: 'DELETE',
      });
      expect.fail('DELETE should not succeed on a read endpoint');
    } catch (e: any) {
      expect(e.message).toMatch(/API (404|405|400)/);
    }
  });
});
