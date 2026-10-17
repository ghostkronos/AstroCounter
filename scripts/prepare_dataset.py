"""
AstroCounter - Automated Dataset Preparation & Tiling Pipeline
--------------------------------------------------------------
1. Scans 'dataset/raw/' for stacked TIFF, PNG, or JPEG astrophotographs.
2. Performs astronomical star detection (background modeling + PSF peak finding).
3. Slices large images into 640x640 tiles (with overlap) matching the SAHI inference pipeline.
4. Generates normalized YOLO annotations (class 0: star) in 'dataset/labels/'.
5. Randomly splits tiles into train (80%) and val (20%).
6. Generates 'astro_data.yaml' configured and ready for 'scripts/train.py'.
"""

import os
import sys
import glob
import random
from pathlib import Path
import numpy as np
from PIL import Image

# Increase max image pixels to allow large astronomical mosaics
Image.MAX_IMAGE_PIXELS = None

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

def detect_stars_fast(gray: np.ndarray, threshold_sigma: float = 3.5):
    """
    Astronomical background modeling and fast local peak detection.
    Returns list of dicts: {'cx': float, 'cy': float, 'w': float, 'h': float, 'snr': float}
    """
    h, w = gray.shape

    # Fast background estimation via subsampled median and MAD
    sample_stride = max(1, min(h, w) // 200)
    sample = gray[::sample_stride, ::sample_stride].flatten()

    median = float(np.median(sample))
    mad = float(np.median(np.abs(sample - median)))
    sigma = max(1.0, 1.4826 * mad)

    threshold = median + (threshold_sigma * sigma)

    # Candidate peaks above threshold
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

    stars = []
    for y, x in peak_coords:
        peak_val = gray[y, x]
        snr = float((peak_val - median) / sigma)

        # Estimate star diameter based on brightness / FWHM
        if snr > 20:
            box_size = 14.0
        elif snr > 10:
            box_size = 10.0
        elif snr > 5:
            box_size = 7.0
        else:
            box_size = 5.0

        stars.append({
            'cx': float(x),
            'cy': float(y),
            'w': box_size,
            'h': box_size,
            'snr': snr
        })

    return stars

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

    # Search for common image formats
    extensions = ["*.tif", "*.tiff", "*.png", "*.jpg", "*.jpeg", "*.fits", "*.fit"]
    raw_files = []
    for ext in extensions:
        raw_files.extend(list(raw_path.glob(ext)))
        raw_files.extend(list(raw_path.glob(ext.upper())))

    # Deduplicate
    raw_files = sorted(list(set(raw_files)))

    if not raw_files:
        print("=" * 65)
        print(f"[!] DİKKAT: '{raw_path.resolve()}' klasöründe fotoğraf bulunamadı!")
        print("    Lütfen stacked astrofotoğraflarınızı (.tif, .png, .jpg vb.)")
        print(f"    aşağıdaki klasöre kopyalayıp bu scripti yeniden çalıştırın:")
        print(f"    --> {raw_path.resolve()}")
        print("=" * 65)
        return

    print("=" * 65)
    print(f"[*] AstroCounter Veri Hazırlık Hattı Başlatıldı")
    print(f"    Ham Görseller Klasörü: {raw_path.resolve()}")
    print(f"    Bulunan Fotoğraf Sayısı: {len(raw_files)}")
    print(f"    Dilim Boyutu: {tile_size}x{tile_size} (Örtüşme: %{int(overlap * 100)})")
    print("=" * 65)

    img_train_dir = Path(output_dir) / "images" / "train"
    img_val_dir = Path(output_dir) / "images" / "val"
    lbl_train_dir = Path(output_dir) / "labels" / "train"
    lbl_val_dir = Path(output_dir) / "labels" / "val"

    for d in [img_train_dir, img_val_dir, lbl_train_dir, lbl_val_dir]:
        d.mkdir(parents=True, exist_ok=True)
        # Clean previous tiles
        for f in d.glob("*.*"):
            try:
                f.unlink()
            except Exception:
                pass

    step = int(tile_size * (1.0 - overlap))
    total_tiles = 0
    total_stars = 0
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

        # Detect stars on full image
        print(f"    Yıldız tespiti yapılıyor (Fotometri & PSF analizi)...")
        detected_stars = detect_stars_fast(gray, threshold_sigma=4.5)
        print(f"    Toplam {len(detected_stars)} belirgin yıldız bulundu.")

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
                # Subcrop tile
                actual_w = min(tile_size, w - tx)
                actual_h = min(tile_size, h - ty)

                tile_rgb = np.zeros((tile_size, tile_size, 3), dtype=np.uint8)
                tile_rgb[:actual_h, :actual_w] = rgb[ty:ty + actual_h, tx:tx + actual_w]

                # Filter stars belonging to this tile and sort by SNR
                stars_in_tile = []
                for s in detected_stars:
                    sx = s['cx']
                    sy = s['cy']
                    if tx <= sx < (tx + tile_size) and ty <= sy < (ty + tile_size):
                        stars_in_tile.append(s)

                # Keep top 150 brightest/clearest stars per tile for optimal YOLO convergence
                stars_in_tile.sort(key=lambda s: s['snr'], reverse=True)
                stars_in_tile = stars_in_tile[:150]

                tile_stars = []
                for s in stars_in_tile:
                    sx = s['cx']
                    sy = s['cy']
                    rel_x = max(0.0, min(1.0, (sx - tx) / float(tile_size)))
                    rel_y = max(0.0, min(1.0, (sy - ty) / float(tile_size)))
                    rel_w = max(0.002, min(1.0, s['w'] / float(tile_size)))
                    rel_h = max(0.002, min(1.0, s['h'] / float(tile_size)))
                    # Class 0: star
                    tile_stars.append(f"0 {rel_x:.6f} {rel_y:.6f} {rel_w:.6f} {rel_h:.6f}")

                is_val = (random.random() > train_ratio)
                tile_name = f"{stem}_tile_y{ty}_x{tx}"

                img_dest = (img_val_dir if is_val else img_train_dir) / f"{tile_name}.jpg"
                lbl_dest = (lbl_val_dir if is_val else lbl_train_dir) / f"{tile_name}.txt"

                # Save tile as JPEG
                Image.fromarray(tile_rgb).save(img_dest, quality=95)

                # Save YOLO annotations
                with open(lbl_dest, "w", encoding="utf-8") as f:
                    if tile_stars:
                        f.write("\n".join(tile_stars) + "\n")

                if is_val:
                    val_count += 1
                else:
                    train_count += 1

                total_tiles += 1
                total_stars += len(tile_stars)
                tiles_for_image += 1

        print(f"    --> Üretilen dilim: {tiles_for_image}")

    # Generate astro_data.yaml
    yaml_path = Path("astro_data.yaml").resolve()
    base_dataset_path = Path(output_dir).resolve()

    yaml_content = f"""# AstroCounter - YOLO11 Dataset Configuration
path: {base_dataset_path.as_posix()}
train: images/train
val: images/val

names:
  0: star
  1: galaxy
  2: nebula
  3: satellite_trail
"""
    with open(yaml_path, "w", encoding="utf-8") as f:
        f.write(yaml_content)

    print("\n" + "=" * 65)
    print("[+] VERİ HAZIRLIĞI BAŞARIYLA TAMAMLANDI!")
    print(f"    Toplam Dilim Sayısı  : {total_tiles} ({train_count} Train / {val_count} Val)")
    print(f"    Etiketlenen Yıldız   : {total_stars}")
    print(f"    Yapılandırma Dosyası : {yaml_path}")
    print("=" * 65)
    print("\nSonraki Adım: Eğitimi başlatmak için:")
    print("  python scripts/train.py")
    print("=" * 65)

if __name__ == "__main__":
    prepare_dataset()
