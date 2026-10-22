"""Generates the Lemur SVGs (icon badge, small icon, logo) from a few geometric parameters."""
import math
import os
import sys

OUT = sys.argv[1] if len(sys.argv) > 1 else '.'

# Palette
BG_TOP, BG_BOTTOM = '#17767D', '#0B4A50'      # deep teal badge
EAR, EAR_IN = '#4B535C', '#EEF1F4'            # dark grey ear, white fur inside
FUR = '#AAB4BE'                               # grey head fur (outline that reads on white pages)
MASK = '#F6F8FA'                              # white face
CROWN = '#454C54'                             # dark crown cap
PATCH = '#16191C'                             # black eye patches + nose
IRIS, IRIS_RIM = '#FFAE1A', '#E07B00'         # amber eyes
PUPIL = '#16191C'
TAIL_LIGHT, TAIL_DARK = '#EEF1F4', '#16191C'


def f(v):
    return f'{v:.2f}'.rstrip('0').rstrip('.')


def teardrop(cx, cy, r, px, py):
    """A circle (cx, cy, r) joined to a point (px, py) by its two tangents: path data."""
    dx, dy = cx - px, cy - py
    d = math.hypot(dx, dy)
    a = math.asin(r / d)
    base = math.atan2(dy, dx)
    length = math.sqrt(d * d - r * r)
    t1 = (px + length * math.cos(base - a), py + length * math.sin(base - a))
    t2 = (px + length * math.cos(base + a), py + length * math.sin(base + a))
    return (f'M{f(px)},{f(py)} L{f(t1[0])},{f(t1[1])} '
            f'A{f(r)},{f(r)} 0 1 1 {f(t2[0])},{f(t2[1])} Z')


def mirror_points(points):
    return [(256 - x, y) for x, y in points]


def poly(points, fill, round_px=0):
    pts = ' '.join(f'{f(x)},{f(y)}' for x, y in points)
    stroke = f' stroke="{fill}" stroke-width="{round_px}" stroke-linejoin="round"' if round_px else ''
    return f'<polygon points="{pts}" fill="{fill}"{stroke}/>'


def face(p):
    """The lemur's face in a 256 x 256 box. p: dict of geometry."""
    out = []
    for pts, inner in ((p['ear'], p['ear_in']), (mirror_points(p['ear']), mirror_points(p['ear_in']))):
        out.append(poly(pts, EAR, p['ear_round']))
        out.append(poly(inner, EAR_IN, p['ear_in_round']))
    if p.get('head'):
        out.append(f'<path d="{p["head"]}" fill="{FUR}"/>')
    out.append(f'<path d="{p["mask"]}" fill="{MASK}"/>')
    if p.get('crown'):
        out.append(f'<path d="{p["crown"]}" fill="{CROWN}"/>')
    ex, ey, er = p['eye']
    tx, ty = p['patch_tip']
    for sx in (1, -1):
        cx = ex if sx == 1 else 256 - ex
        tip = tx if sx == 1 else 256 - tx
        out.append(f'<path d="{teardrop(cx, ey, p["patch_r"], tip, ty)}" fill="{PATCH}" stroke="{PATCH}" '
                   f'stroke-width="{p["patch_round"]}" stroke-linejoin="round"/>')
        out.append(f'<circle cx="{f(cx)}" cy="{f(ey)}" r="{f(er)}" fill="{IRIS}"/>')
        if p.get('iris_rim'):
            out.append(f'<circle cx="{f(cx)}" cy="{f(ey)}" r="{f(er - p["iris_rim"] / 2)}" fill="none" '
                       f'stroke="{IRIS_RIM}" stroke-width="{f(p["iris_rim"])}"/>')
        if p.get('pupil'):
            px_off, py_off, pr = p['pupil']
            out.append(f'<circle cx="{f(cx + px_off * sx)}" cy="{f(ey + py_off)}" r="{f(pr)}" fill="{PUPIL}"/>')
        if p.get('glint'):
            gx, gy, gr = p['glint']
            out.append(f'<circle cx="{f(cx + gx)}" cy="{f(ey + gy)}" r="{f(gr)}" fill="#FFFFFF"/>')
    out.append(poly(p['nose'], PATCH, p['nose_round']))
    return '\n  '.join(out)


def tail(d, width, ring, light=TAIL_LIGHT, dark=TAIL_DARK, offset=0):
    """A ringed tail along path d: a light stroke with dark dashes across it (dashes are cut square to the path)."""
    return (f'<path d="{d}" fill="none" stroke="{light}" stroke-width="{f(width)}" stroke-linecap="round"/>\n  '
            f'<path d="{d}" fill="none" stroke="{dark}" stroke-width="{f(width)}" stroke-linecap="butt" '
            f'stroke-dasharray="{f(ring)} {f(ring)}" stroke-dashoffset="{f(offset)}"/>')


BIG = dict(
    ear=[(40, 30), (108, 70), (62, 128)], ear_round=14,
    ear_in=[(54, 48), (94, 76), (66, 110)], ear_in_round=6,
    head='M128,60 C170,60 202,84 213,118 C222,144 226,166 210,184 C192,204 156,216 128,234 '
         'C100,216 64,204 46,184 C30,166 34,144 43,118 C54,84 86,60 128,60 Z',
    mask='M128,92 C164,92 192,110 202,136 C209,156 206,172 192,184 C174,200 150,210 128,226 '
         'C106,210 82,200 64,184 C50,172 47,156 54,136 C64,110 92,92 128,92 Z',
    crown='M66,104 C78,78 102,66 128,66 C154,66 178,78 190,104 C170,98 148,99 128,112 C108,99 86,98 66,104 Z',
    eye=(92, 148, 17.5), patch_r=24, patch_tip=(116, 194), patch_round=6,
    iris_rim=2.5, pupil=(1.5, 1, 8), glint=(-5.5, -6, 4),
    nose=[(114, 202), (142, 202), (128, 217)], nose_round=9,
)

