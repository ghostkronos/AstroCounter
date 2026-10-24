"""
AstroCounter - Comprehensive Astronomical Dataset Generator
------------------------------------------------------------
Generates high-fidelity synthetic astrophotography training tiles (640x640)
with exact ground-truth bounding box annotations for all 4 astronomical classes:
  0: star             (Noktasal yıldızlar, PSF Airy disk, difraksiyon kütükleri)
  1: galaxy           (Yayvan eliptik ve spiral galaksiler, parlak çekirdek & soluk kollar)
  2: nebula           (Büyük gaz/toz bulutsuları, emisyon & yansıma dalgalanmaları)
  3: satellite_trail  (Uydu izleri, kozmik ışın parazitleri, doğrusal gürültü çizgileri)

Optimized for high-speed training on 16 GB VRAM GPUs like the NVIDIA GeForce RTX 5060 Ti.
"""

import os
import sys

if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

import random
import math
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

def create_airy_disk_star(tile: np.ndarray, cx: float, cy: float, brightness: float, fwhm: float):
    """Draws an astronomical point spread function (PSF) star with Gaussian/Moffat core."""
    h, w, _ = tile.shape
    r = int(math.ceil(fwhm * 3.5))
    x0 = max(0, int(cx - r))
    x1 = min(w, int(cx + r + 1))
    y0 = max(0, int(cy - r))
    y1 = min(h, int(cy + r + 1))

    # Star color based on astronomical spectral type (O/B blue to M red)
    color_temp = random.random()
    if color_temp > 0.7:
        color = np.array([170, 205, 255], dtype=np.float32) # Blue-white
    elif color_temp > 0.35:
        color = np.array([255, 250, 240], dtype=np.float32) # White
    elif color_temp > 0.15:
        color = np.array([255, 220, 140], dtype=np.float32) # Yellow/Orange
    else:
        color = np.array([255, 130, 110], dtype=np.float32) # Deep Red

    sigma2 = (fwhm / 2.355) ** 2
    for y in range(y0, y1):
        dy2 = (y - cy) ** 2
        for x in range(x0, x1):
            d2 = (x - cx) ** 2 + dy2
            # Moffat / Gaussian PSF profile
            intensity = brightness * math.exp(-0.5 * d2 / sigma2)
            if intensity > 0.5:
                for c in range(3):
                    tile[y, x, c] = min(255.0, tile[y, x, c] + (intensity * (color[c] / 255.0)))

    # Diffraction spikes for very bright stars
    if brightness > 180 and fwhm >= 3.5:
        spike_len = int(fwhm * 4.5)
        for offset in range(-spike_len, spike_len + 1):
            if offset == 0: continue
            factor = max(0.0, 1.0 - (abs(offset) / spike_len)) * 45.0
            # Horizontal spike
            px = int(cx + offset)
            py = int(cy)
            if 0 <= px < w and 0 <= py < h:
                tile[py, px] = np.clip(tile[py, px] + factor * (color / 255.0), 0, 255)
            # Vertical spike
            px = int(cx)
            py = int(cy + offset)
            if 0 <= px < w and 0 <= py < h:
                tile[py, px] = np.clip(tile[py, px] + factor * (color / 255.0), 0, 255)

def create_galaxy(tile: np.ndarray, cx: float, cy: float, major_axis: float, minor_axis: float, angle_rad: float):
    """Draws an elliptical or spiral galaxy with an exponential disk profile and bright core."""
    h, w, _ = tile.shape
    r = int(math.ceil(major_axis * 1.8))
    x0 = max(0, int(cx - r))
    x1 = min(w, int(cx + r + 1))
    y0 = max(0, int(cy - r))
    y1 = min(h, int(cy + r + 1))

    cos_a = math.cos(angle_rad)
    sin_a = math.sin(angle_rad)
    core_color = np.array([255, 235, 190], dtype=np.float32)
    arm_color = np.array([160, 200, 245], dtype=np.float32)

    for y in range(y0, y1):
        dy = y - cy
        for x in range(x0, x1):
            dx = x - cx
            # Rotate into galaxy principal axis frame
            xr = (dx * cos_a) + (dy * sin_a)
            yr = (-dx * sin_a) + (dy * cos_a)

            # Elliptical radius
            r_ellip = math.sqrt(((xr / (major_axis * 0.5)) ** 2) + ((yr / (minor_axis * 0.5)) ** 2))
            if r_ellip < 2.5:
                # Sersic-like exponential falloff
                profile = math.exp(-1.8 * r_ellip)
                core_val = 110.0 * profile
                arm_val = 40.0 * math.exp(-0.9 * r_ellip)

                for c in range(3):
                    val = (core_val * (core_color[c] / 255.0)) + (arm_val * (arm_color[c] / 255.0))
                    tile[y, x, c] = min(255.0, tile[y, x, c] + val)

