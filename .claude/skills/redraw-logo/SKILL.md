---
name: redraw-logo
description: "Redraw the Nobodies Vitruvian Man mark with the current model, derive every icon asset from it, archive the previous round, wire it into the site, open the PR. Peter runs this every 2-3 months when a new first-class model ships, to watch the brand evolve. Triggers: 'redraw the logo', 'logo round', 'give the logo a go', '/redraw-logo'."
argument-hint: "[model label for the history folder, e.g. opus-5.5] [effort level, e.g. high]"
---

# Redraw the logo

One round of the brand's model-by-model evolution. The brief is `src/Humans.Web/wwwroot/img/logo.md`
— read it first and in full; it holds the constraints, the self-simplifying design, and the history.
This skill is the procedure around it.

`$ARGUMENTS`: the model label for the archive folder (`<yyyy-mm>-<label>`) and the effort level the
session runs at. If empty, use the model you are, lowercased (`fable-5.1`), and ask Peter for the level
before step 5 — it goes in the History line and is not recoverable afterwards.

## 0. Branch
Feature branch off `origin/main` (`logo/<yyyy-mm>-<label>`). Cloud sessions work in the repo root;
local sessions in their worktree.

## 1. Archive the outgoing round
```
mkdir -p docs/brand/logo-history/<prev>
git mv src/Humans.Web/wwwroot/img/logo.svg    docs/brand/logo-history/<prev>/logo.svg
git mv src/Humans.Web/wwwroot/img/favicon.svg docs/brand/logo-history/<prev>/favicon.svg
```
`<prev>` is the last History line in `logo.md`. Render it once at 280px and keep that PNG in your
scratchpad: it is the bar the new drawing must clear.

## 2. Draw the master (`wwwroot/img/logo.svg`)
- Keep the brief's hard constraints and Leonardo's geometry exactly (coordinates are documented in
  the header comment of the previous master — reuse the frame, hip, shoulder and fingertip points).
- Goal of the round: **more convincingly human than last time at 280px**, still a delicate engraving.
  Redraw the anatomy; don't just add lines to the old file.
- Structure to keep: solid gold base + ink etch tiers (2.0 / 1.4 / 0.9) + faint construction; limb
  symbols carry their own etch so draw order gives occlusion; right-half torso mirrored about x=120.
- **Render and look, never judge from XML.** Headless Chrome is at `/opt/pw-browsers/chromium` in the
  cloud container. Two sheets, every iteration:
  - large: 700px, device scale 1 — anatomy, hands, face, stripes/smiley/fork failure modes.
  - small: 96 / 64 / 48 / 38 / 24 / 16 at device scale **1**, then zoom the PNG 3× nearest-neighbour
    (PIL) — the navbar is 38px; a 2× render lies about it.
  Iterate until both read. Typically 5-8 passes.

## 3. Derive every icon
```
python3 .claude/skills/redraw-logo/derive.py --out <scratchpad>/review.png
```
Writes `favicon.svg` (solid base on the tile, etch stripped, bolder frame), `favicon.ico` (16/32/48),
both apple-touch PNGs (180) and `icon-512.png`, plus a review sheet. Look at the review sheet and at
the `.ico` sizes zoomed. If the strip regex in `derive.py` no longer matches the master's structure,
fix the script, not the SVG.

## 4. Integrate
- Nothing in the `<head>` wiring changes unless an asset is added; `logo.md` lists it.
- The About page (`Views/About/Index.cshtml`, `.about-mark`) shows the master at 260px on an
  aged-ink disc — check it still fits the drawing (viewBox changes move the figure).
- Build the Shell: `dotnet build src/Humans.Web -v quiet`. No tests cover the assets.
- Walk the AGENTS.md surface list: no new strings (decorative `alt=""`), no auth, no data, no
  migrations.

## 5. Record and ship
- `logo.md` → History: one line, `**<yyyy-mm> · <Model>, <level>**`, what changed, what to beat next time.
- `python3 .claude/skills/redraw-logo/history.py` regenerates `docs/brand/logo-history/history.png`
  (every round side by side); look at it, it goes in the PR body.
- Commit the master, derived assets, archive, `history.png`, docs; push; open the PR against
  `origin/main` with `history.png` in the body (rendered images, not code).
- Hand off to the steward (`pd:steward`) or stop, per `steward-thin-session`.

## Don'ts
- Don't change palette, subject, geometry or the transparent-navbar / baked-tile split without Peter.
- Don't ship assets you haven't looked at rendered, at the size they'll be used.
- Don't edit the archived rounds.
