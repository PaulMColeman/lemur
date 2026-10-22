"""Builds the final Lemur assets from the generated SVGs: PNGs per size, the ICO, WiX bitmaps and a preview sheet."""
import io
import os
import re
import shutil
import struct
import sys
from PIL import Image, ImageDraw, ImageFont
import review

SRC = os.path.abspath(sys.argv[1])                      # folder with the generated SVGs (no width/height: they fill the viewport)
ASSETS = os.path.abspath(sys.argv[2])                   # repo assets folder
PNG = os.path.join(ASSETS, 'png')
WORK = os.path.join(SRC, 'final')
os.makedirs(PNG, exist_ok=True)
os.makedirs(WORK, exist_ok=True)

ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
ART = {  # which drawing each size uses
    'lemur-icon-16.svg': [16],
    'lemur-icon-small.svg': [20, 24],
    'lemur-icon-face.svg': [32, 40, 48, 64],
    'lemur-icon.svg': [128, 256],
}
# Generated names -> final names
RENAME = {'lemur-icon-tail.svg': 'lemur-icon.svg', 'lemur-icon.svg': 'lemur-icon-face.svg',
          'lemur-icon-small.svg': 'lemur-icon-small.svg', 'lemur-icon-16.svg': 'lemur-icon-16.svg',
          'lemur-logo.svg': 'lemur-logo.svg'}
TITLES = {'lemur-icon.svg': 'Lemur', 'lemur-icon-face.svg': 'Lemur', 'lemur-icon-small.svg': 'Lemur',
          'lemur-icon-16.svg': 'Lemur', 'lemur-logo.svg': 'Lemur — LLM Meeting Recorder'}

# 1. Copy the SVGs under their final names into the work folder (for rendering) and into assets (with size + title).
for gen_name, final in RENAME.items():
    text = open(os.path.join(SRC, gen_name), encoding='utf-8').read()
    open(os.path.join(WORK, final), 'w', encoding='utf-8', newline='\n').write(text)
    view = re.search(r'viewBox="([^"]+)"', text).group(1).split()
    w, h = float(view[2]), float(view[3])
    if final == 'lemur-icon-16.svg':
        w = h = 16
    elif final.startswith('lemur-icon'):
        w = h = 256
    sized = text.replace('<svg xmlns="http://www.w3.org/2000/svg"',
                         f'<svg xmlns="http://www.w3.org/2000/svg" width="{w:g}" height="{h:g}" role="img" aria-label="{TITLES[final]}"', 1)
    sized = re.sub(r'(<svg[^>]*>)', r'\1\n  <title>' + TITLES[final] + '</title>', sized, count=1)
    open(os.path.join(ASSETS, final), 'w', encoding='utf-8', newline='\n').write(sized)

# 2. Render every icon size natively from its drawing, and the logo.
jobs = []
for art, sizes in ART.items():
    for s in sizes:
        jobs.append({'src': os.path.join(WORK, art), 'out': os.path.join(PNG, f'lemur-icon-{s}.png'), 'w': s, 'h': s})
logo_w, logo_h = 356, 270
for width in (128, 512):
    jobs.append({'src': os.path.join(WORK, 'lemur-logo.svg'), 'out': os.path.join(PNG, f'lemur-logo-{width}.png'),
                 'w': width, 'h': round(width * logo_h / logo_w)})
jobs.append({'src': os.path.join(WORK, 'lemur-logo.svg'), 'out': os.path.join(WORK, 'logo-banner.png'),
             'w': round(46 * logo_w / logo_h), 'h': 46})
jobs.append({'src': os.path.join(WORK, 'lemur-logo.svg'), 'out': os.path.join(WORK, 'logo-panel.png'),
             'w': 132, 'h': round(132 * logo_h / logo_w)})
review.render(jobs)