def create_nebula_cloud(tile: np.ndarray, cx: float, cy: float, radius: float, is_emission: bool):
    """Draws diffuse interstellar nebular gas (H-alpha deep red or OIII teal/cyan)."""
    h, w, _ = tile.shape
    r = int(radius)
    x0 = max(0, int(cx - r))
    x1 = min(w, int(cx + r + 1))
    y0 = max(0, int(cy - r))
    y1 = min(h, int(cy + r + 1))

    color = np.array([220, 50, 70], dtype=np.float32) if is_emission else np.array([40, 170, 210], dtype=np.float32)

    # Multi-octave smooth diffuse noise cloud
    for y in range(y0, y1):
        dy = (y - cy) / radius
        for x in range(x0, x1):
            dx = (x - cx) / radius
            dist = math.sqrt((dx * dx) + (dy * dy))
            if dist < 1.0:
                falloff = (math.cos(dist * math.pi) * 0.5) + 0.5
                intensity = falloff * random.uniform(25.0, 55.0)
                for c in range(3):
                    tile[y, x, c] = min(255.0, tile[y, x, c] + (intensity * (color[c] / 255.0)))

def create_satellite_trail(tile: np.ndarray, start_x: float, start_y: float, end_x: float, end_y: float, width: float = 1.5):
    """Draws a linear streak artifact representing a satellite trail, cosmic ray, or sensor glitch."""
    h, w, _ = tile.shape
    dist = math.hypot(end_x - start_x, end_y - start_y)
    if dist < 1.0: return

    steps = int(dist * 2)
    brightness = random.uniform(140.0, 240.0)
    for i in range(steps):
        t = i / float(steps)
        cx = start_x + (t * (end_x - start_x))
        cy = start_y + (t * (end_y - start_y))
        for dy in range(-int(width), int(width) + 1):
            for dx in range(-int(width), int(width) + 1):
                px = int(cx + dx)
                py = int(cy + dy)
                if 0 <= px < w and 0 <= py < h:
                    weight = max(0.0, 1.0 - (math.hypot(dx, dy) / width))
                    tile[py, px] = np.clip(tile[py, px] + (brightness * weight), 0, 255)

def generate_tile(tile_size: int = 640):
    """Synthesizes a realistic 640x640 astrophotography tile with multiple celestial classes."""
    # 1. Base dark sky with background photon noise and subtle gradient
    bg_base = random.uniform(8.0, 22.0)
    tile = np.random.normal(bg_base, 2.5, (tile_size, tile_size, 3)).astype(np.float32)
    tile = np.clip(tile, 0, 255)

    annotations = []

    # 2. Nebulae clouds (Class 2: nebula) ~ 30% chance per tile
    if random.random() < 0.35:
        num_clouds = random.randint(1, 2)
        for _ in range(num_clouds):
            ncx = random.uniform(120, tile_size - 120)
            ncy = random.uniform(120, tile_size - 120)
            n_rad = random.uniform(60, 140)
            is_emission = (random.random() > 0.4)
            create_nebula_cloud(tile, ncx, ncy, n_rad, is_emission)

            box_size = n_rad * 1.8
            rx = ncx / tile_size
            ry = ncy / tile_size
            rw = box_size / tile_size
            rh = box_size / tile_size
            annotations.append(f"2 {rx:.6f} {ry:.6f} {rw:.6f} {rh:.6f}")

    # 3. Galaxies (Class 1: galaxy) ~ 40% chance per tile
    if random.random() < 0.45:
        num_galaxies = random.randint(1, 3)
        for _ in range(num_galaxies):
            gcx = random.uniform(60, tile_size - 60)
            gcy = random.uniform(60, tile_size - 60)
            major = random.uniform(22.0, 54.0)
            minor = major * random.uniform(0.35, 0.95)
            angle = random.uniform(0, math.pi)
            create_galaxy(tile, gcx, gcy, major, minor, angle)

            box_w = max(major, minor) * 1.6
            box_h = max(major, minor) * 1.6
            rx = gcx / tile_size
            ry = gcy / tile_size
            rw = box_w / tile_size
            rh = box_h / tile_size
            annotations.append(f"1 {rx:.6f} {ry:.6f} {rw:.6f} {rh:.6f}")

    # 4. Satellite trails / Cosmic rays (Class 3: satellite_trail) ~ 25% chance per tile
    if random.random() < 0.28:
        # Generate diagonal or linear streak
        x_start = random.uniform(0, tile_size * 0.4)
        y_start = random.uniform(0, tile_size)
        x_end = random.uniform(tile_size * 0.6, tile_size)
        y_end = random.uniform(0, tile_size)
        create_satellite_trail(tile, x_start, y_start, x_end, y_end, width=1.5)

        streak_cx = (x_start + x_end) * 0.5
        streak_cy = (y_start + y_end) * 0.5
        streak_w = abs(x_end - x_start) + 16.0
        streak_h = abs(y_end - y_start) + 16.0
        rx = streak_cx / tile_size
        ry = streak_cy / tile_size
        rw = min(1.0, streak_w / tile_size)
        rh = min(1.0, streak_h / tile_size)
        annotations.append(f"3 {rx:.6f} {ry:.6f} {rw:.6f} {rh:.6f}")

    # 5. Stars (Class 0: star) - Every deep sky field contains dozens of stars
    num_stars = random.randint(40, 110)
    for _ in range(num_stars):
        scx = random.uniform(8, tile_size - 8)
        scy = random.uniform(8, tile_size - 8)
        # Power law distribution: thousands of faint stars, few bright stars
        brightness_power = random.random() ** 3
        brightness = 25.0 + (brightness_power * 230.0)
        fwhm = 1.8 + (brightness_power * 4.5)
        create_airy_disk_star(tile, scx, scy, brightness, fwhm)

        box_size = max(6.0, fwhm * 2.8)
        rx = scx / tile_size
        ry = scy / tile_size
        rw = box_size / tile_size
        rh = box_size / tile_size
        annotations.append(f"0 {rx:.6f} {ry:.6f} {rw:.6f} {rh:.6f}")

    tile_uint8 = np.clip(tile, 0, 255).astype(np.uint8)
    return tile_uint8, annotations

