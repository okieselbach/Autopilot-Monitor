/**
 * Unit tests for the embedding unit-norm assertion. cosineSimilarity in
 * vector-search-provider is a bare dot product that assumes L2-unit-normalized inputs,
 * so embed()/index() assert the model actually returned a unit vector. These tests drive
 * assertUnitNorm directly with canned vectors — no ML model load required.
 */
import { describe, it, expect } from 'vitest';
import { assertUnitNorm } from '../vector-search-provider.js';

/** Build an L2-unit vector of the given dimension (all components equal). */
function unitVector(dim: number): number[] {
  const v = 1 / Math.sqrt(dim);
  return new Array(dim).fill(v);
}

describe('assertUnitNorm', () => {
  it('accepts an exactly unit-normalized vector', () => {
    expect(() => assertUnitNorm(unitVector(384), 'test')).not.toThrow();
  });

  it('accepts a simple axis-aligned unit vector', () => {
    expect(() => assertUnitNorm([1, 0, 0, 0], 'test')).not.toThrow();
  });

  it('tolerates float32 rounding just inside epsilon', () => {
    // norm ~= 1 + 5e-4, within the 1e-3 tolerance.
    const v = unitVector(384).map((x) => x * (1 + 5e-4));
    expect(() => assertUnitNorm(v, 'test')).not.toThrow();
  });

  it('throws on a non-normalized (un-scaled) vector', () => {
    // A raw [1,1,1,1] has norm 2 — the exact failure mode a dropped `normalize: true` produces.
    expect(() => assertUnitNorm([1, 1, 1, 1], 'test')).toThrow(/not L2-unit-normalized/);
  });

  it('throws on a vector whose norm drifts beyond epsilon', () => {
    const v = unitVector(384).map((x) => x * 1.01); // norm ~= 1.01, outside 1e-3
    expect(() => assertUnitNorm(v, 'test')).toThrow(/not L2-unit-normalized/);
  });

  it('throws on a zero vector', () => {
    expect(() => assertUnitNorm([0, 0, 0, 0], 'test')).toThrow(/not L2-unit-normalized/);
  });

  it('names the context in the error to aid diagnosis', () => {
    expect(() => assertUnitNorm([5, 0], 'index:doc-42')).toThrow(/index:doc-42/);
  });
});