# 3. The ICO: <= 64 px as 32-bit DIBs (BITMAPINFOHEADER, doubled height, bottom-up, AND mask); 128 and 256 as PNG.
def dib(img):
    w, h = img.size
    px = img.load()
    xor = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = px[x, y]
            xor += bytes((b, g, r, a))
    row_bytes = ((w + 31) // 32) * 4
    mask = bytearray()
    for y in range(h - 1, -1, -1):
        row = bytearray(row_bytes)
        for x in range(w):
            if px[x, y][3] == 0:
                row[x // 8] |= 0x80 >> (x % 8)
        mask += row
    header = struct.pack('<IiiHHIIiiII', 40, w, h * 2, 1, 32, 0, len(xor) + len(mask), 0, 0, 0, 0)
    return header + bytes(xor) + bytes(mask)


entries = []
for s in ICON_SIZES:
    img = Image.open(os.path.join(PNG, f'lemur-icon-{s}.png')).convert('RGBA')
    assert img.size == (s, s), (s, img.size)
    if s >= 128:
        buf = io.BytesIO()
        img.save(buf, 'PNG', optimize=True)
        entries.append((s, buf.getvalue()))
    else:
        entries.append((s, dib(img)))
directory, data = b'', b''
offset = 6 + 16 * len(entries)
for s, blob in entries:
    dim = 0 if s >= 256 else s
    directory += struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(blob), offset + len(data))
    data += blob
with open(os.path.join(ASSETS, 'lemur.ico'), 'wb') as fh:
    fh.write(struct.pack('<HHH', 0, 1, len(entries)) + directory + data)


# 4. WiX bitmaps (24-bit BMP).
def font(size, bold=False):
    for name in (('segoeuib.ttf' if bold else 'segoeui.ttf'), 'arial.ttf'):
        try:
            return ImageFont.truetype(os.path.join(os.environ.get('WINDIR', 'C:/Windows'), 'Fonts', name), size)
        except OSError:
            continue
    return ImageFont.load_default(size=size)


def vertical_gradient(w, h, top, bottom):
    top = tuple(int(top[i:i + 2], 16) for i in (1, 3, 5))
    bottom = tuple(int(bottom[i:i + 2], 16) for i in (1, 3, 5))
    img = Image.new('RGBA', (w, h))
    d = ImageDraw.Draw(img)
    for y in range(h):
        t = y / max(1, h - 1)
        d.line([(0, y), (w, y)], fill=tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
    return img


banner = Image.new('RGBA', (493, 58), (255, 255, 255, 255))
mark = Image.open(os.path.join(WORK, 'logo-banner.png')).convert('RGBA')
banner.alpha_composite(mark, (493 - mark.width - 12, (58 - mark.height) // 2))
ImageDraw.Draw(banner).line([(0, 57), (493, 57)], fill=(222, 226, 230, 255))
banner.convert('RGB').save(os.path.join(ASSETS, 'wix-banner.bmp'))

dialog = Image.new('RGBA', (493, 312), (255, 255, 255, 255))
panel = vertical_gradient(164, 312, '#17767D', '#0B4A50')
logo = Image.open(os.path.join(WORK, 'logo-panel.png')).convert('RGBA')
panel.alpha_composite(logo, ((164 - logo.width) // 2, 62))
d = ImageDraw.Draw(panel)
title, sub = 'Lemur', 'LLM Meeting Recorder'
f_title, f_sub = font(30, bold=True), font(12)
tw = d.textlength(title, font=f_title)
d.text(((164 - tw) / 2, 62 + logo.height + 18), title, font=f_title, fill=(255, 255, 255, 255))
sw = d.textlength(sub, font=f_sub)
d.text(((164 - sw) / 2, 62 + logo.height + 58), sub, font=f_sub, fill=(207, 232, 233, 255))
dialog.alpha_composite(panel, (0, 0))
dialog.convert('RGB').save(os.path.join(ASSETS, 'wix-dialog.bmp'))

# 5. Preview sheet: every icon size at 1:1 on light and dark, the small ones zoomed, and the logo.
light, dark = (255, 255, 255, 255), (30, 32, 36, 255)
show = [16, 20, 24, 32, 48, 64, 256]
pad = 24
row_h = 256 + 40
zoom = 8
zoomed = [16, 20, 24, 32]
zoom_h = max(zoomed) * zoom + 40
logo_big = Image.open(os.path.join(PNG, 'lemur-logo-512.png')).convert('RGBA').resize((340, round(340 * logo_h / logo_w)), Image.LANCZOS)
logo_small = Image.open(os.path.join(PNG, 'lemur-logo-128.png')).convert('RGBA')
W = max(pad + sum(s + pad for s in show), pad + sum(s * zoom + pad for s in zoomed), 2 * (logo_big.width + logo_small.width + 3 * pad))
H = 2 * row_h + 2 * zoom_h + logo_big.height + 2 * pad + 40
sheet = Image.new('RGBA', (W, H), light)
d = ImageDraw.Draw(sheet)
label = font(13)
y = 0
for bg, name in ((light, 'light'), (dark, 'dark')):
    sheet.paste(Image.new('RGBA', (W, row_h), bg), (0, y))
    x = pad
    for s in show:
        im = Image.open(os.path.join(PNG, f'lemur-icon-{s}.png')).convert('RGBA')
        sheet.alpha_composite(im, (x, y + 16 + 256 - s))
        d.text((x, y + 16 + 256 + 4), f'{s}', font=label, fill=(110, 110, 110, 255) if bg == light else (170, 170, 170, 255))
        x += s + pad
    y += row_h
for bg in (light, dark):
    sheet.paste(Image.new('RGBA', (W, zoom_h), bg), (0, y))
    x = pad
    for s in zoomed:
        im = Image.open(os.path.join(PNG, f'lemur-icon-{s}.png')).convert('RGBA').resize((s * zoom, s * zoom), Image.NEAREST)
        sheet.alpha_composite(im, (x, y + 16))
        d.text((x, y + 16 + s * zoom + 4), f'{s} px x{zoom}', font=label, fill=(110, 110, 110, 255) if bg == light else (170, 170, 170, 255))
        x += s * zoom + pad
    y += zoom_h
half = W // 2
sheet.paste(Image.new('RGBA', (W - half, H - y), dark), (half, y))
for x0, bg in ((0, light), (half, dark)):
    sheet.alpha_composite(logo_big, (x0 + pad, y + pad))
    sheet.alpha_composite(logo_small, (x0 + pad + logo_big.width + pad, y + pad + logo_big.height - logo_small.height))
sheet.convert('RGB').save(os.path.join(ASSETS, 'preview.png'))
print('done')
