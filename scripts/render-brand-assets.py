"""Render the editable SVG identity into the small raster assets used by the app.

Requires Pillow. The SVG files in assets/branding are the editable source artwork.
"""

from pathlib import Path
from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "VeyonCampus.App" / "Assets"
ASSETS.mkdir(parents=True, exist_ok=True)
SCALE = 3
NAVY = "#132E46"
TEAL = "#187B70"
MINT = "#36A891"
PALE = "#F4FAF8"
SCREEN = "#DFF4EF"
ACCENT = "#77D8C2"


def draw_mark(size: int) -> Image.Image:
    factor = SCALE
    side = size * factor
    image = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    def box(coords):
        return tuple(round(value * factor) for value in coords)

    def points(coords):
        return [(round(x * factor), round(y * factor)) for x, y in coords]

    draw.rounded_rectangle(box((0, 0, 1024, 1024)), radius=box((0, 0, 216, 216))[2], fill=NAVY)
    draw.polygon(points([(512, 74), (854, 196), (854, 443), (820, 610), (740, 748), (512, 947), (284, 748), (204, 610), (170, 443), (170, 196)]), fill=TEAL)
    draw.polygon(points([(512, 126), (802, 230), (802, 440), (774, 571), (702, 700), (512, 871), (322, 700), (250, 571), (222, 440), (222, 230)]), fill=PALE)
    draw.rounded_rectangle(box((226, 286, 798, 680)), radius=box((0, 0, 42, 42))[2], fill=NAVY)
    draw.rounded_rectangle(box((253, 313, 771, 637)), radius=box((0, 0, 25, 25))[2], fill=SCREEN)
    draw.polygon(points([(302, 402), (374, 381), (420, 390), (512, 413), (512, 574), (420, 548), (374, 535), (302, 563)]), fill=TEAL)
    draw.polygon(points([(722, 402), (650, 381), (604, 390), (512, 413), (512, 574), (604, 548), (650, 535), (722, 563)]), fill=MINT)
    draw.line(points([(512, 414), (512, 574)]), fill=PALE, width=12 * factor)
    draw.rectangle(box((474, 682, 550, 734)), fill=NAVY)
    draw.rounded_rectangle(box((372, 746, 652, 778)), radius=10 * factor, fill=NAVY)
    draw.ellipse(box((499, 833, 525, 859)), fill=ACCENT)
    return image.resize((size, size), Image.Resampling.LANCZOS)


ASSETS.mkdir(parents=True, exist_ok=True)
mark = draw_mark(1024)
mark.save(ASSETS / "veyon-campus-mark.png", optimize=True)
mark.save(ASSETS / "veyon-campus.ico", format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])


canvas = Image.new("RGBA", (1480 * SCALE, 460 * SCALE), (0, 0, 0, 0))
canvas.alpha_composite(draw_mark(360 * SCALE), (48 * SCALE, 50 * SCALE))
draw = ImageDraw.Draw(canvas)


def load_font(candidates, size):
    for candidate in candidates:
        path = Path(candidate)
        if path.is_file():
            return ImageFont.truetype(str(path), size)
    try:
        return ImageFont.truetype("Arial", size)
    except OSError:
        return ImageFont.load_default(size=size)


font_bold = load_font([
    "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
    "C:/Windows/Fonts/arialbd.ttf",
    "/usr/share/fonts/truetype/msttcorefonts/Arial_Bold.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
], 104 * SCALE)
font_regular = load_font([
    "/System/Library/Fonts/Supplemental/Arial.ttf",
    "C:/Windows/Fonts/arial.ttf",
    "/usr/share/fonts/truetype/msttcorefonts/Arial.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
], 31 * SCALE)
font_tagline = load_font([
    "/System/Library/Fonts/Supplemental/Arial.ttf",
    "C:/Windows/Fonts/arial.ttf",
    "/usr/share/fonts/truetype/msttcorefonts/Arial.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
], 27 * SCALE)
draw.text((454 * SCALE, 100 * SCALE), "Veyon Campus", font=font_bold, fill=NAVY)
draw.text((461 * SCALE, 222 * SCALE), "CLASSROOM DEPLOYMENT", font=font_regular, fill=TEAL, spacing=4 * SCALE)
draw.line([(461 * SCALE, 286 * SCALE), (1360 * SCALE, 286 * SCALE)], fill="#DDE8E8", width=3 * SCALE)
draw.text((461 * SCALE, 322 * SCALE), "Manage classroom devices with confidence", font=font_tagline, fill="#617184")
canvas = canvas.resize((1480, 460), Image.Resampling.LANCZOS)
canvas.save(ROOT / "logo.png", optimize=True)
