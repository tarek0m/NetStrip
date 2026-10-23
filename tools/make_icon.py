"""Draws the NetStrip icon: a teal up-arrow and a blue down-arrow on a dark rounded tile.

Usage (needs Pillow):  python tools/make_icon.py src/NetStrip/Assets/NetStrip.ico preview.png
Each size is drawn separately (8x supersampled), so small sizes keep their own, thicker strokes.
"""
import sys
from PIL import Image, ImageDraw

SS = 8                                   # supersampling factor
TEAL = (79, 209, 197, 255)               # upload, dark-mode tint (#4FD1C5)
BLUE = (76, 194, 255, 255)               # download, Windows accent light (#4CC2FF)
TOP, BOTTOM = (45, 55, 72), (17, 24, 39)  # tile gradient

def arrow(d, cx, y0, y1, w, color, up):
    """Shaft + triangular head, pointing up or down, centred on cx, spanning y0..y1."""
    head = w * 2.3
    head_len = head * 0.85
    shaft = w * 0.5
    if up:
        d.polygon([(cx, y0), (cx - head / 2, y0 + head_len), (cx + head / 2, y0 + head_len)], fill=color)
        d.rectangle([cx - shaft, y0 + head_len - 1, cx + shaft, y1], fill=color)
    else:
        d.polygon([(cx, y1), (cx - head / 2, y1 - head_len), (cx + head / 2, y1 - head_len)], fill=color)
        d.rectangle([cx - shaft, y0, cx + shaft, y1 - head_len + 1], fill=color)

def render(size):
    n = size * SS
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    grad = Image.new("RGBA", (n, n))
    gd = ImageDraw.Draw(grad)
    for y in range(n):
        t = y / (n - 1)
        gd.line([(0, y), (n, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(TOP, BOTTOM)) + (255,))
    mask = Image.new("L", (n, n), 0)
    inset = n * 0.04
    ImageDraw.Draw(mask).rounded_rectangle([inset, inset, n - inset, n - inset], radius=n * 0.22, fill=255)
    img.paste(grad, (0, 0), mask)
    # Faint light edge so the dark tile still reads on a dark taskbar.
    ImageDraw.Draw(img).rounded_rectangle([inset, inset, n - inset, n - inset], radius=n * 0.22,
                                          outline=(255, 255, 255, 48), width=max(SS, round(n * 0.015)))

    d = ImageDraw.Draw(img)
    # Thicker strokes at small sizes so the arrows survive 16 px.
    w = n * (0.11 if size <= 24 else 0.09)
    y0, y1 = n * 0.20, n * 0.80
    arrow(d, n * 0.35, y0, y1 - n * 0.04, w, TEAL, up=True)
    arrow(d, n * 0.65, y0 + n * 0.04, y1, w, BLUE, up=False)
    return img.resize((size, size), Image.LANCZOS)

if __name__ == "__main__":
    ico_path, preview_path = sys.argv[1], sys.argv[2]
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = [render(s) for s in sizes]
    images[-1].save(ico_path, format="ICO", sizes=[(s, s) for s in sizes], append_images=images[:-1])
    # Preview: every size side by side on light and dark backgrounds.
    width = sum(sizes) + 10 * (len(sizes) + 1)
    sheet = Image.new("RGBA", (width, 2 * 256 + 30), (255, 255, 255, 255))
    ImageDraw.Draw(sheet).rectangle([0, 256 + 20, width, 2 * 256 + 30], fill=(32, 32, 32, 255))
    x = 10
    for s, im in zip(sizes, images):
        sheet.alpha_composite(im, (x, 10))
        sheet.alpha_composite(im, (x, 256 + 25))
        x += s + 10
    sheet.save(preview_path)
