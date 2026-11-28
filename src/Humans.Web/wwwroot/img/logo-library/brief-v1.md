# Logo brief v1 — FROZEN

**Never edit this file.** Every library entry drawn against v1 must have had the same input, or the
library stops being a fair comparison of models. A changed brief is a new file (`brief-v2.md`), and
entries record which brief they used (`"brief": "v1"` in `entry.json`).

## The task
Draw the Nobodies mark — Leonardo da Vinci's Vitruvian Man — as one SVG file, `logo.svg`, gold on
aged ink. Make it the most convincing Vitruvian Man you can within the constraints below: real anatomy
that still reads as a clean glyph at navbar size. Draw it from scratch. Do not open, copy or trace
any other drawing of the mark in this repository (other library entries, `wwwroot/img/logo.svg`, git
history); the point is to see what you produce from this brief alone.

## Hard constraints
- **Palette:** gold `#c9a96e` figure, aged-ink `#3d2b1f` details. Nothing else. Transparent
  background — the file is shown on aged-ink tiles and on the aged-ink navbar.
- **Subject:** Leonardo's Vitruvian Man with the **four-arm / four-leg dual-pose overlay**: arms
  horizontal and raised, legs together and spread. The overlay is what makes it read as Vitruvian.
- **Leonardo's geometry:** height = square side; the circle is centred on the navel with radius
  0.618 × height, so the square *rests on the circle's bottom* and its top edge *cuts the circle*; the
  square's centre is the groin. Horizontal arms span the square; raised arms and spread legs reach the
  circle. Eight-head proportions.
- **Frame, in viewBox units:** `viewBox="-2 -2 244 244"` (cropped tight to the circle). Circle centre
  (120,120), r = 120. Square x=23, y=46, side 194. Head top y=46, chin y≈70, navel (120,120),
  groin (120,143). The figure is left/right symmetric about x=120.
- **Root element:** `<svg xmlns="http://www.w3.org/2000/svg" viewBox="-2 -2 244 244" role="img"
  aria-label="Vitruvian Man">`. No scripts, no external references, no raster images, no fonts.
- **Style:** a delicate engraved study, never a cartoon. Failure modes: torso or legs turning into
  dark stripes, the head reading as a smiley, the hands reading as forks.

## One drawing, every size
The same file is used from 16px to 280px, so it must simplify itself:
- **Bold solid base** — filled body and limbs plus the circle/square frame, in gold. The only thing
  that survives at 38px and below, so keep its forms generous.
- **Tiered etch** on top in aged ink, at finer stroke widths so each tier anti-aliases away below a
  size: tier 1 (≈2.0 units) main contours and the pose separations, shows from ~64px; tier 2 (≈1.4)
  muscle groups and hair, from ~96px; tier 3 (≈0.9) fingers, toes, face, hatching, from ~150px.
- **Faint gold construction** (axes, compass arcs, edge ticks) may enrich large sizes; it must vanish
  small.
- Where the two poses overlap, draw order and each limb's own contour must keep them distinguishable.

## Method — render and look, never judge from XML
Render with `.claude/skills/redraw-logo/render.py` after every change and look at both sheets: the
large one (anatomy, hands, face, the failure modes above) and the small one (96 / 64 / 48 / 38 / 24 /
16 at device scale 1, zoomed 3× nearest-neighbour — the navbar is 38px). Iterate until both read;
typically 5-8 passes. Finish with a one-paragraph note on what you aimed for and what is still weak.
