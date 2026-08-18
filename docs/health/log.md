# Section Doctor — Run Log

One line per run, append-only.

| Date | Section | What ran | Outcome | PR |
|---|---|---|---|---|
| 2026-08-16 | Containers | Shakedown run (`--section`, no plan): deep assessment + strike | Doc-code alignment, dead surface deletion, 5 tests, i18n fix, 3 dead resx keys; the one Needs-Peter item (phase-gating lead container CRUD) resolved in-run — current split intended, re-evaluate December 2026 | peterdrier/Humans#1341 |
| 2026-08-17 | Guide | `--section=Guide`, no plan: deep assessment + strike | Two access defects fixed (admin block leaking to anonymous; Events/Store Admin blocked from their own blocks), 3 content-pinning tests, first controller tests, dead `Humans.Infrastructure` doc paths | peterdrier/Humans#1354 |
| 2026-08-18 | Finance | First scheduled run (plan written this run): deep assessment + strike, then the whole Needs-Peter queue answered in-session and applied | Section doc rewritten off the pre-G5 controller (phantom Budget routes, phantom Tickets dependency, shipped read-split still listed as future work), same claims swept out of the code comments and the section index; the untested invariants pinned (Madrid date conversion, contact-list cache); InspectCode findings. Then, on Peter's go: `Service` split into `HoldedDocService` + `CreditorService`, public contract narrowed, `RawPayload` dropped, rationale blocks trimmed. Reforge 254 → 215 | peterdrier/Humans#1367 |
