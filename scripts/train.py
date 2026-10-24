"""
AstroCounter - YOLO11 High-Speed GPU Training Pipeline for RTX 5060 Ti (16 GB VRAM)
-----------------------------------------------------------------------------------
Trained on comprehensive astronomical celestial objects:
  0: star             (Yıldızlar)
  1: galaxy           (Galaksiler)
  2: nebula           (Bulutsular)
  3: satellite_trail  (Parazitler / Uydu İzleri)

Optimized specifically for 16 GB VRAM GPUs (NVIDIA GeForce RTX 5060 Ti, Blackwell / Ada Lovelace):
  - Batch size 32-64 leveraging 16 GB high-speed GDDR7/GDDR6 VRAM
  - Automatic Mixed Precision (AMP / FP16 Tensor Cores)
  - Blackwell sm_120 forward-compatibility handling
  - RAM caching & multi-worker dataloading
  - Automated ONNX export to 'models/astro_yolo11n.onnx' for AstroCounter UI
"""

import os
import sys
import argparse
from pathlib import Path

# Configure UTF-8 console output for Windows cmd/powershell
if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

def train_model(
    data_yaml: str = "astro_data.yaml",
    model_size: str = "yolo11n.pt",
    epochs: int = 50,
    imgsz: int = 640,
    batch_size: int = -1,
    device: str = "0",
    output_dir: str = "runs/astro_train"
):
    print("=" * 72)
    print("🚀 AstroCounter - RTX 5060 Ti 16 GB VRAM GPU Eğitimi (Faz 6)")
    print("=" * 72)

    # Blackwell sm_120 / sm_100 forward compatibility environment
    os.environ["TORCH_CUDA_ARCH_LIST"] = "8.9;9.0;10.0;12.0"
    os.environ["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID"

    # 1. Verify Dataset
    train_dir = Path("dataset/images/train")
    if not train_dir.exists() or len(list(train_dir.glob("*.jpg"))) == 0:
        print("[!] Eğitim veri seti bulunamadı. Kapsamlı gök cismi veri seti üretiliyor...")
        try:
            import generate_synthetic_astro_dataset as gen
            gen.generate_astro_dataset(num_train=400, num_val=100)
        except Exception as e:
            print(f"[!] Otomatik veri üretimi hatası: {e}")

    try:
        from ultralytics import YOLO
    except ImportError:
        print("[!] Ultralytics kütüphanesi yüklü değil. Kurulum için:")
        print("    pip install ultralytics")
        return

    import torch

    gpu_vram_gb = 0.0
    use_cuda = False

    if device != "cpu" and torch.cuda.is_available():
        try:
            test_t = torch.ones(1, device="cuda")
            props = torch.cuda.get_device_properties(0)
            gpu_name = props.name
            gpu_vram_gb = props.total_memory / (1024 ** 3)
            major, minor = torch.cuda.get_device_capability(0)

            print(f"[*] GPU Donanımı       : {gpu_name}")
            print(f"[*] Toplam VRAM        : {gpu_vram_gb:.1f} GB")
            print(f"[*] CUDA Mimarisi      : sm_{major}{minor} (Blackwell / Ada Ready)")
            use_cuda = True
            device = "0"
        except Exception as ex:
            print(f"[!] CUDA başlatma uyarısı: {ex}. CPU moduna geçiliyor.")
            device = "cpu"
    else:
        print("[*] Donanım            : CPU (CUDA bulunamadı veya pasif)")
        device = "cpu"

    # Dynamic batch size optimized for 16 GB VRAM
    if batch_size <= 0:
        if gpu_vram_gb >= 14.0:
            # 16 GB VRAM (RTX 5060 Ti 16GB / RTX 4060 Ti 16GB / RTX 4080)
            batch_size = 32 if imgsz <= 640 else 16
            print(f"[*] 16 GB VRAM Algılandı -> Optimum Batch Size: {batch_size}")
        elif gpu_vram_gb >= 7.5:
            # 8 GB VRAM
            batch_size = 16
            print(f"[*] 8 GB VRAM Algılandı -> Optimum Batch Size: {batch_size}")
        else:
            batch_size = 8
            print(f"[*] Standart / CPU Belleği -> Batch Size: {batch_size}")

    print(f"[*] Temel Model        : {model_size}")
    print(f"[*] Veri Yapılandırması: {data_yaml}")
    print(f"[*] Epoch Sayısı       : {epochs}")
    print(f"[*] Giriş Çözünürlüğü  : {imgsz}x{imgsz}")
    print(f"[*] Batch Boyutu       : {batch_size}")
    print(f"[*] Donanım Hızlandırma: {'CUDA (NVIDIA Tensor Cores FP16 / AMP)' if use_cuda else 'CPU'}")
    print("=" * 72)

    # Load YOLO11 Model
    model = YOLO(model_size)

    # Astrophotography-tailored training parameters:
    # 1. Degrees = 180 (Full rotational invariance in space)
    # 2. Flips = 0.5 (Telescope orientation invariance)
    # 3. Low HSV jitter (Preserves narrowband Ha, OIII, SII astronomical colors)
    # 4. AMP = True (NVIDIA Tensor Core 16-bit mixed precision)
    # 5. Workers = 8 (High throughput dataloading on fast multi-core CPUs)
    # 6. Cache = 'ram' (Zero disk IO bottleneck during training)
    results = model.train(
        data=data_yaml,
        epochs=epochs,
        imgsz=imgsz,
        batch=batch_size,
        device=device,
        project=output_dir,
        name="astro_detector",
        amp=use_cuda,       # Automatic Mixed Precision for NVIDIA Tensor Cores
        workers=8 if use_cuda else 2,
        cache="ram" if gpu_vram_gb >= 12.0 else True,
        degrees=180.0,      # Astronomical rotational invariance
        flipud=0.5,
        fliplr=0.5,
        mosaic=1.0,         # Multi-tile blending
        hsv_h=0.015,        # Low spectral color distortion
        hsv_s=0.2,
        hsv_v=0.4,
        save=True,
        save_period=10,
        val=True
    )

    print("\n" + "=" * 72)
    print("✅ GPU EĞİTİMİ BAŞARIYLA TAMAMLANDI!")
    best_weights = Path(output_dir) / "astro_detector" / "weights" / "best.pt"
    if not best_weights.exists():
        best_weights = Path("runs/detect") / output_dir / "astro_detector" / "weights" / "best.pt"

    if best_weights.exists():
        print(f"    En İyi Model Checkpoint : {best_weights}")
        print("\n[*] AstroCounter UI için ONNX Formatına Dönüştürülüyor...")
        try:
            scripts_dir = Path(__file__).parent
            sys.path.append(str(scripts_dir))
            from export_onnx import export_to_onnx
            export_to_onnx(weights_path=str(best_weights), output_path="models/astro_yolo11n.onnx")
            print("    --> 'models/astro_yolo11n.onnx' başarıyla güncellendi!")
        except Exception as ex:
            print(f"[!] ONNX dönüştürme hatası: {ex}. 'python scripts/export_onnx.py' ile manuel yapabilirsiniz.")
    print("=" * 72)

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Train YOLO11 on Astrophotography Dataset on RTX 5060 Ti")
    parser.add_argument("--data", default="astro_data.yaml", help="Path to data.yaml")
    parser.add_argument("--model", default="yolo11n.pt", help="Base model weights")
    parser.add_argument("--epochs", type=int, default=30, help="Number of training epochs")
    parser.add_argument("--imgsz", type=int, default=640, help="Tile resolution")
    parser.add_argument("--batch", type=int, default=-1, help="Batch size (-1 for auto 16GB optimization)")
    parser.add_argument("--device", default="0", help="CUDA device index or 'cpu'")
    parser.add_argument("--output", default="runs/astro_train", help="Output directory")
    args = parser.parse_args()

    train_model(
        data_yaml=args.data,
        model_size=args.model,
        epochs=args.epochs,
        imgsz=args.imgsz,
        batch_size=args.batch,
        device=args.device,
        output_dir=args.output
    )
