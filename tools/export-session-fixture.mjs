#!/usr/bin/env node
// =========================================================================
// export-session-fixture.mjs
//
// Plan: plans/REFACTOR_AGENT_V2.md §4 M2.
//
// Exports a Prod session's event stream (via MCP `get_session_events`) into a
// DecisionSignal-shaped JSONL fixture. During the v11 transition period each
// signal is emitted with `Evidence.Kind = "Derived"` and
// `Evidence.Identifier = "legacy-export-v1"` because the Legacy event stream is
// not a true raw signal stream — the export is a derivation. Once a real V2
// Agent runs and writes native SignalLog JSONL, fixtures can be captured with
// `Evidence.Kind = "Raw"` and this tool's role shrinks to anonymization only.
//
// M2 scope: scaffolding + schema validation. The MCP call is a placeholder —
// wire it to the real MCP client in M3 when the first fixture-driven reducer
// test needs Prod input.
//
// Usage:
//   node tools/export-session-fixture.mjs --help
//   node tools/export-session-fixture.mjs --dry-run --session-id=<id> --out=tests/fixtures/enrollment-sessions-local/<name>.jsonl
//   node tools/export-session-fixture.mjs --validate=<path-to-existing.jsonl>
// =========================================================================

import { argv, exit } from "node:process";
import { writeFileSync, readFileSync } from "node:fs";
import { dirname } from "node:path";
import { mkdirSync, existsSync } from "node:fs";

const args = parseArgs(argv.slice(2));

if (args.help) {
    printUsage();
    exit(0);
}

if (args.validate) {
    exit(validateJsonl(args.validate) ? 0 : 2);
}

if (!args.sessionId) {
    console.error("ERROR: --session-id=<id> is required. See --help.");
    exit(1);
}

if (args.dryRun) {
    // Produce a tiny synthetic fixture so downstream tooling can be wired up
    // without a live MCP connection. Each signal has the full required shape
    // per DecisionCore.Signals.DecisionSignal + Evidence.
    const fixture = buildDryRunFixture(args.sessionId);
    const outPath = args.out ?? `tests/fixtures/enrollment-sessions-local/${args.sessionId}.dryrun.jsonl`;
    writeJsonl(outPath, fixture);
    console.log(`Wrote ${fixture.length} dry-run signals to ${outPath}`);
    console.log("Next: fill in MCP client wiring for real session export (M3 bring-up).");
    exit(0);
}

// TODO(v2-m3): wire up real MCP `get_session_events` call here.
// The MCP client integration lives in src/McpServer/autopilot-monitor-mcp/;
// this script will import the client, authenticate with the admin credentials
// in the local environment, fetch the full event stream for the session, and
// convert each row via mapLegacyEventToDecisionSignal() below.
console.error(
    "ERROR: Real MCP export is not wired up yet (plan §4 M3). " +
    "Use --dry-run for the scaffolding path, or --validate to check an existing fixture."
);
exit(3);

// --------------------------------------------------------------------- helpers

function parseArgs(argv) {
    const out = { dryRun: false, help: false };
    for (const arg of argv) {
        if (arg === "--help" || arg === "-h") { out.help = true; continue; }
        if (arg === "--dry-run") { out.dryRun = true; continue; }
        const match = arg.match(/^--([\w-]+)(?:=(.*))?$/);
        if (match) {
            const [, key, value] = match;
            const k = key.replace(/-([a-z])/g, (_, c) => c.toUpperCase());
            out[k] = value ?? true;
        }
    }
    return out;
}

function printUsage() {
    console.log(
        `
export-session-fixture.mjs — export a Prod session to DecisionSignal JSONL

Arguments:
    --session-id=<id>    Required for export. Legacy session ID as seen in the Sessions table.
    --out=<path>         Output path. Default: tests/fixtures/enrollment-sessions-local/<id>.jsonl.
    --dry-run            Produce a small synthetic fixture (no MCP call). For tooling scaffolding.
    --validate=<path>    Validate an existing JSONL file's DecisionSignal shape + Pflicht-Evidence. No output.
    --help | -h          Print this text.

Evidence policy (plan §2.2 / §4 M2):
    During the Legacy->V2 transition, exported signals are emitted as Evidence.Kind="Derived"
    with Identifier="legacy-export-v1" and DerivationInputs populated from the source event row.
    This preserves replay determinism at the signal-log level while the raw event row gets
    discarded after ingest. Raw-Evidence exports become available once V2 Agents run on the
    session (they write native SignalLog JSONL directly).

Output:
    Each line = one JSON-serialized DecisionSignal record. Comment lines starting with '#'
    and blank lines are allowed and ignored by the Replay Harness.
`
    );
}

