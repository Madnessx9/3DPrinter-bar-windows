#!/usr/bin/env python3
"""Draws the Bambu Bar icon (a port of the macOS app's scripts/generate-icon.swift): a dark tile holding a printer
enclosure, the part inside built from coloured layers, the newest layer glowing green under the toolhead.

    python3 tools/generate-icon.py src/BambuBar/Resources

Writes BambuBar.ico (16 to 256 px) and AppIcon.png (256 px). Needs Pillow and NumPy."""
import os, sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

S = 1024  # drawn once at this size, then scaled down for each icon size


def rgba(r, g, b, a=1.0):
    return np.array([r, g, b, a], dtype=np.float32)


def mask_rounded(box, radius):
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).rounded_rectangle([round(v) for v in box], radius=round(radius), fill=255)
    return np.asarray(m, dtype=np.float32) / 255


def mask_polygon(points):
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).polygon([(round(x), round(y)) for x, y in points], fill=255)
    return np.asarray(m, dtype=np.float32) / 255


def linear(box, stops, horizontal=False):
    """Colour per pixel along the box (top to bottom, or left to right), evenly spaced stops."""
    x0, y0, x1, y1 = box
    ys, xs = np.mgrid[0:S, 0:S].astype(np.float32)
    t = (xs - x0) / max(1, x1 - x0) if horizontal else (ys - y0) / max(1, y1 - y0)
    t = np.clip(t, 0, 1)[..., None]
    if len(stops) == 2:
        return stops[0] * (1 - t) + stops[1] * t
    seg = t * (len(stops) - 1)
    i = np.clip(np.floor(seg).astype(int), 0, len(stops) - 2)
    f = seg - i
    a = np.stack(stops)[i[..., 0]]
    b = np.stack(stops)[i[..., 0] + 1]
    return a * (1 - f) + b * f


def radial(center, radius, inner, outer):
    ys, xs = np.mgrid[0:S, 0:S].astype(np.float32)
    t = np.clip(np.hypot(xs - center[0], ys - center[1]) / radius, 0, 1)[..., None]
    return inner * (1 - t) + outer * t


