"""
AstroCounter - Real Astronomical Survey Dataset Downloader & Preparation
------------------------------------------------------------------------
Downloads a compact, real astronomical sky survey dataset (SDSS DR16 / SkyServer
optical g-r-i cutouts and NASA/STScI Digitized Sky Survey fields) covering iconic
star clusters, spiral/elliptical galaxies, and emission nebulae into 'dataset/raw/',
then slices and annotates multi-class YOLO11 tiles via 'prepare_dataset.py'.
"""

import os
import sys
import ssl
import urllib.request
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

# Compact curated list of real astronomical survey fields (SDSS DR16 SkyServer JPEG cutouts)
# Each entry: (name, RA_deg, Dec_deg, scale_arcsec_per_px, width, height)
REAL_SKY_FIELDS = [
    ("SDSS_M51_Whirlpool_Galaxy", 202.4696, 47.1952, 0.8, 1280, 1280),
    ("SDSS_M101_Pinwheel_Galaxy", 210.8024, 54.3488, 0.9, 1280, 1280),
    ("SDSS_M81_Bodes_Galaxy", 148.8882, 69.0653, 0.9, 1280, 1280),
    ("SDSS_M82_Cigar_Galaxy", 148.9685, 69.6797, 0.7, 1280, 1280),
    ("SDSS_M13_Hercules_Cluster", 250.4235, 36.4613, 0.7, 1280, 1280),
    ("SDSS_M3_Globular_Cluster", 205.5484, 28.3773, 0.7, 1280, 1280),
    ("SDSS_M5_Star_Cluster", 229.6384, 2.0810, 0.7, 1280, 1280),
    ("SDSS_M67_Open_Cluster", 132.8250, 11.8000, 0.9, 1280, 1280),
    ("SDSS_NGC4565_Needle_Galaxy", 189.0866, 25.9877, 0.7, 1280, 1280),
    ("SDSS_NGC891_Dense_Starfield", 21.3800, 32.5400, 0.6, 1280, 1280),
    ("SDSS_Coma_Galaxy_Cluster", 194.9531, 27.9807, 0.6, 1280, 1280),
    ("SDSS_Cygnus_Rich_Starfield", 305.5570, 40.2567, 0.6, 1280, 1280),
]

def download_real_sky_survey(raw_dir: str = "dataset/raw"):
    raw_path = Path(raw_dir)
    raw_path.mkdir(parents=True, exist_ok=True)

    print("=" * 72)
    print("🔭 AstroCounter - Gerçek Astronomik Gökyüzü Veri Seti İndirici (SDSS DR16)")
    print("=" * 72)

    downloaded = 0
    for name, ra, dec, scale, w, h in REAL_SKY_FIELDS:
        dest_file = raw_path / f"{name}.jpg"
        if dest_file.exists() and dest_file.stat().st_size > 15000:
            print(f"[+] Zaten mevcut: {dest_file.name} ({dest_file.stat().st_size // 1024} KB)")
            downloaded += 1
            continue

        url = (
            f"https://skyserver.sdss.org/dr16/SkyServerWS/ImgCutout/getjpeg"
            f"?ra={ra}&dec={dec}&scale={scale}&width={w}&height={h}"
        )
        try:
            print(f"[*] İndiriliyor: {name} (RA={ra:.2f}°, Dec={dec:.2f}°)...")
            ctx = ssl.create_default_context()
            ctx.check_hostname = False
            ctx.verify_mode = ssl.CERT_NONE
            req = urllib.request.Request(url, headers={"User-Agent": "AstroCounter-DatasetFetcher/1.3"})
            with urllib.request.urlopen(req, timeout=20, context=ctx) as resp:
                data = resp.read()
                if len(data) > 10000:
                    dest_file.write_bytes(data)
                    print(f"    -> Kaydedildi: {dest_file.name} ({len(data) // 1024} KB)")
                    downloaded += 1
                else:
                    print(f"    [!] Geçersiz yanıt boyutu ({len(data)} B), atlandı.")
        except Exception as ex:
            print(f"    [!] İndirme uyarısı ({name}): {ex}")

    print(f"\n[+] Toplam {downloaded} gerçek gökyüzü gözlem alanı hazır.")
    return downloaded

if __name__ == "__main__":
    count = download_real_sky_survey("dataset/raw")
    scripts_dir = Path(__file__).parent
    sys.path.insert(0, str(scripts_dir))
    from prepare_dataset import prepare_dataset
    from generate_synthetic_astro_dataset import generate_astro_dataset

    # 1. Slice and annotate real astronomical survey fields
    prepare_dataset(raw_dir="dataset/raw", output_dir="dataset", tile_size=640, overlap=0.20)

    # 2. Augment with multi-class synthetic nebulae, galaxies, and satellite trails for balanced 4-class recall
    generate_astro_dataset(num_train=60, num_val=15, output_dir="dataset", tile_size=640)