function buildDryRunFixture(sessionId) {
    // Minimal three-signal synthetic session: start, one phase change, recover.
    // Reducer handlers are not implemented yet (M3); the harness parses and counts.
    const t0 = new Date("2026-04-20T10:00:00Z").toISOString();
    const t1 = new Date("2026-04-20T10:01:00Z").toISOString();
    const t2 = new Date("2026-04-20T10:02:00Z").toISOString();
    return [
        {
            SessionSignalOrdinal: 0,
            SessionTraceOrdinal: 0,
            Kind: "SessionStarted",
            KindSchemaVersion: 1,
            OccurredAtUtc: t0,
            SourceOrigin: "export-session-fixture/dry-run",
            Evidence: {
                Kind: "Synthetic",
                Identifier: `session:${sessionId}:started`,
                Summary: "Dry-run session-started marker",
            },
            Payload: { sessionId },
        },
        {
            SessionSignalOrdinal: 1,
            SessionTraceOrdinal: 1,
            Kind: "EspPhaseChanged",
            KindSchemaVersion: 1,
            OccurredAtUtc: t1,
            SourceOrigin: "export-session-fixture/dry-run",
            Evidence: {
                Kind: "Derived",
                Identifier: "legacy-export-v1",
                Summary: "phase=AccountSetup (dry-run)",
                DerivationInputs: {
                    legacyEventType: "esp_phase_changed",
                    legacyPhase: "AccountSetup",
                },
            },
            Payload: { phase: "AccountSetup" },
        },
        {
            SessionSignalOrdinal: 2,
            SessionTraceOrdinal: 2,
            Kind: "SessionRecovered",
            KindSchemaVersion: 1,
            OccurredAtUtc: t2,
            SourceOrigin: "export-session-fixture/dry-run",
            Evidence: {
                Kind: "Synthetic",
                Identifier: `session:${sessionId}:recovered`,
                Summary: "Dry-run reboot/recover marker",
            },
            Payload: {},
        },
    ];
}

function writeJsonl(path, records) {
    const dir = dirname(path);
    if (!existsSync(dir)) {
        mkdirSync(dir, { recursive: true });
    }
    const body = records.map((r) => JSON.stringify(r)).join("\n") + "\n";
    writeFileSync(path, body, { encoding: "utf-8" });
}

function validateJsonl(path) {
    const text = readFileSync(path, { encoding: "utf-8" });
    let ok = true;
    let n = 0;
    for (const [i, rawLine] of text.split(/\r?\n/).entries()) {
        const line = rawLine.trim();
        if (!line || line.startsWith("#")) continue;
        let rec;
        try {
            rec = JSON.parse(line);
        } catch (err) {
            console.error(`Line ${i + 1}: JSON parse error — ${err.message}`);
            ok = false;
            continue;
        }
        n++;
        const problems = validateSignalShape(rec);
        if (problems.length) {
            console.error(`Line ${i + 1}: ${problems.join("; ")}`);
            ok = false;
        }
    }
    console.log(`Validated ${n} signals from ${path}: ${ok ? "OK" : "FAIL"}`);
    return ok;
}

function validateSignalShape(rec) {
    const errs = [];
    for (const key of ["SessionSignalOrdinal", "SessionTraceOrdinal", "Kind", "KindSchemaVersion", "OccurredAtUtc", "SourceOrigin", "Evidence"]) {
        if (!(key in rec)) errs.push(`missing ${key}`);
    }
    if (rec.KindSchemaVersion !== undefined && rec.KindSchemaVersion < 1) errs.push("KindSchemaVersion must be >= 1");
    if (rec.Evidence) {
        const ev = rec.Evidence;
        for (const key of ["Kind", "Identifier", "Summary"]) {
            if (!(key in ev) || ev[key] === "") errs.push(`Evidence.${key} missing/empty`);
        }
        if (ev.Kind === "Derived" && (!ev.DerivationInputs || Object.keys(ev.DerivationInputs).length === 0)) {
            errs.push("Evidence.Derived requires DerivationInputs");
        }
    }
    return errs;
}
