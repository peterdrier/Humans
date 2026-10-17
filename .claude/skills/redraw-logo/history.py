#!/usr/bin/env python3
"""Render docs/brand/logo-history/history.png: every archived round plus the current master, side by side,
each at 260px with its favicon and navbar mark at 38px beneath. Labels come from logo.md's History lines
(`- **<yyyy-mm> · <Model>, <level>** — ...`); a folder with no matching line is labelled by its name.

Usage: python3 .claude/skills/redraw-logo/history.py [--chrome PATH]
"""
import argparse, os, re, subprocess, tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
WWW = os.path.join(ROOT, "src", "Humans.Web", "wwwroot")
HIST = os.path.join(ROOT, "docs", "brand", "logo-history")
INK, GOLD = "#3d2b1f", "#c9a96e"


def labels():
    out = {}
    with open(os.path.join(WWW, "img", "logo.md")) as f:
        for m in re.finditer(r"^- \*\*(\d{4}-\d{2}) · ([^*]+)\*\*", f.read(), flags=re.M):
            out[m.group(1)] = f"{m.group(1)} · {m.group(2).strip()}"
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--chrome", default=os.environ.get("CHROME", "/opt/pw-browsers/chromium"))
    a = ap.parse_args()
    lab = labels()
    rounds = []
    for d in sorted(os.listdir(HIST)):
        p = os.path.join(HIST, d)
        if os.path.isdir(p) and os.path.exists(os.path.join(p, "logo.svg")):
            rounds.append((lab.get(d[:7], d), os.path.join(p, "logo.svg"), os.path.join(p, "favicon.svg")))
    cur = max(lab) if lab else "current"
    rounds.append((lab.get(cur, "current"), os.path.join(WWW, "img", "logo.svg"), os.path.join(WWW, "img", "favicon.svg")))

    cells = "".join(
        f'<div><img src="file://{logo}" height="260"><br>{label}<br>'
        f'<img src="file://{fav}" height="38" style="margin-top:8px"> <img src="file://{logo}" height="38"></div>'
        for label, logo, fav in rounds)
    html = os.path.join(tempfile.gettempdir(), "_history.html")
    with open(html, "w") as f:
        f.write(f'<html><body style="margin:0;background:{INK};padding:16px;overflow:hidden;font-family:Georgia,serif;'
                f'color:{GOLD};text-align:center"><div style="display:flex;gap:30px;align-items:flex-end;justify-content:center">'
                f'{cells}</div></body></html>')
    out = os.path.join(HIST, "history.png")
    subprocess.run([a.chrome, "--headless", "--no-sandbox", "--disable-gpu", "--force-device-scale-factor=1",
                    f"--window-size={len(rounds) * 320 + 40},460", f"--screenshot={out}", f"file://{html}"],
                   check=True, capture_output=True)
    print("wrote", out, "with", len(rounds), "rounds")


if __name__ == "__main__":
    main()
