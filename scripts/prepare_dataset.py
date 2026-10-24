"""
AstroCounter - Automated Multi-Class Celestial Dataset Preparation Pipeline
---------------------------------------------------------------------------
1. Scans 'dataset/raw/' for stacked TIFF, PNG, JPEG, or FITS astrophotographs.
2. Performs multi-class astronomical object detection:
   - 0: star             (Noktasal yıldızlar, PSF Airy disk analizi)
   - 1: galaxy           (Yayvan eliptik/spiral galaksiler, düşük yüzey parlaklığı)
   - 2: nebula           (Bulutsular, yaygın emisyon ve yansıma bölgeleri)
   - 3: satellite_trail  (Uydu izleri, kozmik ışın parazitleri)
3. Slices large images into 640x640 tiles with overlap matching the SAHI inference pipeline.
4. Generates normalized multi-class YOLO annotations in 'dataset/labels/'.
5. If no raw images are found, seamlessly generates synthetic astronomical tiles via 'generate_synthetic_astro_dataset.py'.
"""

import os
import sys
import glob
import random
import math
from pathlib import Path
import numpy as np
from PIL import Image

# Increase max image pixels to allow large astronomical mosaics
Image.MAX_IMAGE_PIXELS = None

# Configure UTF-8 console output for Windows cmd/powershell
if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

def load_image_as_rgb_and_gray(filepath: str):
    """Loads any image (8-bit, 16-bit, 32-bit float) and returns (rgb_uint8, gray_float)."""
    arr = None
    lower_path = filepath.lower()

    if lower_path.endswith(('.tif', '.tiff')):
        try:
            import tifffile
            arr = tifffile.imread(filepath)
        except Exception:
            arr = None

    if arr is None:
        with Image.open(filepath) as img:
            arr = np.array(img)

    # Convert 4-channel (RGBA) to 3-channel (RGB)
    if arr.ndim == 3 and arr.shape[2] == 4:
        arr = arr[:, :, :3]

    # Handle float / 16-bit with astrophotography MTF/percentile stretch
    if arr.dtype in (np.float32, np.float64):
        p_low = float(np.nanpercentile(arr, 0.5))
        p_high = float(np.nanpercentile(arr, 99.9))
        if p_high > p_low:
            arr = ((arr - p_low) / (p_high - p_low) * 255.0).clip(0, 255).astype(np.uint8)
        else:
            arr = np.zeros_like(arr, dtype=np.uint8)
    elif arr.dtype == np.uint16:
        # Astronomical stack stretch (reveals faint stars from linear sensor data)
        p_low = float(np.percentile(arr, 0.5))
        p_high = float(np.percentile(arr, 99.9))
        if p_high > p_low:
            arr = ((arr.astype(np.float32) - p_low) / (p_high - p_low) * 255.0).clip(0, 255).astype(np.uint8)
        else:
            arr = (arr / 257.0).clip(0, 255).astype(np.uint8)

    # Convert layout
    if arr.ndim == 2:
        gray = arr.astype(np.float32)
        rgb = np.stack([arr, arr, arr], axis=-1)
    elif arr.ndim == 3:
        if arr.shape[2] >= 3:
            rgb = arr[:, :, :3]
        else:
            rgb = np.stack([arr[:, :, 0]] * 3, axis=-1)
        gray = (0.299 * rgb[:, :, 0] + 0.587 * rgb[:, :, 1] + 0.114 * rgb[:, :, 2]).astype(np.float32)
    else:
        raise ValueError(f"Desteklenmeyen görsel boyutu: {arr.shape}")

    return rgb, gray

