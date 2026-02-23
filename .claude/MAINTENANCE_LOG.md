# Maintenance Log

Tracks when recurring maintenance processes were last run.

| Process | Last Run | Next Due | Cadence | Notes |
|---------|----------|----------|---------|-------|
| NuGet vulnerability check | 2026-02-15 | 2026-02-22 | Weekly | `dotnet list package --vulnerable` |
| Todo audit | 2026-02-12 | 2026-02-19 | Weekly | Stale items, completed moves |
| Code simplification | 2026-02-23 | After next feature change | After features | Dead code, unused abstractions |
| ReSharper InspectCode | 2026-02-17 | 2026-02-24 | Weekly | `/resharper` — fix Tier 1+2 warnings |
| Context cleanup | 2026-02-12 | 2026-03-12 | Monthly | CLAUDE.md, .claude/, todos.md |
| Feature spec sync | 2026-02-12 | 2026-03-12 | Monthly | docs/features/ vs implementation |
| i18n audit | — | — | Monthly | Missing translations |
| Data model doc sync | 2026-02-12 | As needed | As needed | .claude/DATA_MODEL.md vs entities |
| Navigation audit | 2026-02-12 | 2026-03-12 | Monthly | `/nav-audit` — discoverability, backlinks |
| GDPR audit | — | — | Quarterly | Exports, consent, PII logging |
| Migration squash check | — | — | Monthly | Check `/Admin/DbVersion` on prod, QA (humans.n.burn.camp), and local dev. Oldest `lastApplied` across all three is the safe squash boundary. |
| NuGet full update | 2026-02-17 | 2026-03-17 | Monthly | Non-security package updates |
| About page package sync | 2026-02-15 | 2026-03-15 | Monthly | Update `About.cshtml` package versions after NuGet updates |
| GitHub issue triage | — | — | Weekly | Sync issues vs todos.md |

