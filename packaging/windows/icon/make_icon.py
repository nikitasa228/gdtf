#!/usr/bin/env python3
"""Draws the Candela application icon: a stage light throwing an orange beam onto a pool of light.

Writes candela.ico (16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 px; Pillow stores the large image PNG-compressed, as
Windows expects) and candela-256.png into the output folder (default: next to this script). Every size is rendered
from the same vector drawing at 8x and downsampled, so the small sizes stay crisp. Requires Pillow.

Usage: python3 make_icon.py [output-dir]
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFilter

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
SUPERSAMPLE = 8

BACKGROUND_TOP = (44, 47, 54)
BACKGROUND_BOTTOM = (18, 19, 22)
BODY = (70, 75, 86)
BODY_EDGE = (128, 134, 148)
LENS = (255, 236, 205)
BEAM = (255, 122, 0)  # the editor's accent colour (#FF7A00)
BEAM_CORE = (255, 186, 92)
POOL = (255, 150, 50)


def lerp(a, b, t):
    return tuple(int(round(x + (y - x) * t)) for x, y in zip(a, b))


def render(size):
    s = size * SUPERSAMPLE
    u = s / 256.0  # drawing units: a 256 x 256 design grid

    def box(x0, y0, x1, y1):
        return [x0 * u, y0 * u, x1 * u, y1 * u]

    # Rounded square with a vertical gradient.
    gradient = Image.new("RGBA", (s, s))
    draw = ImageDraw.Draw(gradient)
    for y in range(s):
        draw.line([(0, y), (s, y)], fill=lerp(BACKGROUND_TOP, BACKGROUND_BOTTOM, y / (s - 1)) + (255,))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle(box(8, 8, 248, 248), radius=52 * u, fill=255)
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    image.paste(gradient, (0, 0), mask)

    # Beam: a cone from the lens down to the floor, fading towards the floor, with a brighter core.
    beam = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(beam)
    top, bottom = 96, 212
    for y in range(int(top * u), int(bottom * u)):
        t = (y / u - top) / (bottom - top)
        half = (22 + (88 - 22) * t) * u
        core = (10 + (40 - 10) * t) * u
        alpha = int(225 * (1 - 0.55 * t))
        draw.line([(128 * u - half, y), (128 * u + half, y)], fill=BEAM + (alpha,))
        draw.line([(128 * u - core, y), (128 * u + core, y)], fill=BEAM_CORE + (min(255, int(alpha * 1.08)),))
    beam = beam.filter(ImageFilter.GaussianBlur(radius=1.5 * u))
    image = Image.alpha_composite(image, beam)

    # Pool of light on the floor.
    pool = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(pool)
    draw.ellipse(box(30, 196, 226, 230), fill=POOL + (235,))
    draw.ellipse(box(76, 203, 180, 223), fill=BEAM_CORE + (255,))
    pool = pool.filter(ImageFilter.GaussianBlur(radius=2.0 * u))
    image = Image.alpha_composite(image, pool)

    # The lamp: a head hanging from a short hook, its lens facing down.
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle(box(118, 30, 138, 52), radius=4 * u, fill=BODY_EDGE)
    draw.rounded_rectangle(box(84, 44, 172, 100), radius=18 * u, fill=BODY, outline=BODY_EDGE, width=max(1, int(5 * u)))
    draw.ellipse(box(100, 84, 156, 108), fill=LENS)

    # Keep everything inside the rounded square.
    clipped = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    clipped.paste(image, (0, 0), mask)
    return clipped.resize((size, size), Image.LANCZOS)


def wizard_image(width=164, height=314):
    """The installer's welcome / finish page picture: the icon's drawing on the dark background, larger, upper half."""
    image = Image.new("RGB", (width, height), BACKGROUND_BOTTOM)
    draw = ImageDraw.Draw(image)
    for y in range(height):
        draw.line([(0, y), (width, y)], fill=lerp(BACKGROUND_TOP, BACKGROUND_BOTTOM, y / (height - 1)))
    art = render(144)
    image.paste(art, ((width - 144) // 2, 60), art)
    return image


def header_image(width=150, height=57):
    """The installer's page header picture (right-aligned, on the white header): the 48 px icon at the right edge."""
    image = Image.new("RGB", (width, height), (255, 255, 255))
    art = render(48)
    image.paste(art, (width - 48 - 6, (height - 48) // 2), art)
    return image


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
    os.makedirs(out, exist_ok=True)
    images = {size: render(size) for size in SIZES}
    largest = images[SIZES[-1]]
    largest.save(os.path.join(out, "candela-256.png"))
    # Pillow writes each requested size from the image of that size when it is among the appended ones.
    largest.save(os.path.join(out, "candela.ico"), format="ICO", sizes=[(s, s) for s in SIZES],
                 append_images=[images[s] for s in SIZES[:-1]])
    # NSIS wants 24-bit BMPs for the Modern UI pictures.
    wizard_image().save(os.path.join(out, "installer-wizard.bmp"), format="BMP")
    header_image().save(os.path.join(out, "installer-header.bmp"), format="BMP")
    print("wrote", os.path.join(out, "candela.ico"), "and the installer pictures")


if __name__ == "__main__":
    main()