def generate_astro_dataset(
    num_train: int = 400,
    num_val: int = 100,
    output_dir: str = "dataset",
    tile_size: int = 640
):
    print("=" * 70)
    print("[*] AstroCounter - Kapsamlı Gök Cismi Veri Seti Üreticisi (Faz 6)")
    print(f"    Hedef Klasör       : {output_dir}")
    print(f"    Eğitim (Train)     : {num_train} Dilim (640x640)")
    print(f"    Doğrulama (Val)    : {num_val} Dilim (640x640)")
    print(f"    Sınıflar (Classes) : 0:star, 1:galaxy, 2:nebula, 3:satellite_trail")
    print("=" * 70)

    train_img_dir = Path(output_dir) / "images" / "train"
    val_img_dir = Path(output_dir) / "images" / "val"
    train_lbl_dir = Path(output_dir) / "labels" / "train"
    val_lbl_dir = Path(output_dir) / "labels" / "val"

    for d in [train_img_dir, val_img_dir, train_lbl_dir, val_lbl_dir]:
        d.mkdir(parents=True, exist_ok=True)

    total_objects = {0: 0, 1: 0, 2: 0, 3: 0}

    # Generate Train
    print(f"[*] {num_train} Eğitim (Train) görseli üretiliyor...")
    for i in range(1, num_train + 1):
        tile, annots = generate_tile(tile_size)
        name = f"astro_synth_train_{i:04d}"
        img_file = train_img_dir / f"{name}.jpg"
        lbl_file = train_lbl_dir / f"{name}.txt"

        Image.fromarray(tile).save(img_file, quality=95)
        with open(lbl_file, "w", encoding="utf-8") as f:
            f.write("\n".join(annots) + "\n")

        for a in annots:
            cls_id = int(a.split()[0])
            total_objects[cls_id] += 1

        if i % 100 == 0 or i == num_train:
            print(f"    Train: {i}/{num_train} üretildi...")

    # Generate Val
    print(f"[*] {num_val} Doğrulama (Val) görseli üretiliyor...")
    for i in range(1, num_val + 1):
        tile, annots = generate_tile(tile_size)
        name = f"astro_synth_val_{i:04d}"
        img_file = val_img_dir / f"{name}.jpg"
        lbl_file = val_lbl_dir / f"{name}.txt"

        Image.fromarray(tile).save(img_file, quality=95)
        with open(lbl_file, "w", encoding="utf-8") as f:
            f.write("\n".join(annots) + "\n")

        for a in annots:
            cls_id = int(a.split()[0])
            total_objects[cls_id] += 1

        if i % 50 == 0 or i == num_val:
            print(f"    Val: {i}/{num_val} üretildi...")

    # Write astro_data.yaml
    yaml_content = """# AstroCounter - Multi-Class YOLO11 Celestial Dataset
path: ./dataset
train: images/train
val: images/val

names:
  0: star
  1: galaxy
  2: nebula
  3: satellite_trail
"""
    Path("astro_data.yaml").write_text(yaml_content, encoding="utf-8")

    print("\n" + "=" * 70)
    print("[+] GÖK CİSMİ VERİ SETİ BAŞARIYLA OLUŞTURULDU!")
    print(f"    Toplam Görsel : {num_train + num_val} adet")
    print(f"    Yıldızlar (Star)            : {total_objects[0]:,} adet")
    print(f"    Galaksiler (Galaxy)         : {total_objects[1]:,} adet")
    print(f"    Bulutsular (Nebula)         : {total_objects[2]:,} adet")
    print(f"    Parazit / Uydular (Trail)   : {total_objects[3]:,} adet")
    print("=" * 70)
    print("RTX 5060 Ti GPU eğitimini başlatmak için: python scripts/train.py --batch 32")
    print("=" * 70)

if __name__ == "__main__":
    generate_astro_dataset()
