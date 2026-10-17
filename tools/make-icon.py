"""生成 Lumen 应用图标。

设计：深色圆角方块 + 三个沿对角线递增的像素方块（小→大，最大块用浅蓝强调色），
表达“分辨率提升”，不使用渐变/发光/机器人等 AI 套壳元素。
"""
from PIL import Image, ImageDraw
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "assets"
APP_ASSETS = ROOT / "src" / "Lumen.App" / "Assets"
ASSETS.mkdir(parents=True, exist_ok=True)
APP_ASSETS.mkdir(parents=True, exist_ok=True)

SIZE = 1024
BG = (26, 26, 26, 255)
GRAY_DARK = (128, 128, 128, 255)
GRAY_LIGHT = (200, 200, 200, 255)
ACCENT = (92, 200, 255, 255)

img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
draw = ImageDraw.Draw(img)
draw.rounded_rectangle([0, 0, SIZE - 1, SIZE - 1], radius=224, fill=BG)


def block(x: int, y: int, side: int, color, radius: int = 36) -> None:
    draw.rounded_rectangle([x, y, x + side, y + side], radius=radius, fill=color)


block(224, 700, 120, GRAY_DARK, radius=24)
block(404, 532, 192, GRAY_LIGHT, radius=32)
block(640, 292, 284, ACCENT, radius=44)

# 轻微的内描边，让图标在浅色任务栏上也有边界感。
draw.rounded_rectangle([2, 2, SIZE - 3, SIZE - 3], radius=222, outline=(255, 255, 255, 22), width=4)

png_path = ASSETS / "appicon-1024.png"
img.save(png_path)
ico_sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
ico_path = ASSETS / "appicon.ico"
img.save(ico_path, sizes=ico_sizes)

# 应用内使用的 256 与 44 尺寸 PNG。
img.resize((256, 256), Image.LANCZOS).save(ASSETS / "appicon-256.png")
img.resize((44, 44), Image.LANCZOS).save(ASSETS / "appicon-44.png")

for target in [APP_ASSETS / "AppIcon.ico", APP_ASSETS / "AppIcon.png", APP_ASSETS / "AppIcon-256.png", APP_ASSETS / "AppIcon-44.png"]:
    if target.suffix == ".ico":
        img.save(target, sizes=ico_sizes)
    elif target.name.endswith("256.png"):
        img.resize((256, 256), Image.LANCZOS).save(target)
    elif target.name.endswith("44.png"):
        img.resize((44, 44), Image.LANCZOS).save(target)
    else:
        img.save(target)

print("icons written:")
for p in [png_path, ico_path, ASSETS / "appicon-256.png", ASSETS / "appicon-44.png"]:
    print(f"  {p.relative_to(ROOT)}  {p.stat().st_size} bytes")
