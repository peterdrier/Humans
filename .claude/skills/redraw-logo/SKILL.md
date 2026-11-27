---
name: redraw-logo
description: "Add one model's drawing of the Nobodies Vitruvian Man to the logo library (shown at /About/Logos), drawn against the frozen brief. Many models and effort levels can run in parallel without conflicts. Promoting an entry to the live site logo is a separate step, only when Peter asks. Triggers: 'redraw the logo', 'logo round', 'add a logo library entry', '/redraw-logo'."
argument-hint: "<model, e.g. 'Claude Opus 5.5'> <effort, e.g. high>"
---

# Redraw the logo — a library entry

The logo library is a timeline of how each model, at each effort level, draws the same mark from the
same brief. One run of this skill adds **one entry**, nothing else. It never changes the live logo.

`$ARGUMENTS`: family, model and version (`Claude Opus 5.5`, `Gemini Neon 4`) and the
effort level the session runs at (`low` / `medium` / `high` / ...). If either is missing, use what you
know you are and ask for the effort — it is not recoverable afterwards.

## The entry
`<slug>` = `<yyyy-mm>-<model>-<effort>`, all lowercase, spaces to `-`, the vendor prefix dropped:
`2026-11-opus-5.5-high`, `2026-11-gemini-4-neon-high`. The folder is
`src/Humans.Web/wwwroot/img/logo-library/<slug>/` and holds exactly two files:

- `logo.svg` — your drawing.
- `entry.json`:
  ```json
  {
    "date": "2026-11",
    "family": "Claude",
    "model": "Opus",
    "version": "5.5",
    "effort": "high",
    "brief": "v1",
    "notes": {
      "en": "One short paragraph: what you aimed for, what is still weak.",
      "es": "...", "de": "...", "it": "...", "fr": "...", "ca": "...", "nl": "..."
    }
  }
  ```
  `notes` is shown on the public page, so it needs all seven cultures (a test enforces it); plain
  prose, no markdown. The terms: **family** `Claude` / `GPT` / `Gemini`, **model** `Opus` / `Sol` / `Neon`,
  **version** the number alone (`5.5`, `6.2`, `4`), **effort** the level. The gallery groups by
  family, then model, then version. Save the
  file as UTF-8 without a BOM. `effort` is one of `low` / `medium` / `high` / `xhigh` / `max` — the page
  translates those labels.

If the folder already exists, stop and say so — the entry has been drawn.

## Steps
1. **Branch.** Off `origin/main`, branch `logo/<slug>`, in your own worktree. Parallel runs are
   expected; you touch only your own folder, so they never conflict.
2. **Read the brief: `.claude/skills/redraw-logo/brief-v1.md`, and only that.** It is frozen and it is
   the whole input. Do not open other library entries, `wwwroot/img/logo.svg`, `logo.md` or git
   history of the mark — the comparison only works if every model starts from the same text.
3. **Draw and iterate** per the brief, rendering every pass with
   `python3 .claude/skills/redraw-logo/render.py <folder>/logo.svg` and looking at both sheets.
4. **Write `entry.json`.**
5. **Ship.** Commit only the new folder, push, open a PR against `origin/main` titled
   `Logo library: <model>, <effort>`. The body is your notes plus a link to the entry on the PR's
   preview deploy, `https://<pr-number>.n.burn.camp/About/Logos`. Don't commit the render sheets.
   Before pushing, run `dotnet test tests/Humans.Web.Tests -v quiet --filter LogoLibrary`.
6. **Tend the PR** per `.claude/steward.md`, or stop.

## Promoting an entry to the live logo — only when Peter asks
This is the old "round": the chosen entry becomes the site's mark.
1. Copy `<entry>/logo.svg` over `src/Humans.Web/wwwroot/img/logo.svg`.
2. `python3 .claude/skills/redraw-logo/derive.py --out <scratchpad>/review.png` derives the favicon,
   `.ico`, apple-touch and 512 icons. Look at the review sheet and the `.ico` sizes zoomed. If the
   strip regex no longer matches the drawing's structure, fix the script, not the SVG.
3. Check the About page disc (`.about-mark`, 260px) still fits the drawing.
4. Record the promotion in `src/Humans.Web/wwwroot/img/logo.md` ("Live logo").
5. Build the Shell (`dotnet build src/Humans.Web -v quiet`), commit, PR.

## Don'ts
- Never edit `brief-v1.md` or any existing library entry. A new brief is `brief-v2.md`, and Peter
  decides when.
- Don't change palette, subject or geometry; they are in the brief.
- Don't ship a drawing you haven't looked at rendered.
