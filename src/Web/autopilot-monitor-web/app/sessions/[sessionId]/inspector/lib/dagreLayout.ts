import dagre from "dagre";
import type { Edge, Node } from "@xyflow/react";
import type { DecisionGraphNode, DecisionGraphEdge } from "../types";

/** Logical category of a graph node — drives renderer + colour. */
export type NodeCategory = "stage" | "terminal-success" | "terminal-failed" | "terminal-paused";

export interface InspectorNodeData {
  label: string;
  stageId: string;
  category: NodeCategory;
  visitCount: number;
  terminalOutcome: string | null;
  [key: string]: unknown;
}

export interface InspectorEdgeData {
  stepIndex: number;
  trigger: string;
  taken: boolean;
  deadEndReason: string | null;
  classifierVerdictId: string | null;
  classifierHypothesisLevel: string | null;
  signalOrdinalRef: number;
  occurredAtUtc: string;
  [key: string]: unknown;
}

const NODE_WIDTH = 180;
const NODE_HEIGHT = 60;

/**
 * Default Dagre config for the Inspector. `TB` = top→bottom, matches the
 * Endkunden EventTimeline reading direction (Plan §M6).
 */
export const DAGRE_CONFIG = {
  rankdir: "TB" as const,
  ranksep: 60,
  nodesep: 40,
  marginx: 20,
  marginy: 20,
};

/**
 * Pure function — translates the backend's DecisionGraphProjection into
 * positioned ReactFlow nodes + edges. Kept side-effect free so it can be
 * unit-tested without a DOM.
 */
export function buildLayout(
  nodes: DecisionGraphNode[],
  edges: DecisionGraphEdge[],
): { nodes: Node<InspectorNodeData>[]; edges: Edge<InspectorEdgeData>[] } {
  const g = new dagre.graphlib.Graph();
  g.setGraph(DAGRE_CONFIG);
  g.setDefaultEdgeLabel(() => ({}));

  for (const n of nodes) {
    g.setNode(n.id, { width: NODE_WIDTH, height: NODE_HEIGHT });
  }

  // Dagre is fed *all* edges (taken + dead-end) so the layout reserves room
  // for the dead-end branches. The renderer separates them visually later.
  for (const e of edges) {
    g.setEdge(e.fromStage, e.toStage, { weight: e.taken ? 2 : 1 });
  }

  dagre.layout(g);

  const layoutNodes: Node<InspectorNodeData>[] = nodes.map((n) => {
    const pos = g.node(n.id);
    return {
      id: n.id,
      type: "inspectorNode",
      position: pos
        ? { x: pos.x - NODE_WIDTH / 2, y: pos.y - NODE_HEIGHT / 2 }
        : { x: 0, y: 0 },
      data: {
        label: n.id,
        stageId: n.id,
        category: classifyNode(n),
        visitCount: n.visitCount,
        terminalOutcome: n.terminalOutcome,
      },
    };
  });

  const layoutEdges: Edge<InspectorEdgeData>[] = edges.map((e) => ({
    // StepIndex is monotonic per session, so it's a stable unique id even when
    // multiple edges share (from, to) pairs (e.g. a guard rejected the same
    // transition twice before another signal unblocked it).
    id: `e${e.stepIndex}`,
    source: e.fromStage,
    target: e.toStage,
    type: "inspectorEdge",
    animated: false,
    data: {
      stepIndex: e.stepIndex,
      trigger: e.trigger,
      taken: e.taken,
      deadEndReason: e.deadEndReason,
      classifierVerdictId: e.classifierVerdictId,
      classifierHypothesisLevel: e.classifierHypothesisLevel,
      signalOrdinalRef: e.signalOrdinalRef,
      occurredAtUtc: e.occurredAtUtc,
    },
  }));

  return { nodes: layoutNodes, edges: layoutEdges };
}

function classifyNode(n: DecisionGraphNode): NodeCategory {
  if (!n.isTerminal) return "stage";
  switch (n.terminalOutcome) {
    case "Succeeded":
      return "terminal-success";
    case "Failed":
      return "terminal-failed";
    case "PausedForPart2":
      return "terminal-paused";
    default:
      return "stage";
  }
}
