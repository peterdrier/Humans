# Nobodies logo — the standing brief

Peter redraws this mark every 2-3 months, when a new first-class model ships, to watch the brand
evolve as the models do. The process is the `/redraw-logo` skill (`.claude/skills/redraw-logo/`);
this file is the brief it draws against. **Keep the constraints; improve only the execution.**
`logo.svg` (this folder) is the master figure — every other asset is derived from it by the
skill's `derive.py`.

## Hard brand constraints (do not change without Peter)
- **Palette:** gold `#c9a96e` figure, aged-ink `#3d2b1f` background. Nothing else.
  (Brand tokens `--h-gold` / `--h-aged-ink` in `wwwroot/css/tokens.css`.)
- **Subject:** Leonardo's Vitruvian Man with the **four-arm / four-leg dual-pose overlay**. That
  overlay is what makes it read as Vitruvian; always keep it.
- **Leonardo's geometry, faithfully (since 2026-10):** height = square side; the circle is centred on
  the navel with radius 0.618 × height, so the square *rests on the circle's bottom* and its top edge
  *cuts the circle*; the square's centre is the groin. Horizontal arms span the square; raised arms
  and spread legs reach the circle. Eight-head proportions. The viewBox is cropped tight to the outer
  circle so the mark fills its box in the navbar.
- **Transparent navbar mark vs. baked icon tiles.** The navbar `<img>` is **transparent** (the navbar
  is already `#3d2b1f`). Every standalone *icon* — browser tab, iOS, Android — **bakes the `#3d2b1f`
  tile** behind the figure, because those surfaces are not brown. A transparent favicon washes out in
  the tab. The About page shows the master large on its own aged-ink disc for the same reason.

## Direction: incrementally more realistic
Each round should look more like Leonardo's drawing than the last — real anatomy, hands with fingers,
feet, a face, hair — while staying a *delicate engraved study*, never a cartoon. Judge against the
previous round in `docs/brand/logo-history/`: if it isn't more convincingly human at 280px, it isn't
done. Keep the detail fine and light. Failure modes to avoid: torso/legs turning into dark stripes,
the head reading as a smiley, the hands reading as forks.

## The mark self-simplifies (one drawing, every size)
`logo.svg` reads as a clean solid glyph when tiny and a detailed engraved study when large, from one
file:
- **Bold solid base** — filled body + limbs + circle/square frame, in gold. The only thing that
  survives ≤38px, so keep its forms generous.
- **Tiered etched detail** on top in aged-ink at progressively finer stroke widths, so each tier
  anti-aliases *away* below a size threshold:
  - tier 1 (2.0 units): main contours, dual-pose separations, clavicle/pectoral — shows ≳64px
  - tier 2 (1.4 units): muscle groups, hair — shows ≳96px
  - tier 3 (0.9 units): fingers, toes, face, hatching — shows ≳150px
  - faint gold **construction** (axes, dashed inscribed circle, compass arc, edge ticks, corner
    flourishes) — enriches large sizes, vanishes small.
- Limb symbols carry their own etch so **draw order gives occlusion**: the raised arm's lower contour
  is what separates it from the horizontal arm where they overlap.

**Option kept in reserve:** if one file can no longer serve both ends, split into a `logo-large.svg`
(About page, 512 tile) and keep `logo.svg` as the navbar/small master. Not needed yet; the one-file
trick is the point of the design, so exhaust it first.

## Method — render and LOOK, never judge from XML
1. Edit `logo.svg`.
2. Rasterise with headless Chrome and **read the PNG yourself** across the full range
   (16 / 38 / 64 / 96 / 180 / 280) on a `#3d2b1f` background *before* showing Peter; fix whatever is
   muddy small / busy large, then iterate. Judge small sizes at device scale 1 (then zoom the PNG
   nearest-neighbour) — a 2× render hides what 38px really looks like. A guide border exposes padding.
3. Show Peter rendered images, not code.

## Assets — all derived by `derive.py` when the mark changes
| File | Form | Size | Purpose |
|------|------|------|---------|
| `wwwroot/img/logo.svg` | transparent | vector | master figure; navbar `<img>`; About page disc |
| `wwwroot/img/favicon.svg` | `#3d2b1f` tile | vector | browser-tab favicon (solid base only, bolder frame, no etch) |
| `wwwroot/favicon.ico` | `#3d2b1f` tile | 16/32/48 | legacy/fallback favicon, packed from favicon.svg |
| `wwwroot/apple-touch-icon.png` | `#3d2b1f` tile | 180×180 | iOS home screen (15px pad) |
| `wwwroot/apple-touch-icon-precomposed.png` | `#3d2b1f` tile | 180×180 | identical copy |
| `wwwroot/icon-512.png` | `#3d2b1f` tile | 512×512 | master / Android-PWA (44px pad) |

The two apple PNGs **must** live at the site root — browsers probe `/apple-touch-icon.png` regardless
of markup (that 404-fix is why they exist — PR #1006).

Wiring in `_Layout.cshtml` + `_AdminLayout.cshtml` `<head>`: `rel="icon"` → `favicon.ico`
(`sizes="any"`) + `favicon.svg`, `rel="apple-touch-icon"` → 180 png, `rel="icon" sizes="512x512"` →
512 png. The navbar `<img>` uses `logo.svg`; `Views/About/Index.cshtml` shows it at 260px in
`.about-mark`.

## History
Every round archives the outgoing master + favicon under `docs/brand/logo-history/<yyyy-mm>-<model>/`,
regenerates `docs/brand/logo-history/history.png` (`history.py`, every round side by side) and adds a
line here: `**<yyyy-mm> · <Model>, <effort level>**`. Level was not recorded before 2026-10.
- **2026-02 · Opus 4.6, level unrecorded** — the original, drawn with the Da Vinci rebrand (commit
  `08ca5f70f`). Hand-coded full-anatomy study, thin and faint, separate `favicon.svg`; square and
  circle concentric. Washed out below ~64px. `favicon.ico` packed from it in 2026-04 (PR #161).
- **2026-06 · Opus 4.8 (1M), level unrecorded** — first self-simplifying mark (PR #1007). Stick-figure
  anatomy in bold capsules plus etch tiers, square and circle concentric; brown-tile favicon
  restored after the transparent one washed out in the tab; apple-touch PNGs added (PR #1006).
- **2026-10 · Fable 5.1, default effort** — faithful Leonardo geometry (square rests on the circle, navel-centred).
  Real anatomy: eight-head proportions, deltoids/biceps/pectorals/abs/quads/calves etched, hands with
  fingers and thumb, feet with toes, face and shoulder-length hair. Process made repeatable:
  `/redraw-logo` skill + `derive.py` generate every icon from the master; previous round archived.
