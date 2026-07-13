from __future__ import annotations

from pathlib import Path
from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont, ImageOps


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "assets"
SCREENSHOTS = ASSETS / "screenshots"
README_ASSETS = ASSETS / "readme"
OUTPUT = README_ASSETS / "codex-monitor-v2.1.3-hero.png"

WIDTH, HEIGHT = 2400, 1600


def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont:
    name = "seguisb.ttf" if bold else "segoeui.ttf"
    return ImageFont.truetype(str(Path("C:/Windows/Fonts") / name), size)


def rounded_mask(size: tuple[int, int], radius: int) -> Image.Image:
    mask = Image.new("L", size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size[0] - 1, size[1] - 1), radius, fill=255)
    return mask


def add_window(canvas: Image.Image, source: Path, box: tuple[int, int, int], radius: int = 34) -> None:
    x, y, target_width = box
    image = Image.open(source).convert("RGBA")
    target_height = round(image.height * target_width / image.width)
    image = image.resize((target_width, target_height), Image.Resampling.LANCZOS)

    clip = rounded_mask(image.size, radius)
    image.putalpha(ImageChops.multiply(image.getchannel("A"), clip))

    shadow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    shadow_mask = Image.new("L", canvas.size, 0)
    shadow_mask.paste(clip, (x, y + 22))
    shadow_mask = shadow_mask.filter(ImageFilter.GaussianBlur(35))
    shadow.putalpha(shadow_mask.point(lambda p: round(p * 0.42)))
    canvas.alpha_composite(shadow)
    canvas.alpha_composite(image, (x, y))

    rim = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    ImageDraw.Draw(rim).rounded_rectangle(
        (x, y, x + target_width - 1, y + target_height - 1),
        radius,
        outline=(255, 255, 255, 150),
        width=3,
    )
    canvas.alpha_composite(rim)


def glass_pill(canvas: Image.Image, xy: tuple[int, int], label: str) -> None:
    draw = ImageDraw.Draw(canvas)
    text_font = font(26, bold=True)
    box = draw.textbbox((0, 0), label, font=text_font)
    width = box[2] - box[0] + 52
    height = 52
    x, y = xy
    draw.rounded_rectangle((x, y, x + width, y + height), 26, fill=(15, 33, 58, 138), outline=(255, 255, 255, 145), width=2)
    draw.text((x + 26, y + 10), label, font=text_font, fill=(255, 255, 255, 242))


def main() -> None:
    README_ASSETS.mkdir(parents=True, exist_ok=True)
    background = Image.open(README_ASSETS / "v2.1.3-hero-background.png").convert("RGB")
    background = ImageOps.fit(background, (WIDTH, HEIGHT), Image.Resampling.LANCZOS)
    canvas = background.convert("RGBA")

    wash = Image.new("RGBA", canvas.size, (255, 255, 255, 0))
    wash_draw = ImageDraw.Draw(wash)
    for y in range(0, 380):
        alpha = round(76 * (1 - y / 380))
        wash_draw.line((0, y, WIDTH, y), fill=(250, 252, 255, alpha))
    canvas.alpha_composite(wash)

    draw = ImageDraw.Draw(canvas)
    ink = (15, 35, 62, 255)
    muted = (35, 61, 92, 225)

    icon = (124, 78, 222, 176)
    draw.rounded_rectangle(icon, 26, fill=(255, 255, 255, 55), outline=ink, width=7)
    draw.arc((147, 102, 204, 159), 44, 310, fill=ink, width=9)
    draw.ellipse((196, 119, 207, 130), fill=ink)

    draw.text((248, 68), "CODEX MONITOR", font=font(76, bold=True), fill=ink)
    draw.text((250, 158), "V2.1.3  ·  Static scenes, liquid-glass controls, live Codex status", font=font(31), fill=muted)

    glass_pill(canvas, (250, 238), "ACTIVE TASKS")
    glass_pill(canvas, (500, 238), "MODEL & REASONING")
    glass_pill(canvas, (874, 238), "TOKEN TOTALS")
    glass_pill(canvas, (1134, 238), "USAGE LIMITS")
    glass_pill(canvas, (1432, 238), "BACKGROUND SETTINGS")

    add_window(canvas, SCREENSHOTS / "v2.1.3-day-demo.png", (78, 365, 1040))
    add_window(canvas, SCREENSHOTS / "v2.1.3-night-demo.png", (1282, 365, 1040))
    glass_pill(canvas, (92, 300), "SUN")
    glass_pill(canvas, (1296, 300), "MOON")

    add_window(canvas, SCREENSHOTS / "v2.1.3-settings-demo.png", (770, 842, 860), radius=30)
    glass_pill(canvas, (786, 780), "SETTINGS")

    draw.text((120, 1534), "Windows 10/11  ·  Local-first  ·  No telemetry", font=font(24, bold=True), fill=(18, 40, 68, 220))
    draw.text((1792, 1534), "github.com/Yxianshe/Codex-Monitor", font=font(24), fill=(18, 40, 68, 220))

    canvas.convert("RGB").save(OUTPUT, quality=95, optimize=True)
    print(OUTPUT)

if __name__ == "__main__":
    main()
