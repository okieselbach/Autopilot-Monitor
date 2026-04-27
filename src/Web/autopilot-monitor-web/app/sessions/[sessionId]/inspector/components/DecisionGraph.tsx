"use client";

import { useMemo, useState } from "react";
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  type EdgeMouseHandler,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";

import type { DecisionGraphProjection } from "../types";
import { buildLayout } from "../lib/dagreLayout";
import { InspectorNode } from "./InspectorNode";
import { InspectorEdge } from "./InspectorEdge";

interface DecisionGraphProps {
  graph: DecisionGraphProjection;
  truncated: boolean;
}

const NODE_TYPES = { inspectorNode: InspectorNode } as const;
const EDGE_TYPES = { inspectorEdge: InspectorEdge } as const;

export function DecisionGraph({ graph, truncated }: DecisionGraphProps) {
  const [showDeadEnds, setShowDeadEnds] = useState(true);
  const [selectedEdgeId, setSelectedEdgeId] = useState<string | null>(null);

  // Pre-filter edges before layout — Dagre then ignores dead-end branches
  // entirely, which keeps the taken-only view tight + readable.
  const filteredEdges = useMemo(
    () => (showDeadEnds ? graph.edges : graph.edges.filter((e) => e.taken)),
    [graph.edges, showDeadEnds],
  );

  const layout = useMemo(
    () => buildLayout(graph.nodes, filteredEdges),
    [graph.nodes, filteredEdges],
  );

  // Highlight selected edge.
  const styledEdges = useMemo(
    () =>
      layout.edges.map((e) => ({
        ...e,
        selected: e.id === selectedEdgeId,
      })),
    [layout.edges, selectedEdgeId],
  );

  const onEdgeClick: EdgeMouseHandler = (_evt, edge) => {
    setSelectedEdgeId(edge.id);
  };

  const selectedEdge = useMemo(
    () => layout.edges.find((e) => e.id === selectedEdgeId) ?? null,
    [layout.edges, selectedEdgeId],
  );

  const stats = useMemo(() => {
    const total = graph.edges.length;
    const taken = graph.edges.filter((e) => e.taken).length;
    return { total, taken, deadEnds: total - taken };
  }, [graph.edges]);

  return (
    <div className="grid grid-cols-1 gap-4 lg:grid-cols-[1fr_360px]">
      <div className="rounded border border-gray-200 bg-white">
        <div className="flex flex-wrap items-center gap-3 border-b border-gray-200 p-3 text-sm">
          <span className="text-gray-600">
            {graph.nodes.length} stages · {stats.taken} taken · {stats.deadEnds} dead-ends
            {truncated && <span className="ml-2 text-amber-600">(truncated)</span>}
          </span>
          <label className="ml-auto flex items-center gap-1.5 text-xs">
            <input
              type="checkbox"
              checked={showDeadEnds}
              onChange={(e) => setShowDeadEnds(e.target.checked)}
            />
            Show dead-ends
          </label>
        </div>

        <div style={{ height: "70vh" }}>
          <ReactFlow
            nodes={layout.nodes}
            edges={styledEdges}
            nodeTypes={NODE_TYPES}
            edgeTypes={EDGE_TYPES}
            fitView
            fitViewOptions={{ padding: 0.15 }}
            proOptions={{ hideAttribution: true }}
            onEdgeClick={onEdgeClick}
            onPaneClick={() => setSelectedEdgeId(null)}
          >
            <Background gap={20} />
            <Controls />
            <MiniMap pannable zoomable nodeStrokeWidth={2} />
          </ReactFlow>
        </div>
      </div>

      <EdgeDetailPanel
        edge={
          selectedEdge && selectedEdge.data
            ? {
                stepIndex: selectedEdge.data.stepIndex,
                trigger: selectedEdge.data.trigger,
                taken: selectedEdge.data.taken,
                deadEndReason: selectedEdge.data.deadEndReason,
                classifierVerdictId: selectedEdge.data.classifierVerdictId,
                classifierHypothesisLevel: selectedEdge.data.classifierHypothesisLevel,
                signalOrdinalRef: selectedEdge.data.signalOrdinalRef,
                occurredAtUtc: selectedEdge.data.occurredAtUtc,
                from: selectedEdge.source,
                to: selectedEdge.target,
              }
            : null
        }
      />
    </div>
  );
}

interface EdgeDetail {
  stepIndex: number;
  trigger: string;
  taken: boolean;
  deadEndReason: string | null;
  classifierVerdictId: string | null;
  classifierHypothesisLevel: string | null;
  signalOrdinalRef: number;
  occurredAtUtc: string;
  from: string;
  to: string;
}

function EdgeDetailPanel({ edge }: { edge: EdgeDetail | null }) {
  if (!edge) {
    return (
      <div className="rounded border border-gray-200 bg-gray-50 p-4 text-sm text-gray-500">
        Click a transition (edge) in the graph to see its trigger, source signal, and any guard reason.
      </div>
    );
  }

  return (
    <div className="rounded border border-gray-200 bg-white p-4 text-sm space-y-2">
      <div className="flex items-baseline justify-between">
        <h3 className="font-semibold">Step {edge.stepIndex}</h3>
        <span
          className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${
            edge.taken ? "bg-blue-100 text-blue-800" : "bg-amber-100 text-amber-800"
          }`}
        >
          {edge.taken ? "TAKEN" : "DEAD-END"}
        </span>
      </div>
      <KV k="From" v={edge.from} />
      <KV k="To" v={edge.to} />
      <KV k="Trigger" v={edge.trigger} mono />
      <KV k="Time (UTC)" v={edge.occurredAtUtc} mono />
      <KV k="Signal ord ref" v={String(edge.signalOrdinalRef)} mono />
      {edge.deadEndReason && <KV k="Dead-end reason" v={edge.deadEndReason} mono />}
      {edge.classifierVerdictId && (
        <KV k="Classifier verdict" v={edge.classifierVerdictId} mono />
      )}
      {edge.classifierHypothesisLevel && (
        <KV k="Hypothesis level" v={edge.classifierHypothesisLevel} mono />
      )}
    </div>
  );
}

function KV({ k, v, mono = false }: { k: string; v: string; mono?: boolean }) {
  return (
    <div className="grid grid-cols-[110px_1fr] gap-2">
      <span className="text-xs text-gray-500">{k}</span>
      <span className={`text-xs ${mono ? "font-mono" : ""} break-all text-gray-800`}>{v}</span>
    </div>
  );
}
