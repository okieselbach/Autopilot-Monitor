# Tasks

## Post-Deploy: WhiteGlove Scoring (Deploy-Datum: _YYYY-MM-DD eintragen bei Rollout_)

Plan: `C:\Users\OliverKieselbach\.claude\plans\1-whiteglove-klassifizierung-inkonsiste-piped-chipmunk.md`
Pflicht-Checks nach Deploy. Siehe [feedback_post_deploy_in_todo.md](C:/Users/OliverKieselbach/.claude/projects/c--Code-GitHubRepos-Autopilot-Monitor/memory/feedback_post_deploy_in_todo.md).

- [ ] **+24h** — Scoring-Verteilung Baseline: `query_table(table="Events", filter="EventType eq 'whiteglove_classification' and Timestamp ge <24h-ago>", select="data, SessionId, Timestamp")` → nach `data.wgConfidence` gruppieren (Strong/Weak/None); Ergebnis protokollieren
- [ ] **+24h** — Late-AAD-Rate: `query_table(... filter="EventType eq 'aad_user_joined_late'")` + gleiche Query für `'aad_placeholder_user_detected_late'` → niedrig aber nicht null erwartet
- [ ] **+24h** — Eigene Test-VM(s) prüfen: müssen `wgConfidence=None` liefern, **nie** `Weak` oder `Strong` (konkrete DeviceId/SessionId hier eintragen)
- [ ] **+72h** — Weak-Band drill-down: `query_table(... filter="EventType eq 'whiteglove_classification'")` → Client-Filter `wgConfidence=="Weak"` → `get_session_events(sessionId)` für ≥5 Sessions; sind das echte WG-false-negatives oder legitime Grenzfälle?
- [ ] **+7d** — Gewichts-Review: brauchen `ShellCoreWhiteGloveSuccess (+80)` oder `HasSaveWhiteGloveSuccessResult (+10)` Anpassung? Falls ja Follow-up-Ticket
- [ ] **+7d** — `shadow_discrepancy`-Rate vs. Baseline vor Deploy (siehe [project_shadow_sm_cutover.md](C:/Users/OliverKieselbach/.claude/projects/c--Code-GitHubRepos-Autopilot-Monitor/memory/project_shadow_sm_cutover.md)): sollte fallen, nicht steigen
- [ ] Ergebnisse inline dokumentieren (z.B. `✓ 2026-04-20: 142 Strong / 18 Weak / 3400 None`)

## Post-Deploy: WhiteGlove Guard-Site 4 (`esp_exiting` Classifier) (Deploy-Datum: _YYYY-MM-DD eintragen bei Rollout_)

Plan: `C:\Users\OliverKieselbach\.claude\plans\304620a8-3f18-4927-9bed-324bb1997303-bei-rustling-cake.md`
Referenz-Session die den Bug ausgelöst hat: `304620a8-3f18-4927-9bed-324bb1997303` (Status=Pending nach 2x whiteglove_complete).

- [ ] **+24h** — `whiteglove_classification` Events mit `callSite=EspExiting_SkipUserTrue`: `query_table(table="Events", filter="EventType eq 'whiteglove_classification' and Timestamp ge <24h-ago>")` → Client-Filter `data.callSite=="EspExiting_SkipUserTrue"` → Verteilung Strong/Weak/None protokollieren. Erwartung: die allermeisten echten Part-1-ESP-Exits bleiben Strong; Part-2-Resume-Sessions mit AADJ fallen auf Weak/None.
- [ ] **+24h** — Rate `decision_process_completion` mit `outcome=pending_part1` muss gegenüber 7d-Baseline vor Deploy sinken. `query_table(table="Events", filter="EventType eq 'decision_process_completion'")` + Client-Filter `data.outcome=="pending_part1"`.
- [ ] **+24h** — Suche nach Sessions mit `whiteglove_complete` **und** `aadJoinedWithUser=true` **und** `Status=Pending`: sollte nach Deploy ≈ 0 sein. Identifiziert Sessions die weiterhin falsch klassifiziert werden.
- [ ] **+7d** — `agent_trace` mit `decision=whiteglove_guard_esp_exiting` prüfen: DataJson muss `wgScore`/`wgConfidence`/`wgFactors` enthalten (Classifier-basiert), keine alten Einträge ohne Score.
