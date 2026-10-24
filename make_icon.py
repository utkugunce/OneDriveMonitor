from PIL import Image, ImageDraw

def create_onedrive_icon(path):
    sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]
    images = []

    for size in sizes:
        img = Image.new("RGBA", size, (0, 0, 0, 0))
        draw = ImageDraw.Draw(img)
        w, h = size
        scale = w / 256.0

        # Arka plan yuvarlak kart (Modern Windows 11 App Icon)
        pad = int(12 * scale)
        draw.rounded_rectangle(
            [pad, pad, w - pad, h - pad],
            radius=int(48 * scale),
            fill=(15, 23, 42, 255), # Koyu Slate
            outline=(56, 189, 248, 220), # Mavi Çerçeve
            width=max(1, int(4 * scale))
        )

        # Bulut şekli (3 daire ve bir taban dikdörtgeni)
        cx, cy = w / 2, h / 2 - int(4 * scale)
        c_blue = (0, 120, 212, 255) # OneDrive Mavisi
        c_cyan = (56, 189, 248, 255) # Açık Mavi

        # Sol daire
        draw.ellipse([cx - 55*scale, cy - 10*scale, cx + 5*scale, cy + 50*scale], fill=c_blue)
        # Sağ daire
        draw.ellipse([cx - 5*scale, cy - 20*scale, cx + 60*scale, cy + 45*scale], fill=c_cyan)
        # Orta büyük tepe
        draw.ellipse([cx - 35*scale, cy - 45*scale, cx + 35*scale, cy + 25*scale], fill=c_cyan)
        # Taban
        draw.rounded_rectangle([cx - 50*scale, cy + 10*scale, cx + 50*scale, cy + 50*scale], radius=int(10*scale), fill=c_blue)

        images.append(img)

    images[0].save(path, format="ICO", sizes=[(s[0], s[1]) for s in sizes], append_images=images[1:])
    print(f"Icon created successfully: {path}")

create_onedrive_icon(r"C:\Users\utkug\.gemini\antigravity\scratch\OneDriveMonitor\icon.ico")
