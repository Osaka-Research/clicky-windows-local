#!/usr/bin/env python3
"""Generates installer branding assets matching Auto's visual identity:
ink ground (#12141A), a single glowing periwinkle cue-dot (#7C8AFF) -- the
same motif as the app's own pointing dot / the homepage's tally-light dot.
No text baked into the images (Inno Setup renders real page titles with
proper font rendering; baking text into a raster image looks cheap)."""

import math
from PIL import Image, ImageDraw, ImageFilter

INK = (0x12, 0x14, 0x1A)
INK_DEEP = (0x0B, 0x0C, 0x10)
CUE = (0x7C, 0x8A, 0xFF)


def vertical_gradient(w, h, top, bottom):
    img = Image.new("RGB", (w, h))
    for y in range(h):
        t = y / max(h - 1, 1)
        row = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
        for x in range(w):
            img.putpixel((x, y), row)
    return img


def glow_dot(size, color, radius_frac=0.16, glow_frac=0.55):
    """A soft-glow filled circle on a transparent layer, radius as a fraction of size."""
    scale = 4  # supersample for a clean edge
    s = size * scale
    layer = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    cx, cy = s / 2, s / 2
    r_core = s * radius_frac
    r_glow = s * glow_frac

    # outer soft glow
    d.ellipse([cx - r_glow, cy - r_glow, cx + r_glow, cy + r_glow],
              fill=(*color, 70))
    layer = layer.filter(ImageFilter.GaussianBlur(s * 0.05))
    d = ImageDraw.Draw(layer)
    # solid core, redrawn crisp after blur
    d.ellipse([cx - r_core, cy - r_core, cx + r_core, cy + r_core],
              fill=(*color, 255))

    return layer.resize((size, size), Image.LANCZOS)


def make_banner(path, w=164, h=314):
    bg = vertical_gradient(w, h, INK, INK_DEEP)
    # dot placed in the lower third, echoing where the app's own cursor dot
    # / the homepage's "tally light" sits relative to its content
    dot_size = int(w * 1.35)
    dot = glow_dot(dot_size, CUE)
    bg = bg.convert("RGBA")
    px = (w - dot_size) // 2
    py = int(h * 0.58) - dot_size // 2
    bg.alpha_composite(dot, (px, py))
    bg.convert("RGB").save(path, format="BMP")


def make_small(path, w=55, h=58):
    bg = Image.new("RGB", (w, h), INK)
    dot_size = int(min(w, h) * 1.6)
    dot = glow_dot(dot_size, CUE)
    bg = bg.convert("RGBA")
    px = (w - dot_size) // 2
    py = (h - dot_size) // 2
    bg.alpha_composite(dot, (px, py))
    bg.convert("RGB").save(path, format="BMP")


def make_icon(path, sizes=(16, 32, 48, 256)):
    imgs = []
    for s in sizes:
        canvas = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        # rounded-square ink tile for larger sizes, plain for tiny ones
        d = ImageDraw.Draw(canvas)
        if s >= 32:
            radius = s * 0.22
            d.rounded_rectangle([0, 0, s - 1, s - 1], radius=radius, fill=(*INK, 255))
        else:
            d.rectangle([0, 0, s - 1, s - 1], fill=(*INK, 255))
        dot_size = int(s * 0.62)
        dot = glow_dot(max(dot_size, 8), CUE, radius_frac=0.30, glow_frac=0.62)
        px = (s - dot.width) // 2
        py = (s - dot.height) // 2
        canvas.alpha_composite(dot, (px, py))
        imgs.append(canvas)
    imgs[0].save(path, format="ICO", sizes=[(im.width, im.height) for im in imgs],
                 append_images=imgs[1:])


if __name__ == "__main__":
    import sys
    outdir = sys.argv[1] if len(sys.argv) > 1 else "."
    make_banner(f"{outdir}/wizard-banner.bmp")
    make_small(f"{outdir}/wizard-small.bmp")
    make_icon(f"{outdir}/setup.ico")
    print("done")
