# Nobodies logo — how to (re)generate

Peter redoes this mark every few months as image/LLM models improve, to watch the brand
evolve over time. This file is the standing brief so nobody has to re-explain it.
**Keep the constraints below; improve only the execution.** `logo.svg` (this folder) holds the
master figure — every other asset is derived from it.

## Hard brand constraints (do not change without Peter)
- **Palette:** gold `#c9a96e` figure, aged-ink `#3d2b1f` background. Nothing else.
  (Brand tokens `--h-gold` / `--h-aged-ink` in `wwwroot/css/tokens.css`.)
- **Subject:** Da Vinci's Vitruvian Man — **circle + square** (square dropped to the groin,
  navel at the circle's centre) with the **four-arm / four-leg dual-pose overlay**. That overlay
  is what makes it read as Vitruvian; always keep it.
- **Transparent navbar mark vs. baked icon tiles.** The in-page navbar logo is **transparent**
  (the navbar is already `#3d2b1f`, so the figure sits straight on it). Every standalone *icon* —
  browser tab, iOS, Android — instead **bakes the `#3d2b1f` tile** behind the figure, because those
  surfaces are not brown. A transparent favicon washes out in the tab; the brown tile is the look.

## The mark self-simplifies (one drawing, every size)
`logo.svg` reads as a clean solid glyph when tiny and a detailed engraved study when large, from
one file:
- **Bold solid base** — filled body + rounded-capsule limbs + circle/square frame. The only thing
  that survives ≤38px, so keep its strokes generous; viewBox cropped tight to the outer circle so
  it fills its box (no dead padding) in the navbar.
- **Tiered etched detail** on top in aged-ink at progressively finer stroke widths, so each tier
  anti-aliases *away* below a size threshold (self-simplifying):
  - tier 1 (~2px): main contours + dual-pose separations — shows ≳64px
  - tier 2 (~1.4px): finer anatomy + hair — shows ≳96px
  - tier 3 (~0.9px): fingers, toes, face, hatching — shows ≳150px
  - faint gold **construction** (axes, dashed proportion circle, compass arc, edge ticks, corner
    flourishes) — enriches large sizes, vanishes small.
- Keep detail **fine and light** (delicate, like the real drawing). Failure modes to avoid:
  torso/legs turning into dark stripes, and the head reading as a smiley.

## Method — render and LOOK, never judge from XML
1. Edit `logo.svg`.
2. Rasterise with headless Chrome and **read the PNG yourself** across the full range
   (16 / 38 / 64 / 96 / 180 / 280) on a `#3d2b1f` background *before* showing Peter; fix whatever
   is muddy small / busy large, then iterate. A guide border around each render exposes padding.
   `chrome --headless --force-device-scale-factor=2 --window-size=W,H --screenshot=out.png file:///…/preview.html`
3. Show Peter rendered images, not code.

## Assets — regenerate ALL of these when the mark changes
| File | Form | Size | Purpose |
|------|------|------|---------|
| `wwwroot/img/logo.svg` | transparent | vector | master figure; navbar `<img>` |
| `wwwroot/img/favicon.svg` | `#3d2b1f` tile | vector | browser-tab favicon (solid base only — etch is invisible ≤48px) |
| `wwwroot/favicon.ico` | `#3d2b1f` tile | 16/32/48 | legacy/fallback favicon, packed from favicon.svg |
| `wwwroot/apple-touch-icon.png` | `#3d2b1f` tile | 180×180 | iOS home screen (~15px pad) |
| `wwwroot/apple-touch-icon-precomposed.png` | `#3d2b1f` tile | 180×180 | identical copy |
| `wwwroot/icon-512.png` | `#3d2b1f` tile | 512×512 | master / Android-PWA (~44px pad) |

How to regenerate the derived assets from `logo.svg`:
- **favicon.svg** — the solid base figure (frame + limbs + body, **no etch**) on a full-bleed
  `#3d2b1f` square, figure centred (~8% padding).
- **favicon.ico** — render favicon.svg, downscale to 16/32/48, pack as a PNG-embedded `.ico`.
- **apple-touch / icon-512** — centre `logo.svg` on a solid `#3d2b1f` square at the target px
  (`--force-device-scale-factor=1` for exact dimensions); copy the 180 to `-precomposed`.
- The two apple PNGs **must** live at the site root — browsers probe `/apple-touch-icon.png`
  regardless of markup (that 404-fix is why they exist — PR #1006).

Wiring in `_Layout.cshtml` + `_AdminLayout.cshtml` `<head>`:
`rel="icon"` → `favicon.ico` (`sizes="any"`) + `favicon.svg`, `rel="apple-touch-icon"` → 180 png,
`rel="icon" sizes="512x512"` → 512 png. The navbar `<img>` uses `logo.svg`.

## History
- **2026-06 · Opus 4.8** — first self-simplifying mark. Replaced an over-faint full-anatomy
  `logo.svg` (washed out below ~64px) and the previous `favicon.svg`. Kept the transparent navbar
  mark and the brown favicon tile as deliberately distinct assets — a transparent tab icon looked
  weak in the toolbar, so every icon context bakes the brown tile.
