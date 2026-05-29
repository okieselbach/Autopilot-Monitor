/**
 * Unit tests for the auto-exhaust forward-scan (`scanUntilMatch`) that fixes the
 * "count:0 + nextLink ≠ no results" confusion, and for the catalog validators that
 * make an invalid filter a clear error instead of a silent empty result.
 *
 * Deterministic and backend-free: scanUntilMatch takes an injectable page fetcher
 * and clock, so canned pages drive it instead of the live API.
 */
import { describe, it, expect } from 'vitest';
import { scanUntilMatch } from '../client.js';
import {
  isKnownEventType,
  assertKnownEventType,
  assertKnownDevicePropertyKeys,
  eventTypePrefixOf,
  ALL_DEVICE_PROPERTY_KEYS,
} from '../resource-catalog.js';

type Page = Record<string, unknown>;

/** Build a fake page fetcher from a path -> page map; throws for unknown paths. */
function fakeFetcher(pages: Record<string, Page>): (path: string) => Promise<Page> {
  return async (path: string) => {
    if (!(path in pages)) throw new Error(`unexpected path: ${path}`);
    return pages[path];
  };
}

/** Monotonic fake clock: each call advances by `stepMs`. */
function fakeClock(startMs = 0, stepMs = 0): () => number {
  let t = startMs;
  return () => {
    const now = t;
    t += stepMs;
    return now;
  };
}

const BASE = '/api/raw/events';
const ROOMY = { maxPages: 50, wallClockMs: 60_000 };

describe('scanUntilMatch', () => {
  it('returns a first non-empty page verbatim with scannedPages=1 and no moreToScan', async () => {
    const first = `${BASE}?eventType=app_install_failed&pageSize=200`;
    const pages: Record<string, Page> = {
      [first]: { success: true, count: 2, events: [{ id: 'a' }, { id: 'b' }], nextLink: `${BASE}?continuation=tok2` },
    };

    const res = await scanUntilMatch(first, BASE, ROOMY, fakeFetcher(pages), fakeClock());

    expect(res.events).toHaveLength(2);
    expect(res.scannedPages).toBe(1);
    expect(res.moreToScan).toBeUndefined();
    expect(res.nextLink).toBe(`${BASE}?continuation=tok2`); // still signals "more pages"
  });

  it('scans past empty pages until it finds matches, then returns that page', async () => {
    const first = `${BASE}?eventType=x&pageSize=200`;
    const pages: Record<string, Page> = {
      [first]: { success: true, count: 0, events: [], nextLink: `${BASE}?continuation=tok2` },
      [`${BASE}?continuation=tok2`]: { success: true, count: 0, events: [], nextLink: `${BASE}?continuation=tok3` },
      [`${BASE}?continuation=tok3`]: { success: true, count: 1, events: [{ id: 'hit' }], nextLink: `${BASE}?continuation=tok4` },
    };

    const res = await scanUntilMatch(first, BASE, ROOMY, fakeFetcher(pages), fakeClock());

    expect(res.events).toEqual([{ id: 'hit' }]);
    expect(res.scannedPages).toBe(3);
    expect(res.moreToScan).toBeUndefined();
    expect(res.nextLink).toBe(`${BASE}?continuation=tok4`);
  });

  it('drains to a truly-empty result: count:0, NO nextLink, no moreToScan', async () => {
    const first = `${BASE}?eventType=x&pageSize=200`;
    const pages: Record<string, Page> = {
      [first]: { success: true, count: 0, events: [], nextLink: `${BASE}?continuation=tok2` },
      [`${BASE}?continuation=tok2`]: { success: true, count: 0, events: [] }, // no nextLink → drained
    };

    const res = await scanUntilMatch(first, BASE, ROOMY, fakeFetcher(pages), fakeClock());

    expect(res.events).toEqual([]);
    expect(res.count).toBe(0);
    expect(res.nextLink).toBeUndefined();
    expect(res.moreToScan).toBeUndefined();
    expect(res.scannedPages).toBe(2);
  });

  it('stops at the page budget with moreToScan + recallNote, keeping nextLink', async () => {
    // Every page is empty-but-continuable; the page budget (3) trips before any match.
    const first = `${BASE}?eventType=x&pageSize=200`;
    const link = (n: number) => `${BASE}?continuation=tok${n}`;
    const pages: Record<string, Page> = {
      [first]: { success: true, count: 0, events: [], nextLink: link(2) },
      [link(2)]: { success: true, count: 0, events: [], nextLink: link(3) },
      [link(3)]: { success: true, count: 0, events: [], nextLink: link(4) },
    };

    const res = await scanUntilMatch(first, BASE, { maxPages: 3, wallClockMs: 60_000 }, fakeFetcher(pages), fakeClock());

    expect(res.scannedPages).toBe(3);
    expect(res.moreToScan).toBe(true);
    expect(typeof res.recallNote).toBe('string');
    expect(res.nextLink).toBe(link(4)); // preserved so the caller can resume
  });

  it('stops at the wall-clock budget with moreToScan', async () => {
    const first = `${BASE}?eventType=x&pageSize=200`;
    const pages: Record<string, Page> = {
      [first]: { success: true, count: 0, events: [], nextLink: `${BASE}?continuation=tok2` },
    };

    // deadline = first now() (0) + 10 = 10; the in-loop now() returns 100 > 10 → trip.
    const res = await scanUntilMatch(first, BASE, { maxPages: 100, wallClockMs: 10 }, fakeFetcher(pages), fakeClock(0, 100));

    expect(res.scannedPages).toBe(1);
    expect(res.moreToScan).toBe(true);
  });

  it('passes a non-list envelope through unchanged (plus scannedPages)', async () => {
    const first = '/api/something';
    const pages: Record<string, Page> = {
      [first]: { success: true, value: 42 }, // no recognised item array
    };

    const res = await scanUntilMatch(first, '/api/something', ROOMY, fakeFetcher(pages), fakeClock());

    expect(res.value).toBe(42);
    expect(res.scannedPages).toBe(1);
    expect(res.moreToScan).toBeUndefined();
  });

  it('detects the sessions[] array shape too', async () => {
    const first = '/api/search/sessions?prop.tpm_status.manufacturerName=IFX&pageSize=20';
    const pages: Record<string, Page> = {
      [first]: { success: true, count: 0, sessions: [], nextLink: '/api/search/sessions?continuation=tokB' },
      ['/api/search/sessions?continuation=tokB']: { success: true, count: 1, sessions: [{ sessionId: 's1' }] },
    };

    const res = await scanUntilMatch(first, '/api/search/sessions', ROOMY, fakeFetcher(pages), fakeClock());

    expect(res.sessions).toEqual([{ sessionId: 's1' }]);
    expect(res.scannedPages).toBe(2);
  });
});

