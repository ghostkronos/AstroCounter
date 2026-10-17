"""
AstroCounter - ONNX Export & Optimization Pipeline
--------------------------------------------------
Exports PyTorch/Ultralytics YOLO checkpoint into optimized ONNX format
compatible with DirectML and CPU Execution Providers.
"""

import os
import argparse
from pathlib import Path

def export_to_onnx(
    weights_path: str = "best.pt",
    output_path: str = "models/astro_yolo11n.onnx",
    imgsz: int = 640,
    opset: int = 17,
    half: bool = False,
    dynamic: bool = False
):
    try:
        from ultralytics import YOLO
    except ImportError:
        print("[!] Ultralytics is not installed. Install with: pip install ultralytics")
        return

    print(f"[*] Loading weights from: {weights_path}")
    model = YOLO(weights_path)

    print(f"[*] Exporting to ONNX format (opset={opset}, dynamic={dynamic}, half={half})...")
    exported_file = model.export(
        format="onnx",
        imgsz=imgsz,
        opset=opset,
        simplify=True,
        dynamic=dynamic,
        half=half
    )

    out_file = Path(output_path)
    out_file.parent.mkdir(parents=True, exist_ok=True)

    if Path(exported_file).exists():
        import shutil
        shutil.move(exported_file, out_file)
        print(f"[+] Successfully exported and saved to: {out_file.resolve()}")
    else:
        print(f"[!] Warning: Exported file {exported_file} was not found.")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Export YOLO model to ONNX for AstroCounter")
    parser.add_argument("--weights", default="best.pt", help="Path to .pt weights")
    parser.add_argument("--output", default="models/astro_yolo11n.onnx", help="Destination ONNX path")
    parser.add_argument("--imgsz", type=int, default=640, help="Tile resolution")
    parser.add_argument("--opset", type=int, default=17, help="ONNX opset version")
    parser.add_argument("--half", action="store_true", help="Use FP16 half precision")
    parser.add_argument("--dynamic", action="store_true", help="Enable dynamic batching")
    args = parser.parse_args()

    export_to_onnx(
        weights_path=args.weights,
        output_path=args.output,
        imgsz=args.imgsz,
        opset=args.opset,
        half=args.half,
        dynamic=args.dynamic
    )