class Canvas:
    def __init__(self):
        self.px = np.zeros((S, S, 4), dtype=np.float32)  # straight RGBA

    def fill(self, color, mask):
        """Source-over with a per-pixel or constant colour inside a coverage mask."""
        color = np.broadcast_to(color, (S, S, 4)) if np.ndim(color) == 1 else color
        a = color[..., 3] * mask
        dst_a = self.px[..., 3]
        out_a = a + dst_a * (1 - a)
        safe = np.where(out_a > 0, out_a, 1)
        for c in range(3):
            self.px[..., c] = (color[..., c] * a + self.px[..., c] * dst_a * (1 - a)) / safe
        self.px[..., 3] = out_a

    def glow(self, color, mask, blur):
        soft = np.asarray(Image.fromarray((mask * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(blur)), dtype=np.float32) / 255
        self.fill(color, np.clip(soft * 1.6, 0, 1))
        self.fill(color, mask)

    def image(self):
        return Image.fromarray((np.clip(self.px, 0, 1) * 255).astype(np.uint8), "RGBA")


def stroke_mask(box, radius, width):
    outer = mask_rounded(box, radius)
    inner = mask_rounded([box[0] + width, box[1] + width, box[2] - width, box[3] - width], max(0, radius - width))
    return np.clip(outer - inner, 0, 1)


def draw():
    c = Canvas()
    # Windows icons fill more of their square than macOS tiles do.
    inset = S * 0.035
    tile = [inset, inset, S - inset, S - inset]
    tw = tile[2] - tile[0]
    radius = S * 0.2
    tile_mask = mask_rounded(tile, radius)
    # Drop shadow.
    shadow = np.asarray(Image.fromarray((tile_mask * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(S * 0.012)), dtype=np.float32) / 255
    c.fill(rgba(0, 0, 0, 0.35), np.roll(shadow, int(S * 0.008), axis=0))
    c.fill(linear(tile, [rgba(0.20, 0.22, 0.23), rgba(0.07, 0.08, 0.09)]), tile_mask)
    # Green bloom from below.
    centre = (tile[0] + tw / 2, tile[3] - tw * 0.18)
    c.fill(radial(centre, tw * 0.62, rgba(0.10, 0.85, 0.42, 0.45), rgba(0.10, 0.85, 0.42, 0)), tile_mask)
    c.fill(rgba(1, 1, 1, 0.10), stroke_mask(tile, radius, max(1, S * 0.004)))

    u = tw / 100

    def r(x, y, w, h):
        return [tile[0] + x * u, tile[1] + y * u, tile[0] + (x + w) * u, tile[1] + (y + h) * u]

    # Enclosure frame.
    frame = r(17, 15, 66, 72)
    c.fill(linear(frame, [rgba(0.36, 0.38, 0.39), rgba(0.16, 0.17, 0.18)]), mask_rounded(frame, 8 * u))
    # Chamber window.
    chamber = r(23, 21, 54, 58)
    chamber_mask = mask_rounded(chamber, 4.5 * u)
    c.fill(linear(chamber, [rgba(0.05, 0.06, 0.06), rgba(0.10, 0.11, 0.11)]), chamber_mask)
    # Chamber light.
    light = r(23, 21, 54, 34)
    c.fill(linear(light, [rgba(1, 0.96, 0.88, 0.30), rgba(1, 0.96, 0.88, 0)]), chamber_mask * mask_rounded(light, 0))
    # Plate.
    plate = r(28, 69, 44, 3)
    c.fill(linear(plate, [rgba(0.55, 0.52, 0.46), rgba(0.30, 0.29, 0.27)], horizontal=True), chamber_mask * mask_rounded(plate, u))

    # The part: layers stepping from white through orange to green.
    layers = [((0.96, 0.94, 0.90), 21), ((0.96, 0.94, 0.90), 26), ((0.93, 0.55, 0.26), 29.5), ((0.93, 0.55, 0.26), 30.5),
              ((0.14, 0.78, 0.68), 29), ((0.14, 0.78, 0.68), 25.5), ((0.13, 0.82, 0.42), 21.5), ((0.13, 0.82, 0.42), 19)]
    layer_h = 4.0
    top = 69
    for i, (col, w) in enumerate(layers):
        top -= layer_h
        rect = r(50 - w / 2, top, w, layer_h - 0.5)
        m = chamber_mask * mask_rounded(rect, 1.6 * u)
        c.fill(rgba(*col), m)
        c.fill(linear(rect, [rgba(1, 1, 1, 0.30), rgba(1, 1, 1, 0), rgba(0, 0, 0, 0.30)], horizontal=True), m)
        if i == len(layers) - 1:
            # Fresh layer glow.
            c.glow(rgba(0.35, 1, 0.6), chamber_mask * mask_rounded(r(50 - w / 2 + 1, top, w - 2, 1), 0.5 * u), 3 * u)
    # Gantry and toolhead.
    c.fill(rgba(0.42, 0.44, 0.45), chamber_mask * mask_rounded(r(23, top - 9.5, 54, 1.6), 0))
    head = r(42, top - 12.5, 16, 9)
    c.fill(linear(head, [rgba(0.97, 0.97, 0.97), rgba(0.72, 0.73, 0.74)]), chamber_mask * mask_rounded(head, 2 * u))
    c.fill(rgba(0.13, 0.82, 0.42), chamber_mask * mask_rounded(r(44, top - 10.5, 6, 1.4), 0.7 * u))
    px = lambda x, y: (tile[0] + x * u, tile[1] + y * u)
    c.fill(rgba(1, 0.62, 0.30), chamber_mask * mask_polygon([px(47.5, top - 3.5), px(52.5, top - 3.5), px(51, top - 0.6), px(49, top - 0.6)]))

    # Glass sheen and rim.
    c.fill(rgba(1, 1, 1, 0.07), chamber_mask * mask_polygon([
        (chamber[0] + 14 * u, chamber[1]), (chamber[0] + 30 * u, chamber[1]), (chamber[0] + 4 * u, chamber[3]), (chamber[0] - 12 * u, chamber[3])]))
    c.fill(rgba(1, 1, 1, 0.22), stroke_mask(chamber, 4.5 * u, max(1, 0.7 * u)))
    c.fill(rgba(1, 1, 1, 0.25), stroke_mask([v + d for v, d in zip(frame, (0.3 * u, 0.3 * u, -0.3 * u, -0.3 * u))], 7.8 * u, max(1, 0.6 * u)))
    # Status strip.
    c.glow(rgba(0.25, 0.95, 0.52), mask_rounded(r(42, 82.2, 16, 1.6), 0.8 * u), 2.5 * u)
    return c.image()


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "."
    os.makedirs(out, exist_ok=True)
    big = draw()
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = [big.resize((s, s), Image.LANCZOS) for s in sizes]
    images[-1].save(os.path.join(out, "AppIcon.png"))
    images[-1].save(os.path.join(out, "BambuBar.ico"), sizes=[(s, s) for s in sizes], append_images=images[:-1])
    print(os.path.join(out, "BambuBar.ico"))


if __name__ == "__main__":
    main()
