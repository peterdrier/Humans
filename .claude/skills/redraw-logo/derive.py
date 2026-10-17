#!/usr/bin/env python3
"""Derive every icon asset from the master mark (wwwroot/img/logo.svg).

Usage:  python3 .claude/skills/redraw-logo/derive.py [--chrome PATH]

Outputs (all under src/Humans.Web/wwwroot):
  img/favicon.svg                  solid base figure on a #3d2b1f tile (etch + construction stripped)
  favicon.ico                      16/32/48 packed from favicon.svg
  apple-touch-icon.png (+ -precomposed)  180x180, logo.svg centred on the tile
  icon-512.png                     512x512, logo.svg centred on the tile
Also writes a review sheet to --out (default: ./logo-review.png) — look at it.
"""
import argparse, os, re, shutil, subprocess, tempfile
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
WWW = os.path.join(ROOT, "src", "Humans.Web", "wwwroot")
INK, GOLD = "#3d2b1f", "#c9a96e"


def chrome_shot(chrome, html, out, w, h, scale=1):
    subprocess.run([chrome, "--headless", "--no-sandbox", "--disable-gpu", f"--force-device-scale-factor={scale}",
                    f"--window-size={w},{h}", f"--screenshot={out}", f"file://{html}"],
                   check=True, capture_output=True)


def solid_base(master: str) -> str:
    """favicon.svg: strip every ink etch group and the faint construction, bolden the frame, add the tile."""
    s = master
    s = re.sub(r"\s*<!--.*?-->", "", s, flags=re.S)
    # drop ink etch groups (stroke=ink) and faint gold construction groups (opacity on gold strokes)
    s = re.sub(r'\s*<g fill="none" stroke="#3d2b1f"[^>]*>.*?</g>', "", s, flags=re.S)
    s = re.sub(r'\s*<g fill="none" stroke="#c9a96e" stroke-width="[0-9.]+" opacity="[0-9.]+"[^>]*>.*?(?:</g>\s*)+?(?=\s*<g fill="none" stroke="#c9a96e" stroke-width="4")', "\n  ", s, count=1, flags=re.S)
    s = s.replace('stroke-width="4" stroke-linejoin="round"', 'stroke-width="5.5" stroke-linejoin="round"')
    # thicken the figure: outline every gold fill with gold
    s = s.replace('<path fill="#c9a96e"', f'<path fill="{GOLD}" stroke="{GOLD}" stroke-width="2.5" stroke-linejoin="round"')
    s = s.replace('<g stroke="#c9a96e" stroke-width="2.3"', '<g stroke="#c9a96e" stroke-width="3.2"')
    # tile + ~8% padding: viewBox grows, figure sits centred
    s = re.sub(r'viewBox="[^"]+"', 'viewBox="-22 -22 284 284"', s, count=1)
    s = s.replace('aria-label="Vitruvian Man">', f'aria-label="Nobodies">\n  <rect x="-22" y="-22" width="284" height="284" fill="{INK}"/>', 1)
    return s


def tile_png(chrome, svg_path, size, pad, out):
    html = os.path.join(tempfile.gettempdir(), "_tile.html")
    with open(html, "w") as f:
        f.write(f'<html><body style="margin:0;background:#000;overflow:hidden">'
                f'<div style="position:absolute;left:0;top:0;width:{size}px;height:{size}px;background:{INK};'
                f'display:flex;align-items:center;justify-content:center">'
                f'<img src="file://{svg_path}" width="{size - 2 * pad}"></div></body></html>')
    chrome_shot(chrome, html, out, max(size, 600), max(size, 600))
    im = Image.open(out).convert("RGB")
    if im.size != (size, size):
        im = im.crop((0, 0, size, size))
    im.save(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--chrome", default=os.environ.get("CHROME", "/opt/pw-browsers/chromium"))
    ap.add_argument("--out", default=os.path.join(os.getcwd(), "logo-review.png"))
    a = ap.parse_args()
    master = os.path.join(WWW, "img", "logo.svg")
    fav_svg = os.path.join(WWW, "img", "favicon.svg")
    with open(master) as f:
        src = f.read()
    with open(fav_svg, "w") as f:
        f.write(solid_base(src))

    tmp = tempfile.mkdtemp()
    big = os.path.join(tmp, "fav256.png")
    tile_png(a.chrome, fav_svg, 256, 0, big)
    im = Image.open(big).convert("RGBA")
    im.save(os.path.join(WWW, "favicon.ico"), sizes=[(16, 16), (32, 32), (48, 48)])

    apple = os.path.join(WWW, "apple-touch-icon.png")
    tile_png(a.chrome, master, 180, 15, apple)
    shutil.copyfile(apple, os.path.join(WWW, "apple-touch-icon-precomposed.png"))
    tile_png(a.chrome, master, 512, 44, os.path.join(WWW, "icon-512.png"))

    # review sheet: master at 280/180/96/64/38 on the navbar colour, favicon.svg at 48/32/16 on white
    html = os.path.join(tmp, "review.html")
    imgs = "".join(f'<img src="file://{master}" width="{w}" style="outline:1px solid #666">' for w in (280, 180, 96, 64, 38))
    favs = "".join(f'<img src="file://{fav_svg}" width="{w}">' for w in (48, 32, 16))
    with open(html, "w") as f:
        f.write(f'<html><body style="margin:0;background:{INK};padding:12px;overflow:hidden;display:flex;align-items:flex-end;gap:14px">'
                f'{imgs}<span style="background:#f4f4f4;padding:8px;display:flex;gap:10px;align-items:flex-end">{favs}</span></body></html>')
    chrome_shot(a.chrome, html, a.out, 900, 380, scale=1)
    print("wrote favicon.svg, favicon.ico, apple-touch-icon(.png/-precomposed.png), icon-512.png; review:", a.out)


if __name__ == "__main__":
    main()
