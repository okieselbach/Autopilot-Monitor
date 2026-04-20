# Local Enrollment Session Fixtures — NOT committed

This directory holds **raw** or **partially-anonymized** session exports used during the
M3 reducer bring-up. Files here are **excluded from git** — see root `.gitignore`.

## Workflow (plan §4 M3)

1. Export a session via `tools/export-session-fixture.mjs` (pipes through MCP
   `get_session_events`).
2. Inspect the resulting `{sessionId}.jsonl` here. Adjust / anonymize as needed.
3. When a fixture is stable and anonymized, **move** it to
   `tests/fixtures/enrollment-sessions/` and commit it with a short README entry
   (see that directory's README for the commit policy).

## Why this split

Plan: "Lokal Rohdaten arbeiten, nicht committen bis stabil + anonymisiert."
Prod-session exports contain tenant identifiers, device IDs, and user names that must
never enter the repo history. The two-directory pattern forces an explicit "promote"
step with review.
