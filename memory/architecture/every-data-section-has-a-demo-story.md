---
name: Every data section has a demo story or says why not
description: Adding a section with a DbContext, or data/a UI state to one: add it to the section's IDemoStory, or carry [assembly: NoDemoStory("reason")]; never seed demo data any other way.
---

Every section that owns a `DbContext` registers an `IDemoStory` (`Humans.Base.Demo`) with `services.AddDemoStory<T>(configuration)`, or carries `[assembly: NoDemoStory("reason")]` in its `Section.cs`. A feature that adds data or a state the UI shows adds it to its section's story.

**Why:** Peter, 2026-11-19, approved the demo world: non-production hosts get realistic data on top of what QA already holds, built through each section's real write paths and recorded in Development's demo ledger, so it can be topped up and reset without delete code.

**How to apply:**
- A story calls its own section's write verbs acting as a cast member, wraps each created row in `IDemoWorld.EnsureAsync` (never looks rows up by name), and lists the stories it needs in `After`.
- `RetireAsync` ends rows only through actions the app already has (cancel, withdraw, deactivate, `PurgeAsync`) and marks the rest left behind. Never add a delete verb for the demo.
- Never touch rows the demo didn't make; never flip settings or the active event on a host that has one.
- `DemoStoryCoverageTests` (Humans.Web.Tests) enforces coverage and ordering; there is no baseline.