describe('event-type validation', () => {
  it('accepts known event types', () => {
    expect(isKnownEventType('app_install_failed')).toBe(true);
    expect(isKnownEventType('enrollment_failed')).toBe(true);
    expect(() => assertKnownEventType('app_install_failed')).not.toThrow();
  });

  it('rejects an unknown event type with a helpful, suggestion-bearing error', () => {
    expect(isKnownEventType('made_up_type')).toBe(false);
    expect(() => assertKnownEventType('app_install_fialed')).toThrow(/event_types catalog/);
    // "app_install_fialed" shares the "app" token → at least one suggestion surfaces.
    expect(() => assertKnownEventType('app_install_fialed')).toThrow(/Did you mean/);
  });
});

describe('deviceProperties key validation', () => {
  it('extracts the event-type prefix', () => {
    expect(eventTypePrefixOf('tpm_status.specVersion')).toBe('tpm_status');
    expect(eventTypePrefixOf('no_dot_key')).toBe('no_dot_key');
  });

  it('accepts keys whose prefix is a known event type — even uncatalogued full keys', () => {
    expect(() => assertKnownDevicePropertyKeys(['tpm_status.manufacturerName'])).not.toThrow();
    // hardware_spec is a known event type; a not-yet-catalogued sub-property must NOT be blocked.
    expect(() => assertKnownDevicePropertyKeys(['hardware_spec.someFutureField'])).not.toThrow();
  });

  it('rejects a key whose prefix is not a known event type (typo)', () => {
    expect(() => assertKnownDevicePropertyKeys(['tmp_status.specVersion'])).toThrow(/Unknown deviceProperties key prefix/);
  });

  it('catalog key list is curated and excludes the _usage doc block', () => {
    expect(ALL_DEVICE_PROPERTY_KEYS).toContain('tpm_status.manufacturerName');
    expect(ALL_DEVICE_PROPERTY_KEYS.some((k) => k.startsWith('_usage'))).toBe(false);
  });
});
