"""
AstroCounter - YOLO11 Transfer Learning & Fine-Tuning Script for Astrophotography
---------------------------------------------------------------------------------
Trains a specialized YOLO11-nano/small model on astronomical image tiles.
Dataset classes:
  0: star
  1: galaxy
  2: nebula
  3: satellite_trail
"""

import os
import argparse
from pathlib import Path

def train_model(
    data_yaml: str = "astro_data.yaml",
    model_size: str = "yolo11n.pt",
    epochs: int = 100,
    imgsz: int = 640,
    batch_size: int = 16,
    device: str = "0",
    output_dir: str = "runs/astro_train"
):
    try:
        from ultralytics import YOLO
    except ImportError:
        print("[!] Ultralytics is not installed. Install with: pip install ultralytics")
        return

    print(f"[*] Starting YOLO11 training for Astrophotography...")
    print(f"    Base Model : {model_size}")
    print(f"    Data Config: {data_yaml}")
    print(f"    Epochs     : {epochs}")
    print(f"    Image Size : {imgsz}x{imgsz}")

    # Load base model (pre-trained weights)
    model = YOLO(model_size)

    # Train model with astrophotography-friendly augmentations:
    # Astrophotography data is rotationally invariant (0, 90, 180, 270 deg)
    # Color jittering is disabled or minimized to preserve narrowband/spectral colors
    results = model.train(
        data=data_yaml,
        epochs=epochs,
        imgsz=imgsz,
        batch=batch_size,
        device=device,
        project=output_dir,
        name="astro_detector",
        degrees=180.0,      # Invariant to telescope orientation
        flipud=0.5,         # Vertical flip
        fliplr=0.5,         # Horizontal flip
        mosaic=1.0,         # Blend multiple tiles
        hsv_h=0.015,        # Low hue variation
        hsv_s=0.2,          # Moderate saturation
        hsv_v=0.4,          # Light curve variation
        save=True,
        save_period=10,
        val=True
    )

    print(f"[+] Training completed. Best checkpoint: {output_dir}/astro_detector/weights/best.pt")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Train YOLO11 on Astrophotography Dataset")
    parser.add_argument("--data", default="astro_data.yaml", help="Path to data.yaml")
    parser.add_argument("--model", default="yolo11n.pt", help="Base model weights")
    parser.add_argument("--epochs", type=int, default=100, help="Number of training epochs")
    parser.add_argument("--imgsz", type=int, default=640, help="Tile resolution")
    parser.add_argument("--batch", type=int, default=16, help="Batch size")
    parser.add_argument("--device", default="0", help="CUDA device index or 'cpu'")
    args = parser.parse_args()

    train_model(
        data_yaml=args.data,
        model_size=args.model,
        epochs=args.epochs,
        imgsz=args.imgsz,
        batch_size=args.batch,
        device=args.device
    )

