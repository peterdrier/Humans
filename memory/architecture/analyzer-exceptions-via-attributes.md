---
name: Analyzer exceptions live on the code via attributes, never in centralised lists
description: Per-class `[Grandfathered("HUM####", ...)]` attributes are the only way to grandfather a class out of an analyzer rule. No baselines, no editorconfig per-file severity blocks, no analyzer-internal allowlists, no SuppressMessage scattered files-as-a-list.
type: feedback
---

When an analyzer rule (HUMxxxx) needs to allow existing violators while still blocking new ones, the exception **lives as an attribute on the violating class itself**. Use `[Grandfathered(ruleId, justification, since, issueRef)]` from `Humans.Application.Architecture`. The analyzer detects the attribute and downgrades the diagnostic from Error to Warning for that class only.

**Why:** Centralised lists conflict on every merge and pull cleanup attention away from the violating code. We've been bitten by this pattern enough times (analyzer-internal allowlists, baseline-text-files, surface-budget history blocks, `.editorconfig` per-file overrides) that the project's posture is now: **lists for this purpose are dead**.

**How to apply:**
- Adding a grandfathered class: put `[Grandfathered("HUM####", justification: "<why>", since: "<YYYY-MM-DD>", issueRef: "<umbrella issue or per-class issue>")]` on the class declaration. Add a `using Humans.Application.Architecture;`. Done — no analyzer change, no editorconfig change, no test baseline.
- Fixing a grandfathered class: refactor the code so the rule no longer fires, then **delete the `[Grandfathered]` attribute in the same commit**. Symmetric. The analyzer will fire Error on regression.
- Filing a new rule: design the analyzer to inspect `INamedTypeSymbol.GetAttributes()` for `[Grandfathered]` with the matching ruleId. Emit at `DiagnosticSeverity.Warning` when matched, `DiagnosticSeverity.Error` otherwise, via the `Diagnostic.Create` overload that takes an `effectiveSeverity` argument.

**Build posture supporting this:** `TreatWarningsAsErrors` stays `true` in `Directory.Build.props` so nullable-reference, Roslynator, and Meziantou strictness is preserved system-wide. Grandfathered analyzer rule IDs are exempted via `<WarningsNotAsErrors>$(WarningsNotAsErrors);HUM####</WarningsNotAsErrors>` — one ID per grandfathered analyzer rule, removed when the last `[Grandfathered]` for that rule is gone. That `WarningsNotAsErrors` list is **rule-level** (analyzer ID), not **violator-level** (class name / file path) — it's part of the central build config, not a maintained list of who's allowed to break what. The `since` date in each `[Grandfathered]` attribute lets reviewers see how long a grandfather has been in place — past a reasonable cleanup window, that's a signal to scold ourselves.

**What this rule forbids (per-violator lists — the maintenance hazard):**
- A new entry in a baseline `.txt` ratchet file (none should be added for analyzer rules going forward).
- A `dotnet_diagnostic.HUMxxxx.severity = warning` block in `.editorconfig` targeting specific files.
- An allowlist of class names hardcoded inside the analyzer source.
- A `[SuppressMessage("Architecture", "HUMxxxx")]` attribute used as a maintained allowlist of who's exempt — those *suppress* the diagnostic entirely; we want it *visible* as a warning so the TODO doesn't fade.

**What this rule allows (rule-level config — the central, intentional knobs):**
- `WarningsNotAsErrors` listing analyzer rule IDs that have a grandfather mechanism. Each ID appears once and represents "this rule has tagged grandfathers; let those warnings through." Per-violator detail lives on the code via `[Grandfathered]`.

When in doubt, ask: "is this list keyed by *violator* (class name / file path) or by *rule* (analyzer ID)?" Violator lists are forbidden. Rule-level config is fine.