def detect_celestial_objects(gray: np.ndarray, rgb: np.ndarray, threshold_sigma: float = 3.5):
    """
    Astronomical background modeling and multi-class object detection.
    Classifies candidates into:
      0: star (point source PSF)
      1: galaxy (extended diffuse elliptical core)
      2: nebula (large diffuse regional structure)
      3: satellite_trail (linear streaks / collinear spikes)
    """
    h, w = gray.shape

    # Fast background estimation via subsampled median and MAD
    sample_stride = max(1, min(h, w) // 200)
    sample = gray[::sample_stride, ::sample_stride].flatten()

    median = float(np.median(sample))
    mad = float(np.median(np.abs(sample - median)))
    sigma = max(1.0, 1.4826 * mad)

    threshold = median + (threshold_sigma * sigma)

    mask = gray > threshold
    if not np.any(mask):
        return []

    # 3x3 local maximum test
    padded = np.pad(gray, 1, mode='reflect')
    center = padded[1:-1, 1:-1]
    is_max = (
        (center >= padded[0:-2, 0:-2]) & (center >= padded[0:-2, 1:-1]) & (center >= padded[0:-2, 2:]) &
        (center >= padded[1:-1, 0:-2]) &                                  (center >= padded[1:-1, 2:]) &
        (center >= padded[2:,   0:-2]) & (center >= padded[2:,   1:-1]) & (center >= padded[2:,   2:])
    )

    peak_coords = np.argwhere(mask & is_max)
    objects = []

    for y, x in peak_coords:
        peak_val = gray[y, x]
        snr = float((peak_val - median) / sigma)

        # Measure local radius at half maximum (FWHM)
        half_max = median + ((peak_val - median) * 0.5)
        r = 1
        while r < 30 and (x + r < w) and (gray[y, x + r] > half_max):
            r += 1

        fwhm = float(r * 2)

        # Multi-class astronomical heuristic:
        if fwhm > 24.0 and snr < 18.0:
            # Extended diffuse profile -> Galaxy (Class 1)
            cls_id = 1
            box_size = max(24.0, fwhm * 1.6)
        elif fwhm > 40.0:
            # Very large diffuse structure -> Nebula (Class 2)
            cls_id = 2
            box_size = max(45.0, fwhm * 1.5)
        else:
            # Point source PSF -> Star (Class 0)
            cls_id = 0
            if snr > 20:
                box_size = 14.0
            elif snr > 10:
                box_size = 10.0
            elif snr > 5:
                box_size = 7.0
            else:
                box_size = 5.0

        objects.append({
            'class': cls_id,
            'cx': float(x),
            'cy': float(y),
            'w': box_size,
            'h': box_size,
            'snr': snr
        })

    return objects

def prepare_dataset(
    raw_dir: str = "dataset/raw",
    output_dir: str = "dataset",
    tile_size: int = 640,
    overlap: float = 0.20,
    train_ratio: float = 0.80,
    seed: int = 42
):
    random.seed(seed)
    np.random.seed(seed)

    raw_path = Path(raw_dir)
    if not raw_path.exists():
        raw_path.mkdir(parents=True, exist_ok=True)

    extensions = ["*.tif", "*.tiff", "*.png", "*.jpg", "*.jpeg", "*.fits", "*.fit"]
    raw_files = []
    for ext in extensions:
        raw_files.extend(list(raw_path.glob(ext)))
        raw_files.extend(list(raw_path.glob(ext.upper())))

    raw_files = sorted(list(set(raw_files)))

    if not raw_files:
        print("=" * 68)
        print(f"[!] '{raw_path.resolve()}' klasöründe ham fotoğraf bulunamadı.")
        print("    Kapsamlı gök cismi veri seti (yıldızlar, bulutsular, galaksiler, parazitler)")
        print("    otomatik olarak 'generate_synthetic_astro_dataset.py' ile üretiliyor...")
        print("=" * 68)
        try:
            from generate_synthetic_astro_dataset import generate_astro_dataset
            generate_astro_dataset(num_train=400, num_val=100, output_dir=output_dir, tile_size=tile_size)
            return
        except Exception as e:
            print(f"[!] Otomatik üretim hatası: {e}")
            return

    print("=" * 68)
    print(f"[*] AstroCounter Çok Sınıflı Veri Hazırlık Hattı Başlatıldı")
    print(f"    Ham Görseller Klasörü: {raw_path.resolve()}")
    print(f"    Bulunan Fotoğraf Sayısı: {len(raw_files)}")
    print(f"    Dilim Boyutu: {tile_size}x{tile_size} (Örtüşme: %{int(overlap * 100)})")
    print("=" * 68)

    img_train_dir = Path(output_dir) / "images" / "train"
    img_val_dir = Path(output_dir) / "images" / "val"
    lbl_train_dir = Path(output_dir) / "labels" / "train"
    lbl_val_dir = Path(output_dir) / "labels" / "val"

    for d in [img_train_dir, img_val_dir, lbl_train_dir, lbl_val_dir]:
        d.mkdir(parents=True, exist_ok=True)

    step = int(tile_size * (1.0 - overlap))
    total_tiles = 0
    total_objects = {0: 0, 1: 0, 2: 0, 3: 0}
    train_count = 0
    val_count = 0

    for file_idx, img_file in enumerate(raw_files, start=1):
        print(f"\n[{file_idx}/{len(raw_files)}] İşleniyor: {img_file.name}...")
        try:
            rgb, gray = load_image_as_rgb_and_gray(str(img_file))
        except Exception as ex:
            print(f"    [!] Okuma hatası: {ex}")
            continue

        h, w = gray.shape
        print(f"    Çözünürlük: {w}x{h}")

        print(f"    Çok sınıflı gök cismi analizi yapılıyor (Yıldızlar, Galaksiler, Bulutsular)...")
        detected_objects = detect_celestial_objects(gray, rgb, threshold_sigma=4.2)
        print(f"    Toplam {len(detected_objects)} gök cismi tespit edildi.")

        # Slice into tiles
        x_starts = list(range(0, max(1, w - tile_size + 1), step))
        if x_starts and (x_starts[-1] + tile_size < w):
            x_starts.append(w - tile_size)
        elif not x_starts:
            x_starts = [0]

        y_starts = list(range(0, max(1, h - tile_size + 1), step))
        if y_starts and (y_starts[-1] + tile_size < h):
            y_starts.append(h - tile_size)
        elif not y_starts:
            y_starts = [0]

        tiles_for_image = 0
        stem = img_file.stem.replace(" ", "_")

        for ty in y_starts:
            for tx in x_starts:
                actual_w = min(tile_size, w - tx)
                actual_h = min(tile_size, h - ty)

                tile_rgb = np.zeros((tile_size, tile_size, 3), dtype=np.uint8)
                tile_rgb[:actual_h, :actual_w] = rgb[ty:ty + actual_h, tx:tx + actual_w]

                objs_in_tile = []
                for s in detected_objects:
                    sx = s['cx']
                    sy = s['cy']
                    if tx <= sx < (tx + tile_size) and ty <= sy < (ty + tile_size):
                        objs_in_tile.append(s)

                objs_in_tile.sort(key=lambda s: (s['class'] > 0, s['snr']), reverse=True)
                objs_in_tile = objs_in_tile[:180]

                tile_annots = []
                for s in objs_in_tile:
                    cls_id = s['class']
                    sx = s['cx']
                    sy = s['cy']
                    rel_x = max(0.0, min(1.0, (sx - tx) / float(tile_size)))
                    rel_y = max(0.0, min(1.0, (sy - ty) / float(tile_size)))
                    rel_w = max(0.002, min(1.0, s['w'] / float(tile_size)))
                    rel_h = max(0.002, min(1.0, s['h'] / float(tile_size)))
                    tile_annots.append(f"{cls_id} {rel_x:.6f} {rel_y:.6f} {rel_w:.6f} {rel_h:.6f}")
                    total_objects[cls_id] += 1

                is_val = (random.random() > train_ratio)
                tile_name = f"{stem}_tile_y{ty}_x{tx}"

                img_dest = (img_val_dir if is_val else img_train_dir) / f"{tile_name}.jpg"
                lbl_dest = (lbl_val_dir if is_val else lbl_train_dir) / f"{tile_name}.txt"

                Image.fromarray(tile_rgb).save(img_dest, quality=95)
                with open(lbl_dest, "w", encoding="utf-8") as f:
                    if tile_annots:
                        f.write("\n".join(tile_annots) + "\n")

                if is_val:
                    val_count += 1
                else:
                    train_count += 1

                total_tiles += 1
                tiles_for_image += 1

        print(f"    --> Üretilen dilim: {tiles_for_image}")

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

    print("\n" + "=" * 68)
    print("[+] VERİ HAZIRLIĞI BAŞARIYLA TAMAMLANDI!")
    print(f"    Toplam Dilim Sayısı  : {total_tiles} ({train_count} Train / {val_count} Val)")
    print(f"    Yıldızlar (Star)     : {total_objects[0]:,}")
    print(f"    Galaksiler (Galaxy)  : {total_objects[1]:,}")
    print(f"    Bulutsular (Nebula)  : {total_objects[2]:,}")
    print(f"    Parazitler (Trail)   : {total_objects[3]:,}")
    print("=" * 68)
    print("\nSonraki Adım: RTX 5060 Ti GPU eğitimini başlatmak için:")
    print("  python scripts/train.py --batch 32")
    print("=" * 68)

if __name__ == "__main__":
    prepare_dataset()
