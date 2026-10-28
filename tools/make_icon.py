# Рисует иконку: скруглённый квадрат с градиентом, тёмный щит и молния (как логотип в окне)
from PIL import Image, ImageDraw

S = 1024  # рисуем крупно, потом уменьшаем — гладкие края
img = Image.new("RGBA", (S, S))
grad = Image.new("RGBA", (S, S))
px = grad.load()
for y in range(S):
    for x in range(S):
        t = (x + y) / (2 * S)
        px[x, y] = (int(46 + 13 * t), int(230 - 100 * t), int(168 + 78 * t), 255)
mask = Image.new("L", (S, S))
ImageDraw.Draw(mask).rounded_rectangle((32, 32, S - 32, S - 32), radius=250, fill=255)
img.paste(grad, (0, 0), mask)


def pt(x, y):  # координаты логотипа 24x24 -> иконка
    k = (S - 300) / 24
    return (150 + x * k, 150 + y * k)


shield = [(12, 1.5), (21, 5), (21, 11.5), (20.5, 14.5), (19, 17.3), (16, 20.3), (12, 22.5),
          (8, 20.3), (5, 17.3), (3.5, 14.5), (3, 11.5), (3, 5)]
bolt = [(13.2, 5.5), (8, 13), (11.6, 13), (10.6, 18.5), (16, 10.8), (12.4, 10.8)]
d = ImageDraw.Draw(img)
d.polygon([pt(*p) for p in shield], fill=(6, 17, 13, 235))
d.polygon([pt(*p) for p in bolt], fill=(46, 230, 168, 255))

img.resize((256, 256), Image.LANCZOS).save(
    "src/app.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
