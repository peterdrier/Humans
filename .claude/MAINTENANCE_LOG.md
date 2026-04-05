# Maintenance Log

Tracks when recurring maintenance processes were last run.

| Process | Last Run | Next Due | Cadence | Est. Cost | Notes |
|---------|----------|----------|---------|-----------|-------|
| NuGet vulnerability check | 2026-04-05 | 2026-04-12 | Weekly | — | `dotnet list package --vulnerable` |
| Todo audit | 2026-03-08 | 2026-03-15 | Weekly | — | Stale items, completed moves |
| Code simplification | 2026-02-24 | — | After features | codex: ~5% | Dead code, unused abstractions |
| ReSharper InspectCode | 2026-02-24 | 2026-03-03 | Weekly | — | `/resharper` — fix Tier 1+2 warnings. Codex can't run `jb` in sandbox. |
| Context cleanup | 2026-03-18 | 2026-04-18 | Monthly | — | CLAUDE.md, .claude/, todos.md |
| Feature spec sync | 2026-02-12 | 2026-03-12 | Monthly | — | docs/features/ vs implementation |
| i18n audit | 2026-02-24 | 2026-03-24 | Monthly | gemini: ~2% | Missing translations |
| Data model doc sync | 2026-02-12 | As needed | As needed | — | .claude/DATA_MODEL.md vs entities |
| Navigation audit | 2026-03-22 | 2026-04-22 | Monthly | — | `/nav-audit` — discoverability, backlinks |
| GDPR audit | — | — | Quarterly | — | Exports, consent, PII logging |
| Migration squash check | 2026-02-24 | 2026-03-24 | Monthly | — | Check `/Admin/DbVersion` on prod, QA (humans.n.burn.camp), and local dev. Oldest `lastApplied` across all three is the safe squash boundary. |
| NuGet full update | 2026-04-05 | 2026-05-05 | Monthly | — | Non-security package updates |
| About page package sync | 2026-04-05 | 2026-05-05 | Monthly | — | Update `About.cshtml` package versions after NuGet updates |
| GitHub issue triage | 2026-03-08 | 2026-03-15 | Weekly | — | Sync issues vs todos.md |
| Access matrix verification | 2026-03-18 | 2026-03-25 | Weekly | — | Compare `AccessMatrixDefinitions.cs` against actual controller auth checks |
