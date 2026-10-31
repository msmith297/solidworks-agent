#!/usr/bin/env python3
"""Draws the SolidWorks Agent brand images: a red cube seen from just above one corner, a heavy "S" on the left face and a flat-topped "AI" on the right.

    python tools/dev/brand/make_brand.py          # assets/brand and ui/public/brand
    python tools/dev/brand/make_brand.py OUTDIR   # a trial copy

Fonts come from Windows (Segoe UI, Georgia). Small sizes get larger letters so they stay readable.
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

FONTS = Path(r"C:\Windows\Fonts")
SANS_HEAVY = FONTS / "segoeuib.ttf"
SERIF = FONTS / "georgia.ttf"

RED_LIGHT, RED_MID, RED_DARK = (240, 78, 62), (214, 30, 26), (150, 16, 22)
WHITE, IVORY = (255, 255, 255), (250, 240, 224)
GRAPHITE = (43, 46, 51, 255)
MARK_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256, 512]
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def glyph(text, font_path, height):
    """The text's ink as an L mask, cropped tight, cap height = height."""
    font = ImageFont.truetype(str(font_path), 800)
    canvas = Image.new("L", (2400, 1400), 0)
    ImageDraw.Draw(canvas).text((100, 100), text, font=font, fill=255)
    ink = canvas.crop(canvas.getbbox())
    return ink.resize((max(1, round(ink.width * height / ink.height)), height), Image.LANCZOS)


def on_face(img, texture, origin, u_end, v_end):
    """Paste an RGBA texture onto a cube face: its top-left at origin, top-right at u_end, bottom-left at v_end."""
    ux, uy = (u_end[0] - origin[0]) / texture.width, (u_end[1] - origin[1]) / texture.width
    vx, vy = (v_end[0] - origin[0]) / texture.height, (v_end[1] - origin[1]) / texture.height
    det = ux * vy - uy * vx
    # Image.transform maps output to input, so the face's axes are inverted.
    a, b = vy / det, -vx / det
    d, e = -uy / det, ux / det
    c = -(a * origin[0] + b * origin[1])
    f = -(d * origin[0] + e * origin[1])
    warped = texture.transform(img.size, Image.AFFINE, (a, b, c, d, e, f), Image.BICUBIC)
    img.alpha_composite(warped)


def ai_letters(height):
    """The "AI": a heavy flat-topped A and an I leaning along with it, drawn as shapes, not from a font."""
    h = height
    width = round(h * 1.5)
    mask = Image.new("L", (width, h), 0)
    d = ImageDraw.Draw(mask)
    pts = lambda *ps: [(x * h, y * h) for x, y in ps]
    d.polygon(pts((0, 1), (0.37, 0), (0.63, 0), (1, 1), (0.72, 1), (0.64, 0.77), (0.36, 0.77), (0.28, 1)), fill=255)
    d.polygon(pts((0.5, 0.25), (0.42, 0.53), (0.58, 0.53)), fill=0)
    d.polygon(pts((1.06, 0), (1.32, 0), (1.5, 1), (1.24, 1)), fill=255)
    return mask


