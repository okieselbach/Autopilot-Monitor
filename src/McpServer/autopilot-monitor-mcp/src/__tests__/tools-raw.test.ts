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
  it('should return raw sessions (cross-tenant)', async () => {
    const data = await apiFetch<any>(`/api/global/raw/sessions?limit=3`);
    expect(data).toHaveProperty('sessions');
    expect(Array.isArray(data.sessions)).toBe(true);
    expect(data.sessions.length).toBeLessThanOrEqual(3);
  });

  it('should filter by status', async () => {
    const data = await apiFetch<any>(`/api/global/raw/sessions?status=Succeeded&limit=2`);
    for (const s of data.sessions) {
      expect(s.status).toBe('Succeeded');
    }
  });

  it('should support field projection', async () => {
    const data = await apiFetch<any>(
      `/api/global/raw/sessions?fields=sessionId,status&limit=2`,
    );
    expect(data.sessions.length).toBeGreaterThan(0);
    const s = data.sessions[0];
    expect(s).toHaveProperty('sessionId');
    expect(s).toHaveProperty('status');
  });
});

suite('query_raw_events', () => {
  it('should return events for a tenant', async () => {
    if (!knownTenantId) return;
    const data = await apiFetch<any>(
      `/api/raw/events?tenantId=${knownTenantId}&eventType=enrollment_complete&limit=2`,
    );
    expect(data).toHaveProperty('events');
    expect(Array.isArray(data.events)).toBe(true);
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
