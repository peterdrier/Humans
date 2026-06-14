# Nobodies logo — how to (re)generate

Peter redoes this mark every few months as image/LLM models improve, to watch the brand
evolve over time. This file is the standing brief so nobody has to re-explain it.
**Keep the constraints below; improve only the execution.** `logo.svg` (this folder) is the
single source of truth — everything else is derived from it.

## Hard brand constraints (do not change without Peter)
- **Palette:** gold `#c9a96e` figure, aged-ink `#3d2b1f` background. Nothing else.
  (Brand tokens `--h-gold` / `--h-aged-ink` live in `wwwroot/css/tokens.css`.)
- **Subject:** Da Vinci's Vitruvian Man — **circle + square** (square dropped to the groin,
  navel at the circle's centre) with the **four-arm / four-leg dual-pose overlay**. That overlay
  is what makes it read as Vitruvian at a glance; always keep it.
- **One self-simplifying SVG.** `logo.svg` is used for the navbar (~38px) AND the favicon (16px)
  AND as the source for the raster icons. It must look like a clean solid glyph when tiny and a
  detailed engraved study when large — from the same file.

## Why one file works at every size (the technique)
- **Bold solid base** — filled body + rounded-capsule limbs + circle/square frame. This is the
  only thing that survives at ≤38px, so keep its strokes generous and let it fill the box (the
  viewBox is cropped tight to the outer circle — no dead padding).
- **Tiered etched detail** on top, drawn in aged-ink at progressively finer stroke widths so each
  tier anti-aliases *away* below a size threshold (self-simplifying):
  - tier 1 (~2px): main contours + the dual-pose separations — shows ≳64px
  - tier 2 (~1.4px): finer anatomy + hair — shows ≳96px
  - tier 3 (~0.9px): fingers, toes, face, muscle hatching — shows ≳150px
  - faint gold **construction** (axes, dashed proportion circle, compass arc, edge measurement
    ticks, corner flourishes) — enriches large sizes, vanishes small.
- Keep the detail **fine and light** (delicate, like the real drawing). Failure modes to avoid:
  torso/legs turning into dark stripes, and the head reading as a smiley. Between "crayon" and
  "photoreal Da Vinci" — aim for an engraved study.

## Method — render and LOOK, never judge from XML
1. Edit `logo.svg`.
2. Rasterise with headless Chrome and **read the PNG yourself** across the full range
   (16 / 38 / 64 / 96 / 180 / 280) on a `#3d2b1f` background *before* showing Peter. Fix whatever
   is muddy when small or busy when large, then iterate.
   `chrome --headless --force-device-scale-factor=2 --window-size=W,H --screenshot=out.png file:///…/preview.html`
3. Show Peter rendered images, not code. A guide border around each render exposes padding/fill.

## Deliverables — regenerate ALL of these from logo.svg
| File | Size | Purpose |
|------|------|---------|
| `wwwroot/img/logo.svg` | vector | source of truth — navbar + favicon |
| `wwwroot/apple-touch-icon.png` | 180×180 | iOS home screen — opaque `#3d2b1f` tile, ~15px padding |
| `wwwroot/apple-touch-icon-precomposed.png` | 180×180 | identical copy of the above |
| `wwwroot/icon-512.png` | 512×512 | master / Android-PWA — opaque tile, ~44px padding |

Render each PNG by centring `logo.svg` on a solid `#3d2b1f` square at the target pixel size
(`--force-device-scale-factor=1` for exact dimensions), then copy the 180 to `-precomposed`.
The two apple PNGs **must** sit at the site root: browsers probe `/apple-touch-icon.png`
regardless of markup (that 404-fix is why they exist — see PR #1006).

Wiring (already in `_Layout.cshtml` + `_AdminLayout.cshtml` `<head>`): `rel="icon"` → `logo.svg`,
`rel="apple-touch-icon"` → 180 png, `rel="icon" sizes="512x512"` → 512 png. `favicon.ico` is the
legacy fallback and is intentionally left alone.

## History
- **2026-06 · Opus 4.8** — first self-simplifying version. Replaced an over-faint full-anatomy
  study (`logo.svg`) that washed out below ~64px, plus a separate `favicon.svg` mark, which was
  retired in favour of this single file.
