"""Renders the GearGlass icon: an amber gear with a smoked-glass lens in the middle."""
import math
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

SS = 8  # supersampling factor
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(len(a)))


def vertical_gradient(size, top, bottom):
    grad = Image.new("RGBA", (1, size))
    for y in range(size):
        grad.putpixel((0, y), lerp(top, bottom, y / max(1, size - 1)))
    return grad.resize((size, size))


def gear_points(c, r_root, r_tip, teeth):
    # Same tooth profile as MainWindow.BuildGear, scaled.
    profile = [(-0.5, 0), (-0.34, 0), (-0.27, 1), (-0.14, 1), (0.14, 1), (0.27, 1), (0.34, 0), (0.5, 0)]
    pts = []
    for tooth in range(teeth):
        for offset, tip in profile:
            a = ((tooth + offset) / teeth) * math.tau - math.pi / 2
            r = r_tip if tip else r_root
            pts.append((c + math.cos(a) * r, c + math.sin(a) * r))
    return pts


def mask_from(draw_fn, n):
    m = Image.new("L", (n, n), 0)
    draw_fn(ImageDraw.Draw(m))
    return m


def render(size):
    n = size * SS
    c = n / 2
    small = size <= 24
    teeth = 8 if small else 12
    r_tip = n * 0.485
    r_root = n * (0.385 if small else 0.40)
    r_lens = n * (0.235 if small else 0.255)
    rim = n * (0.045 if small else 0.035)

    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))

    # Gear body: warm brass gradient, dark outline for contrast on light taskbars.
    gear = mask_from(lambda d: d.polygon(gear_points(c, r_root, r_tip, teeth), fill=255), n)
    outline = gear.filter(ImageFilter.MaxFilter(max(3, (SS * (1 if small else 2)) | 1)))
    img.paste((46, 34, 24, 255), (0, 0), outline)
    brass = vertical_gradient(n, (250, 214, 140, 255), (176, 118, 58, 255))
    img.paste(brass, (0, 0), gear)

    # Bevel ring just outside the lens.
    ring_r = r_lens + rim
    ring = mask_from(lambda d: d.ellipse((c - ring_r, c - ring_r, c + ring_r, c + ring_r), fill=255), n)
    img.paste((58, 42, 30, 255), (0, 0), ring)

    # Smoked glass lens.
    lens = mask_from(lambda d: d.ellipse((c - r_lens, c - r_lens, c + r_lens, c + r_lens), fill=255), n)
    glass = vertical_gradient(n, (70, 66, 74, 255), (24, 21, 26, 255))
    img.paste(glass, (0, 0), lens)

    # Amber glow at the bottom of the lens, like light caught in glass.
    glow = mask_from(
        lambda d: d.ellipse((c - r_lens * 0.75, c + r_lens * 0.15, c + r_lens * 0.75, c + r_lens * 1.25), fill=150),
        n,
    ).filter(ImageFilter.GaussianBlur(n * 0.03))
    glow = ImageChops.multiply(glow, lens)
    img.paste((232, 190, 120, 255), (0, 0), glow)

    # Glass reflection: a crescent in the upper-left.
    hl_r = r_lens * 0.78
    shift = r_lens * 0.28
    crescent = mask_from(lambda d: d.ellipse((c - hl_r, c - hl_r, c + hl_r, c + hl_r), fill=255), n)
    cut = mask_from(
        lambda d: d.ellipse((c - hl_r + shift, c - hl_r + shift, c + hl_r + shift, c + hl_r + shift), fill=255), n
    )
    crescent = ImageChops.subtract(crescent, cut)
    crescent = ImageChops.multiply(crescent, lens).point(lambda v: v * 0.72)
    img.paste((255, 247, 232, 255), (0, 0), crescent)

    if not small:
        glint_r = r_lens * 0.11
        gx, gy = c + r_lens * 0.36, c - r_lens * 0.42
        glint = mask_from(lambda d: d.ellipse((gx - glint_r, gy - glint_r, gx + glint_r, gy + glint_r), fill=230), n)
        img.paste((255, 255, 255, 255), (0, 0), glint)

    return img.resize((size, size), Image.LANCZOS)


def main(out_dir, preview_path=None):
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    images = {s: render(s) for s in SIZES}
    largest = images[256]
    largest.save(out / "GearGlass.ico", sizes=[(s, s) for s in SIZES],
                 append_images=[images[s] for s in SIZES if s != 256])
    largest.save(out / "GearGlass.png")

    if preview_path is None:
        return

    # Preview sheet on dark and light backgrounds.
    w = sum(SIZES) + 12 * (len(SIZES) + 1)
    sheet = Image.new("RGBA", (w, 2 * 256 + 36), (0, 0, 0, 0))
    for row, bg in enumerate([(32, 32, 36, 255), (236, 236, 238, 255)]):
        y0 = row * (256 + 24) + 6
        ImageDraw.Draw(sheet).rectangle((0, y0 - 6, w, y0 + 256 + 6), fill=bg)
        x = 12
        for s in SIZES:
            sheet.alpha_composite(images[s], (x, y0 + 256 - s))
            x += s + 12
    sheet.save(preview_path)


if __name__ == "__main__":
    # Usage: make_icon.py <output dir> [preview.png]
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None)
