"""Renders an SVG at several sizes in headless Edge and lays them out on light and dark, zoomed, for review."""
import json
import os
import subprocess
import sys
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))


def render(jobs):
    path = os.path.join(HERE, 'jobs.json')
    with open(path, 'w', encoding='utf-8') as fh:
        json.dump(jobs, fh)
    subprocess.run(['node', os.path.join(HERE, 'render.mjs'), path], check=True, capture_output=True)


def sheet(svg_by_size, out, sizes=(16, 20, 24, 32, 48, 64, 256), zoom_small=6):
    """svg_by_size: size -> svg path (lets small sizes use the simplified art)."""
    jobs, files = [], {}
    for s in sizes:
        png = os.path.join(HERE, f'r{s}.png')
        jobs.append({'src': svg_by_size(s), 'out': png, 'w': s, 'h': s})
        files[s] = png
    render(jobs)
    pad = 16
    # Row 1: actual size on light and dark. Row 2: small sizes zoomed (nearest) so pixels can be judged.
    width = pad + sum(s + pad for s in sizes)
    zoomed = [s for s in sizes if s <= 48]
    zoom_w = pad + sum(s * zoom_small + pad for s in zoomed)
    W = max(width, zoom_w)
    H = pad + 256 + pad + 256 + pad + max(s * zoom_small for s in zoomed) + pad + max(s * zoom_small for s in zoomed) + pad
    img = Image.new('RGBA', (W, H), (255, 255, 255, 255))
    dark = (32, 34, 38, 255)
    y_light, y_dark = pad, pad + 256 + pad
    img.paste(Image.new('RGBA', (W, 256 + pad), dark), (0, y_dark - pad // 2))
    x = pad
    for s in sizes:
        im = Image.open(files[s]).convert('RGBA')
        img.alpha_composite(im, (x, y_light + 256 - s))
        img.alpha_composite(im, (x, y_dark + 256 - s))
        x += s + pad
    zh = max(s * zoom_small for s in zoomed)
    y_zl = y_dark + 256 + pad
    y_zd = y_zl + zh + pad
    img.paste(Image.new('RGBA', (W, zh + pad), dark), (0, y_zd - pad // 2))
    x = pad
    for s in zoomed:
        im = Image.open(files[s]).convert('RGBA').resize((s * zoom_small, s * zoom_small), Image.NEAREST)
        img.alpha_composite(im, (x, y_zl))
        img.alpha_composite(im, (x, y_zd))
        x += s * zoom_small + pad
    img.save(out)
    return files


if __name__ == '__main__':
    big = sys.argv[1]
    small = sys.argv[2] if len(sys.argv) > 2 and sys.argv[2] != '-' else big
    out = sys.argv[3] if len(sys.argv) > 3 else os.path.join(HERE, 'sheet.png')
    sheet(lambda s: small if s <= 24 else big, out)
    print(out)