def face_letters(mask, colour, fill_w, fill_h, width, height):
    """A face-sized texture with the mask centred, as large as fill_w x fill_h of it allows."""
    scale = min(width * fill_w / mask.width, height * fill_h / mask.height)
    mask = mask.resize((max(1, round(mask.width * scale)), max(1, round(mask.height * scale))), Image.LANCZOS)
    tex = Image.new("RGBA", (width, height), colour + (0,))
    tex.paste(Image.new("RGBA", mask.size, colour + (255,)), ((width - mask.width) // 2, (height - mask.height) // 2), mask)
    return tex


def mark(size):
    """One mark at `size` px: a cube seen from just above one corner, S on the left face and AI on the right."""
    small = size <= 32
    n = 2048
    dx, dt, tall = n * 0.445, n * 0.125, n * 0.66
    cx, y = n / 2, (n - 2 * dt - tall) / 2
    top, bottom = (cx, y), (cx, y + 2 * dt + tall)
    upper_left, upper_right = (cx - dx, y + dt), (cx + dx, y + dt)
    lower_left, lower_right = (cx - dx, y + dt + tall), (cx + dx, y + dt + tall)
    centre = (cx, y + 2 * dt)

    # The faces meet along lines through the near corner, so growing each around it keeps those edges exact; the
    # rounded outline then trims the outside in one clean line.
    def grown(poly):
        return [(centre[0] + (px - centre[0]) * 1.1, centre[1] + (py - centre[1]) * 1.1) for px, py in poly]

    faces = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(faces)
    d.polygon(grown([top, upper_right, centre, upper_left]), fill=RED_LIGHT + (255,))
    d.polygon(grown([upper_left, centre, bottom, lower_left]), fill=RED_MID + (255,))
    d.polygon(grown([centre, upper_right, lower_right, bottom]), fill=RED_DARK + (255,))
    outline = [top, upper_right, lower_right, bottom, lower_left, upper_left]
    silhouette = Image.new("L", (n, n), 0)
    sd = ImageDraw.Draw(silhouette)
    sd.polygon(outline, fill=255)
    sd.line(outline + outline[:2], fill=255, width=int(n * 0.03), joint="curve")
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    img.paste(faces, (0, 0), silhouette)

    # Face textures keep the face's proportions, so the letters aren't squeezed.
    w = 1024
    h = round(w * tall / dx)
    # The AI's width sets the letter height; the S is a touch taller so the two read at one scale.
    ai = ai_letters(1000)
    letter_h = round(w * (0.92 if small else 0.84) * ai.height / ai.width)
    on_face(img, face_letters(glyph("S", SANS_HEAVY, 1000), WHITE, 1, letter_h * 1.2 / h, w, h), upper_left, centre, lower_left)
    on_face(img, face_letters(ai, IVORY, 1, letter_h / h, w, h), centre, upper_right, bottom)
    return img.resize((size, size), Image.LANCZOS)


def wordmark(height=335):
    """ "SolidWorks Agent": graphite ink (dark themes lighten it in CSS). """
    sans = ImageFont.truetype(str(SANS_HEAVY), 400)
    serif = ImageFont.truetype(str(SERIF), 400)
    first, second = "SolidWorks", "Agent"
    canvas = Image.new("RGBA", (4200, 800), (0, 0, 0, 0))
    d = ImageDraw.Draw(canvas)
    d.text((60, 120), first, font=sans, fill=GRAPHITE)
    x = 60 + d.textlength(first, font=sans) + 110
    # Same baseline for both fonts.
    baseline = 120 + sans.getmetrics()[0]
    d.text((x, baseline - serif.getmetrics()[0]), second, font=serif, fill=GRAPHITE)
    ink = canvas.crop(canvas.getbbox())
    return ink.resize((round(ink.width * height / ink.height), height), Image.LANCZOS)


def lockup(word):
    m = mark(512)
    h = 550
    m = m.resize((h, h), Image.LANCZOS)
    w = word.resize((round(word.width * 250 / word.height), 250), Image.LANCZOS)
    out = Image.new("RGBA", (h + 70 + w.width, h), (0, 0, 0, 0))
    out.paste(m, (0, 0), m)
    out.paste(w, (h + 70, (h - w.height) // 2), w)
    return out


def main():
    repo = Path(__file__).resolve().parents[3]
    assets = Path(sys.argv[1]) if len(sys.argv) > 1 else repo / "assets" / "brand"
    public = Path(sys.argv[1]) / "public" if len(sys.argv) > 1 else repo / "ui" / "public" / "brand"
    assets.mkdir(parents=True, exist_ok=True)
    public.mkdir(parents=True, exist_ok=True)
    for size in MARK_SIZES:
        img = mark(size)
        img.save(assets / f"mark-{size}.png")
        img.save(public / f"mark-{size}.png")
    word = wordmark()
    word.save(assets / "wordmark.png")
    word.save(public / "wordmark.png")
    lockup(word).save(assets / "logo-full.png")
    big = mark(256)
    big.save(assets / "solidworks-agent.ico", sizes=[(s, s) for s in ICO_SIZES],
             append_images=[mark(s) for s in ICO_SIZES if s != 256])


if __name__ == "__main__":
    main()