# Drawn for 20-24 px: no fur rim or crown, bigger eyes, fewer shapes.
SMALL = dict(
    ear=[(28, 20), (110, 72), (46, 130)], ear_round=16,
    ear_in=[(44, 46), (90, 80), (56, 108)], ear_in_round=8,
    head=None,
    mask='M128,62 C176,62 214,92 222,140 C228,172 210,196 180,210 C160,220 144,226 128,240 '
         'C112,226 96,220 76,210 C46,196 28,172 34,140 C42,92 80,62 128,62 Z',
    crown=None,
    eye=(84, 146, 22), patch_r=31, patch_tip=(113, 202), patch_round=8,
    iris_rim=0, pupil=(1.5, 1.5, 9.5), glint=None,
    nose=[(110, 208), (146, 208), (128, 228)], nose_round=12,
)

# 16 px: pixel art on the 16 x 16 grid (each string is a row; . = clear, T = badge).
PIXELS_16 = [
    "..TTTTTTTTTTTT..",
    ".TETTTTTTTTTTET.",
    "TTEETTTTTTTTEETT",
    "TTEWETTTTTTEWETT",
    "TTEWWGGGGGGWWETT",
    "TTWWWWWWWWWWWWTT",
    "TWWKKKWWWWKKKWWT",
    "TWKAAAKWWKAAAKWT",
    "TWKAPAKWWKAPAKWT",
    "TWKAAAKWWKAAAKWT",
    "TTWKKKKWWKKKKWTT",
    "TTTWWKKWWKKWWTTT",
    "TTTTWWWKKWWWTTTT",
    "TTTTTWWKKWWTTTTT",
    ".TTTTTTWWTTTTTT.",
    "..TTTTTTTTTTTT..",
]


def badge(inner, rx=54):
    return (f'<defs><linearGradient id="bg" x1="0" y1="0" x2="0" y2="1">'
            f'<stop offset="0" stop-color="{BG_TOP}"/><stop offset="1" stop-color="{BG_BOTTOM}"/></linearGradient></defs>\n'
            f'  <rect x="8" y="8" width="240" height="240" rx="{rx}" fill="url(#bg)"/>\n  {inner}')


def svg(body, view='0 0 256 256'):
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{view}">\n  {body}\n</svg>\n'


def scaled(inner, s, dx, dy):
    return f'<g transform="translate({f(dx)} {f(dy)}) scale({f(s)})">\n  {inner}\n  </g>'


def write(name, text):
    with open(os.path.join(OUT, name), 'w', encoding='utf-8', newline='\n') as fh:
        fh.write(text)


PIXEL_COLOURS = {'E': EAR, 'W': MASK, 'G': CROWN, 'K': PATCH, 'A': IRIS, 'P': PUPIL}


def pixel_svg(rows):
    """Pixel art as crisp rects; the badge is one rounded rect behind, so its corners match the bigger sizes."""
    n = len(rows)
    out = [f'<defs><linearGradient id="bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{BG_TOP}"/>'
           f'<stop offset="1" stop-color="{BG_BOTTOM}"/></linearGradient></defs>',
           f'<rect x="0" y="0" width="{n}" height="{n}" rx="3.2" fill="url(#bg)"/>']
    for y, row in enumerate(rows):
        x = 0
        while x < n:
            c = row[x]
            run = 1
            while x + run < n and row[x + run] == c:
                run += 1
            if c in PIXEL_COLOURS:
                out.append(f'<rect x="{x}" y="{y}" width="{run}" height="1" fill="{PIXEL_COLOURS[c]}"/>')
            x += run
    nl = '\n  '
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {n} {n}" shape-rendering="crispEdges">{nl}'
            + nl.join(out) + '\n</svg>\n')


if __name__ == '__main__':
    write('lemur-icon.svg', svg(badge(face(BIG))))
    write('lemur-icon-small.svg', svg(badge(face(SMALL), rx=56)))
    # Variant: the face a little smaller, with the ringed tail curling up behind it on the right.
    tail_d = 'M96,228 C170,240 232,208 232,140 C232,98 226,66 206,54 C190,45 176,52 178,66'
    write('lemur-icon-tail.svg', svg(badge(tail(tail_d, 21, 12.5, offset=4) + '\n  ' + scaled(face(BIG), 0.80, 6, 36))))
    write('lemur-icon-16.svg', pixel_svg(PIXELS_16))
    logo_tail = 'M196,262 C268,276 344,240 350,166 C355,110 338,66 304,54 C278,46 258,58 260,80'
    nl = '\n  '
    logo = (f'<path d="{logo_tail}" fill="none" stroke="{FUR}" stroke-width="34" stroke-linecap="round"/>{nl}'
            + tail(logo_tail, 26, 15, light='#F1F4F6', offset=2) + nl
            + scaled(face(BIG), 1, 0, 30))
    write('lemur-logo-raw.svg', svg(logo, view='0 0 400 320'))
    write('lemur-logo.svg', svg(logo, view='22 22 356 270'))
    print('written')
