/**
 * Unit tests for the metrics-tool helpers that fix three MCP findings:
 *  - inferGeoGroupBy / buildGeoLocationParams: the geographic drilldown was dead
 *    for country/region keys because groupBy was never forwarded on the
 *    raw-locationKey path (backend defaulted to "city" → 0 matches).
 *  - platformWindowEcho: get_platform_metrics silently capped at the newest 100
 *    sessions; the cap and a truncated flag are now surfaced.
 */
import { describe, it, expect } from 'vitest';
import { inferGeoGroupBy, buildGeoLocationParams, platformWindowEcho } from '../tools/admin.js';

describe('inferGeoGroupBy', () => {
  it('treats a single-segment key as country', () => {
    expect(inferGeoGroupBy('US')).toBe('country');
  });

  it('treats a two-segment key as region', () => {
    expect(inferGeoGroupBy('Saxony, DE')).toBe('region');
  });

  it('treats a three-segment key as city', () => {
    expect(inferGeoGroupBy('Falkenstein, Saxony, DE')).toBe('city');
  });
});

describe('buildGeoLocationParams', () => {
  it('forwards a raw locationKey with an inferred groupBy (country key resolves)', () => {
    expect(buildGeoLocationParams({ locationKey: 'US' })).toEqual({ locationKey: 'US', groupBy: 'country' });
  });

  it('forwards a region locationKey with groupBy=region', () => {
    expect(buildGeoLocationParams({ locationKey: 'Saxony, DE' })).toEqual({ locationKey: 'Saxony, DE', groupBy: 'region' });
  });

  it('locationKey wins over structured filters', () => {
    const out = buildGeoLocationParams({ locationKey: 'US', country: 'DE', region: 'Saxony' });
    expect(out).toEqual({ locationKey: 'US', groupBy: 'country' });
    expect(out.country).toBeUndefined();
  });

  it('forwards structured country-only filter (no locationKey reconstruction)', () => {
    expect(buildGeoLocationParams({ country: 'US' })).toEqual({ country: 'US', region: undefined, city: undefined });
  });

  it('forwards country + region + city verbatim', () => {
    expect(buildGeoLocationParams({ country: 'DE', region: 'Saxony', city: 'Falkenstein' }))
      .toEqual({ country: 'DE', region: 'Saxony', city: 'Falkenstein' });
  });

  it('returns no location params when nothing is provided', () => {
    expect(buildGeoLocationParams({})).toEqual({});
  });
});

describe('platformWindowEcho', () => {
  it('prefers the backend-clamped window + limit over the requested values', () => {
    const echo = platformWindowEcho({ windowDays: 365, sessionLimit: 2000 }, { days: 99999, limit: 99999 }, 2000);
    expect(echo.windowDays).toBe(365);
    expect(echo.sessionLimit).toBe(2000);
  });

  it('flags truncated when the analyzed count reaches the cap', () => {
    const echo = platformWindowEcho({ sessionLimit: 100 }, { days: 30, limit: 100 }, 100);
    expect(echo.truncated).toBe(true);
  });

  it('does not flag truncated when fewer sessions than the cap were analyzed', () => {
    const echo = platformWindowEcho({ sessionLimit: 100 }, { days: 30, limit: 100 }, 42);
    expect(echo.truncated).toBe(false);
  });

  it('falls back to the requested values when the backend omits its echo', () => {
    const echo = platformWindowEcho({}, { days: 7, limit: 250 }, 0);
    expect(echo).toEqual({ windowDays: 7, sessionLimit: 250, truncated: false });
  });
});
