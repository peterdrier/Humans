# Nobodies logo — the live mark

`logo.svg` (this folder) is the site's mark; every icon below is derived from it by
`.claude/skills/redraw-logo/derive.py`. It is promoted, when Peter chooses, from the **logo library**
(`img/logo-library/`, shown at `/About/Logos`): one entry per model and effort level, each drawn by
`/redraw-logo` against the frozen brief `img/logo-library/brief-v1.md` (also shown on the page). The brief holds the
drawing constraints (palette, subject, Leonardo's geometry, the self-simplifying tiers).

**Transparent navbar mark vs. baked icon tiles.** The navbar `<img>` is **transparent** (the navbar
is already `#3d2b1f`). Every standalone *icon* — browser tab, iOS, Android — **bakes the `#3d2b1f`
tile** behind the figure, because those surfaces are not brown. A transparent favicon washes out in
the tab. The About page shows the master large on its own aged-ink disc for the same reason.

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

## Live logo
Each promotion adds a line: `**<yyyy-mm> · <library entry>**`. Older rounds' notes live in their
library entries.
- **2026-02 · 2026-02-opus-4.6-unrecorded** — the original (commit `08ca5f70f`); `favicon.ico` packed
  from it in 2026-04 (PR #161).
- **2026-06 · 2026-06-opus-4.8-unrecorded** — PR #1007; apple-touch PNGs added (PR #1006).
- **2026-10 · 2026-10-fable-5.1-high** — PR #1993; `derive.py` generates every icon from the master.
