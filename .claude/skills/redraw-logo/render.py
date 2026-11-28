#!/usr/bin/env python3
"""Render a logo.svg into the two review sheets the brief asks for.

Usage:  python3 .claude/skills/redraw-logo/render.py <path/to/logo.svg> [--out DIR] [--chrome PATH]

Writes DIR/large.png (700px on aged ink) and DIR/small.png (96/64/48/38/24/16 at device scale 1, zoomed
3x nearest-neighbour so 38px is judged honestly). DIR defaults to <system temp>/logo-render-<entry folder>, never the repo.
"""
import argparse, os, shutil, subprocess, tempfile
from pathlib import Path
from PIL import Image

INK = "#3d2b1f"
SMALL = (96, 64, 48, 38, 24, 16)
CANDIDATES = ("/opt/pw-browsers/chromium", r"C:\Program Files\Google\Chrome\Application\chrome.exe",
              r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
              "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome")


def find_chrome(explicit):
    for c in (explicit, os.environ.get("CHROME"), *CANDIDATES):
        if c and os.path.exists(c):
            return c
    for name in ("chromium", "chromium-browser", "google-chrome", "chrome", "msedge"):
        if shutil.which(name):
            return shutil.which(name)
    raise SystemExit("No headless Chrome found; pass --chrome PATH or set CHROME.")


def shot(chrome, body, out, w, h):
    html = Path(tempfile.mkdtemp()) / "sheet.html"
    html.write_text(f'<html><body style="margin:0;background:{INK};overflow:hidden">{body}</body></html>')
    subprocess.run([chrome, "--headless", "--no-sandbox", "--disable-gpu", "--hide-scrollbars",
                    "--force-device-scale-factor=1", f"--window-size={w},{h}",
                    f"--screenshot={out}", html.as_uri()], check=True, capture_output=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("svg")
    ap.add_argument("--out")
    ap.add_argument("--chrome")
    a = ap.parse_args()
    svg = Path(a.svg).resolve()
    out = Path(a.out).resolve() if a.out else Path(tempfile.gettempdir()) / f"logo-render-{svg.parent.name}"
    out.mkdir(parents=True, exist_ok=True)
    chrome = find_chrome(a.chrome)
    src = svg.as_uri()

    large = out / "large.png"
    shot(chrome, f'<img src="{src}" width="700" style="display:block;margin:10px">', large, 800, 800)
    Image.open(large).crop((0, 0, 720, 720)).save(large)

    gap, pad = 12, 8
    width = sum(SMALL) + gap * (len(SMALL) - 1) + 2 * pad
    height = SMALL[0] + 2 * pad
    imgs = "".join(f'<img src="{src}" width="{s}" style="margin-right:{gap}px">' for s in SMALL)
    raw = out / "small-1x.png"
    shot(chrome, f'<div style="padding:{pad}px;display:flex;align-items:flex-end">{imgs}</div>', raw,
         max(width, 500), max(height, 200))
    im = Image.open(raw).crop((0, 0, width, height))
    im.resize((width * 3, height * 3), Image.NEAREST).save(out / "small.png")
    raw.unlink()
    print("wrote", out / "large.png", "and", out / "small.png")


if __name__ == "__main__":
    main()
