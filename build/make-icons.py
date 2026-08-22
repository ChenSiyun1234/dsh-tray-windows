#!/usr/bin/env python3
"""Build the dsh-tray icon set.

Two modes:

  python make-icons.py
      Draw the project's own mark — an app window with a status light. Every
      size is drawn at its own scale (8x supersampled) rather than downscaled
      from one big bitmap, so 16 px stays legible instead of turning to mush.

  python make-icons.py --from-dsh
      Use the whale favicon from your local DeepSeek Harness install instead.
      Nothing is downloaded and nothing is redistributed: the artwork stays on
      your machine. Requires Microsoft Edge for the SVG render.

Outputs into --out (default: %LOCALAPPDATA%\\Programs\\DeepSeekHarness):
  tray.ico      service running
  tray-off.ico  service stopped
  app.ico       window / shortcut / Alt-Tab
"""
import argparse
import os
import subprocess
import sys

from PIL import Image, ImageDraw, ImageEnhance

TRAY_SIZES = [16, 20, 24, 32, 40, 48, 64]
APP_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SS = 8  # supersampling factor

TILE_TOP = (0x5B, 0x51, 0xD8)      # indigo
TILE_BOTTOM = (0x36, 0x2C, 0x93)
TILE_TOP_OFF = (0x8A, 0x8A, 0x99)  # the "stopped" tile is drained of colour
TILE_BOTTOM_OFF = (0x63, 0x63, 0x72)
GLYPH = (0xFF, 0xFF, 0xFF, 0xFF)
LIGHT_ON = (0x33, 0xC5, 0x6E, 0xFF)
LIGHT_OFF = (0xD9, 0xDA, 0xE0, 0xFF)


def rounded_tile(size, top, bottom):
    """The app tile: a rounded square with a vertical gradient."""
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gradient = Image.new("RGBA", (size, size))
    pixels = gradient.load()
    for y in range(size):
        t = y / max(1, size - 1)
        pixels_row = (
            int(top[0] + (bottom[0] - top[0]) * t),
            int(top[1] + (bottom[1] - top[1]) * t),
            int(top[2] + (bottom[2] - top[2]) * t),
            255,
        )
        for x in range(size):
            pixels[x, y] = pixels_row
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (0, 0, size - 1, size - 1), radius=int(size * 0.22), fill=255)
    image.paste(gradient, (0, 0), mask)
    return image


def draw_mark(size, running=True):
    """One frame of the project mark, drawn at `size` px.

    The mark is a window with a status light: exactly what the tray controls —
    a single app window, and whether the service behind it is up.
    """
    big = size * SS
    tile_top, tile_bottom = (TILE_TOP, TILE_BOTTOM) if running else (TILE_TOP_OFF, TILE_BOTTOM_OFF)
    image = rounded_tile(big, tile_top, tile_bottom)
    draw = ImageDraw.Draw(image)

    # Window body.
    left, right = big * 0.21, big * 0.79
    top, bottom = big * 0.25, big * 0.68
    draw.rounded_rectangle((left, top, right, bottom),
                           radius=big * 0.06, fill=GLYPH)

    # Title-bar separator, punched back out to the tile colour.
    bar = top + (bottom - top) * 0.30
    thickness = max(1.0, big * 0.045)
    tile_pixel = image.getpixel((int(big * 0.5), int(bar)))
    slit = rounded_tile(big, tile_top, tile_bottom).crop(
        (int(left), int(bar - thickness / 2), int(right), int(bar + thickness / 2)))
    image.paste(slit, (int(left), int(bar - thickness / 2)))
    del tile_pixel

    # Status light, sitting on the window's lower-right corner with a cut-out
    # ring so it never smudges into the white body at small sizes.
    cx, cy = big * 0.775, big * 0.725
    radius = big * 0.135
    ring = radius + max(1.0, big * 0.05)
    ring_patch = rounded_tile(big, tile_top, tile_bottom)
    ring_mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(ring_mask).ellipse((cx - ring, cy - ring, cx + ring, cy + ring), fill=255)
    image.paste(ring_patch, (0, 0), ring_mask)
    draw = ImageDraw.Draw(image)
    draw.ellipse((cx - radius, cy - radius, cx + radius, cy + radius),
                 fill=LIGHT_ON if running else LIGHT_OFF)

    return image.resize((size, size), Image.LANCZOS)


