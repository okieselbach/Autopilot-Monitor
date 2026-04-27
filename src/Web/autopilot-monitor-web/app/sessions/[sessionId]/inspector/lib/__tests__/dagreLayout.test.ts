import { describe, expect, it } from "vitest";
import { buildLayout } from "../dagreLayout";
import type { DecisionGraphNode, DecisionGraphEdge } from "../../types";

const node = (id: string, isTerminal = false, terminalOutcome: string | null = null): DecisionGraphNode => ({
  id,
  isTerminal,
  terminalOutcome,
  visitCount: 0,
});

const edge = (
  stepIndex: number,
  fromStage: string,
  toStage: string,
  taken: boolean,
  deadEndReason: string | null = null,
): DecisionGraphEdge => ({
  stepIndex,
  fromStage,
  toStage,
  trigger: `trigger_${stepIndex}`,
  taken,
  deadEndReason,
  signalOrdinalRef: stepIndex,
  occurredAtUtc: "2026-04-27T00:00:00.000Z",
  classifierVerdictId: null,
  classifierHypothesisLevel: null,
});

describe("buildLayout", () => {
  it("returns empty layout for empty input", () => {
    const result = buildLayout([], []);
    expect(result.nodes).toEqual([]);
    expect(result.edges).toEqual([]);
  });

  it("classifies a non-terminal node as 'stage'", () => {
    const result = buildLayout([node("Started")], []);
    expect(result.nodes[0].data.category).toBe("stage");
  });

  it("classifies terminal nodes by outcome", () => {
    const result = buildLayout(
      [
        node("Completed", true, "Succeeded"),
        node("Failed", true, "Failed"),
        node("WhiteGloveSealed", true, "PausedForPart2"),
      ],
      [],
    );
    const byId = Object.fromEntries(result.nodes.map((n) => [n.id, n.data.category]));
    expect(byId["Completed"]).toBe("terminal-success");
    expect(byId["Failed"]).toBe("terminal-failed");
    expect(byId["WhiteGloveSealed"]).toBe("terminal-paused");
  });

  it("falls back to 'stage' for terminal node with unknown outcome", () => {
    const result = buildLayout([node("MysteryEnd", true, "ZorkOutcome")], []);
    expect(result.nodes[0].data.category).toBe("stage");
  });

  it("assigns positive y-coordinates with TB layout (top→bottom)", () => {
    const nodes = [node("A"), node("B")];
    const edges = [edge(1, "A", "B", true)];
    const result = buildLayout(nodes, edges);
    const positions = Object.fromEntries(result.nodes.map((n) => [n.id, n.position]));
    // A is the source; in TB layout A.y < B.y.
    expect(positions["A"].y).toBeLessThan(positions["B"].y);
  });

  it("preserves StepIndex as the edge id (so duplicate from→to pairs stay distinct)", () => {
    const nodes = [node("A"), node("B")];
    const edges = [edge(1, "A", "B", false, "blocked"), edge(2, "A", "B", true)];
    const result = buildLayout(nodes, edges);
    const ids = result.edges.map((e) => e.id).sort();
    expect(ids).toEqual(["e1", "e2"]);
  });

  it("propagates dead-end metadata onto the rendered edge", () => {
    const result = buildLayout(
      [node("A"), node("B")],
      [edge(1, "A", "B", false, "guard:esp_gate_blocking")],
    );
    expect(result.edges[0].data?.taken).toBe(false);
    expect(result.edges[0].data?.deadEndReason).toBe("guard:esp_gate_blocking");
  });

  it("propagates trigger + classifier verdict on a taken edge", () => {
    const e = edge(7, "A", "B", true);
    e.classifierVerdictId = "WhiteGloveSealing";
    e.classifierHypothesisLevel = "Confirmed";
    const result = buildLayout([node("A"), node("B")], [e]);
    expect(result.edges[0].data?.trigger).toBe("trigger_7");
    expect(result.edges[0].data?.classifierVerdictId).toBe("WhiteGloveSealing");
    expect(result.edges[0].data?.classifierHypothesisLevel).toBe("Confirmed");
  });

  it("renders both taken and dead-end branches when given a fork", () => {
    // Reproduces a 2-stage WhiteGlove pattern: from EspInProgress, the agent
    // attempts WhiteGloveSealed (taken) but a guard initially rejects it
    // (dead-end). Both must appear in the layout output.
    const nodes = [node("EspInProgress"), node("WhiteGloveSealed", true, "PausedForPart2")];
    const edges = [
      edge(10, "EspInProgress", "WhiteGloveSealed", false, "guard:hello_pending"),
      edge(11, "EspInProgress", "WhiteGloveSealed", true),
    ];
    const result = buildLayout(nodes, edges);
    expect(result.edges).toHaveLength(2);
    expect(result.edges.some((e) => e.data?.taken === false)).toBe(true);
    expect(result.edges.some((e) => e.data?.taken === true)).toBe(true);
  });
});