def load_dsh_favicon():
    """Render the locally installed DeepSeek Harness favicon onto the tile."""
    svg_path = os.path.expandvars(
        r"%APPDATA%\npm\node_modules\@deepseek-ai\dsh\node_modules"
        r"\@deepseek-ai\dsh-web-frontend\dist\favicon.svg")
    if not os.path.isfile(svg_path):
        sys.exit("找不到本地 dsh 的 favicon.svg：%s\n请确认已安装 @deepseek-ai/dsh。" % svg_path)
    edge = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    if not os.path.isfile(edge):
        edge = r"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
    if not os.path.isfile(edge):
        sys.exit("--from-dsh 需要 Microsoft Edge 来渲染 SVG。")

    with open(svg_path, "r", encoding="utf-8") as handle:
        svg = handle.read()
    html = """<!DOCTYPE html><html><head><style>
* { margin:0; padding:0; box-sizing:border-box; }
body { width:256px; height:256px; background:transparent;
       display:flex; align-items:center; justify-content:center; }
.tile { width:240px; height:240px; border-radius:54px;
        background:linear-gradient(145deg,#3B82F6 0%%,#1D4ED8 100%%);
        display:flex; align-items:center; justify-content:center; }
svg { width:170px; height:170px; }
svg path { fill:#fff !important; }
</style></head><body><div class="tile">%s</div></body></html>""" % svg

    import tempfile
    tmp = tempfile.mkdtemp()
    html_file = os.path.join(tmp, "icon.html")
    png_file = os.path.join(tmp, "icon.png")
    with open(html_file, "w", encoding="utf-8") as handle:
        handle.write(html)
    subprocess.run([edge, "--headless", "--disable-gpu", "--hide-scrollbars",
                    "--window-size=256,256", "--default-background-color=00000000",
                    "--screenshot=" + png_file,
                    "file:///" + html_file.replace(os.sep, "/")], check=True)
    return Image.open(png_file).convert("RGBA")


def dim(image):
    grey = ImageEnhance.Color(image).enhance(0.18)
    r, g, b, a = ImageEnhance.Brightness(grey).enhance(0.92).split()
    return Image.merge("RGBA", (r, g, b, a.point(lambda v: int(v * 0.62))))


def frames_from_source(source, sizes, tighten=True):
    """Resample one bitmap into every size, trimming padding for small ones."""
    inset = int(source.width * 0.07)
    cropped = source.crop((inset, inset, source.width - inset, source.height - inset))
    return [(cropped if (tighten and s < 32) else source).resize((s, s), Image.LANCZOS)
            for s in sizes]


def save_ico(path, images, dib=True):
    # Save from the LARGEST frame: Pillow silently drops any requested size
    # bigger than the image being saved, so passing the 16 px one first would
    # collapse the icon to a single 16 px entry.
    #
    # bitmap_format="bmp" writes classic DIB entries; System.Drawing.Icon then
    # reliably picks the frame matching the shell's small-icon metric, which is
    # what keeps the tray crisp at 125% and 150% scaling.
    extra = {"bitmap_format": "bmp"} if dib else {}
    images[-1].save(path, format="ICO",
                    sizes=[(i.width, i.height) for i in images],
                    append_images=images[:-1], **extra)
    print("wrote %s  %s" % (path, [i.width for i in images]))


def main():
    parser = argparse.ArgumentParser(description="Build the dsh-tray icon set.")
    parser.add_argument("--from-dsh", action="store_true",
                        help="use the whale favicon from the local dsh install")
    parser.add_argument("--out", default=os.path.expandvars(
        r"%LOCALAPPDATA%\Programs\DeepSeekHarness"))
    args = parser.parse_args()
    os.makedirs(args.out, exist_ok=True)
    out = lambda name: os.path.join(args.out, name)

    if args.from_dsh:
        source = load_dsh_favicon()
        save_ico(out("tray.ico"), frames_from_source(source, TRAY_SIZES))
        save_ico(out("tray-off.ico"), frames_from_source(dim(source), TRAY_SIZES))
        save_ico(out("app.ico"), frames_from_source(source, APP_SIZES, tighten=False), dib=False)
    else:
        save_ico(out("tray.ico"), [draw_mark(s, True) for s in TRAY_SIZES])
        save_ico(out("tray-off.ico"), [draw_mark(s, False) for s in TRAY_SIZES])
        save_ico(out("app.ico"), [draw_mark(s, True) for s in APP_SIZES], dib=False)


if __name__ == "__main__":
    main()
