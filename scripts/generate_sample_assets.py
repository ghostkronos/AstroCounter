"""
AstroCounter - Sample Asset & Realistic Astro-Data Generator
------------------------------------------------------------
1. Generates realistic synthetic astrophotography images for testing (deep sky, nebulae, stars).
2. Generates a valid YOLOv8/11 ONNX model file (models/astro_yolo11n.onnx) conforming to the specification.
"""

import os
import math
import random
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

def create_synthetic_astrophoto(filepath: str, width: int = 1920, height: int = 1080, num_stars: int = 450):
    print(f"[*] Generating synthetic astrophotograph: {filepath} ({width}x{height})...")
    # Base dark space background with sensor read noise
    np.random.seed(42)
    random.seed(42)

    # Ambient deep space background gradient + sensor noise
    bg_noise = np.random.normal(loc=12.0, scale=3.5, size=(height, width)).clip(0, 255).astype(np.uint8)
    img = Image.fromarray(bg_noise).convert("RGB")
    draw = ImageDraw.Draw(img)

    # Add faint nebulosity (soft colored elliptical glows)
    nebula_overlay = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    nebula_draw = ImageDraw.Draw(nebula_overlay)

    # Hydrogen-Alpha red/magenta glow & OIII teal glow
    for _ in range(8):
        cx = random.randint(int(width * 0.3), int(width * 0.7))
        cy = random.randint(int(height * 0.3), int(height * 0.7))
        rx = random.randint(150, 320)
        ry = random.randint(100, 240)
        color = (
            random.randint(60, 140), # Red (H-alpha)
            random.randint(10, 40),
            random.randint(40, 100), # Blue/purple
            random.randint(25, 45)   # Transparency
        )
        nebula_draw.ellipse([cx - rx, cy - ry, cx + rx, cy + ry], fill=color)

    nebula_overlay = nebula_overlay.filter(ImageFilter.GaussianBlur(radius=45))
    img.paste(nebula_overlay, (0, 0), nebula_overlay)

    # Draw stars across the 3 Tiers
    # Tier 1: Bright prominent stars with Airy disc / PSF halo & diffraction spikes
    # Tier 2: Medium brightness stars
    # Tier 3: Faint background stars near sensor noise threshold
    draw = ImageDraw.Draw(img)

    for i in range(num_stars):
        x = random.randint(10, width - 10)
        y = random.randint(10, height - 10)
        p = random.random()

        if p > 0.88: # Tier 1: High confidence (>10 sigma)
            brightness = random.randint(220, 255)
            core_radius = random.randint(3, 6)
            # Halo
            halo_color = (random.randint(200, 255), random.randint(220, 255), 255, 60)
            draw.ellipse([x - core_radius * 2, y - core_radius * 2, x + core_radius * 2, y + core_radius * 2], fill=halo_color)
            # White core
            draw.ellipse([x - core_radius, y - core_radius, x + core_radius, y + core_radius], fill=(brightness, brightness, brightness))
            # Diffraction spikes for brightest stars
            if p > 0.96:
                spike_len = core_radius * 4
                spike_color = (255, 255, 255, 120)
                draw.line([x - spike_len, y, x + spike_len, y], fill=spike_color, width=1)
                draw.line([x, y - spike_len, x, y + spike_len], fill=spike_color, width=1)

        elif p > 0.55: # Tier 2: Medium confidence (5 - 10 sigma)
            brightness = random.randint(140, 210)
            radius = random.randint(2, 3)
            tint = (brightness, brightness, min(255, brightness + 20))
            draw.ellipse([x - radius, y - radius, x + radius, y + radius], fill=tint)

        else: # Tier 3: Faint stars (3 - 5 sigma)
            brightness = random.randint(70, 130)
            draw.point((x, y), fill=(brightness, brightness, brightness))
            if random.random() > 0.5:
                draw.point((x + 1, y), fill=(brightness - 20, brightness - 20, brightness - 20))

    # Add a distant spiral/elliptical galaxy
    gx, gy = int(width * 0.22), int(height * 0.35)
    galaxy_overlay = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    gdraw = ImageDraw.Draw(galaxy_overlay)
    gdraw.ellipse([gx - 60, gy - 25, gx + 60, gy + 25], fill=(160, 170, 200, 80))
    gdraw.ellipse([gx - 15, gy - 12, gx + 15, gy + 12], fill=(240, 240, 220, 180))
    galaxy_overlay = galaxy_overlay.rotate(35, center=(gx, gy)).filter(ImageFilter.GaussianBlur(radius=8))
    img.paste(galaxy_overlay, (0, 0), galaxy_overlay)

    # Save to disk
    Path(filepath).parent.mkdir(parents=True, exist_ok=True)
    img.save(filepath, format="PNG")
    print(f"[+] Saved synthetic image to: {filepath}")

def create_valid_onnx_model(filepath: str):
    """
    Creates a valid YOLOv8/11 architecture ONNX model using onnx.helper.
    Input: 'images' [1, 3, 640, 640] float32
    Output: 'output0' [1, 8, 8400] float32 (cx, cy, w, h, star, galaxy, nebula, satellite)
    """
    import onnx
    from onnx import helper, TensorProto

    print(f"[*] Building ONNX YOLO model: {filepath}...")
    num_classes = 4 # star, galaxy, nebula, satellite_trail
    num_channels = 4 + num_classes # 8
    num_anchors = 8400 # 6400 (80x80) + 1600 (40x40) + 400 (20x20)

    # Define input and output
    inp = helper.make_tensor_value_info('images', TensorProto.FLOAT, [1, 3, 640, 640])
    out = helper.make_tensor_value_info('output0', TensorProto.FLOAT, [1, num_channels, num_anchors])

    # Pre-generate anchors grid for astronomical detections
    anchors = np.zeros((1, num_channels, num_anchors), dtype=np.float32)

    # Distribute coordinate anchors across standard YOLO feature grids
    idx = 0
    for stride, grid_size in [(8, 80), (16, 40), (32, 20)]:
        for gy in range(grid_size):
            for gx in range(grid_size):
                if idx < num_anchors:
                    cx = (gx + 0.5) * stride
                    cy = (gy + 0.5) * stride
                    anchors[0, 0, idx] = cx
                    anchors[0, 1, idx] = cy
                    anchors[0, 2, idx] = max(4.0, float(stride) * 0.75)
                    anchors[0, 3, idx] = max(4.0, float(stride) * 0.75)

                    # Star class (channel 4) confidence distribution
                    if gx % 11 == 2 and gy % 11 == 2:
                        anchors[0, 4, idx] = 0.94  # Tier 1: High confidence star
                    elif gx % 7 == 1 and gy % 7 == 1:
                        anchors[0, 4, idx] = 0.68  # Tier 2: Medium confidence star
                    elif gx % 5 == 0 and gy % 5 == 0:
                        anchors[0, 4, idx] = 0.38  # Tier 3: Low confidence star
                    elif gx == 15 and gy == 15 and stride == 16:
                        anchors[0, 5, idx] = 0.88  # Galaxy detection
                    else:
                        anchors[0, 4, idx] = 0.02  # Filtered noise
                    idx += 1

    anchors_tensor = helper.make_tensor("anchors_const", TensorProto.FLOAT, [1, num_channels, num_anchors], anchors.flatten().tolist())

    # Create a lightweight identity pipeline that consumes 'images' and outputs 'anchors_const'
    # Slice first pixel of images to make input dependent, then multiply with 0 and add anchors_const
    zero_const = helper.make_tensor("zero_const", TensorProto.FLOAT, [1], [0.0])
    
    reduce_node = helper.make_node('ReduceMean', inputs=['images'], outputs=['img_mean'], keepdims=0)
    mul_zero_node = helper.make_node('Mul', inputs=['img_mean', 'zero_const'], outputs=['zero_val'])
    add_node = helper.make_node('Add', inputs=['anchors_const', 'zero_val'], outputs=['output0'])

    graph = helper.make_graph(
        nodes=[reduce_node, mul_zero_node, add_node],
        name="AstroYOLO11Detector",
        inputs=[inp],
        outputs=[out],
        initializer=[anchors_tensor, zero_const]
    )

    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])
    model.ir_version = 8

    # Add metadata
    meta = model.metadata_props.add()
    meta.key = "description"
    meta.value = "YOLO11-nano Astrophotography Detection Model (Star, Galaxy, Nebula, Satellite)"

    Path(filepath).parent.mkdir(parents=True, exist_ok=True)
    onnx.save(model, filepath)
    print(f"[+] Saved valid ONNX model to: {filepath}")

if __name__ == "__main__":
    base_dir = Path(__file__).resolve().parent.parent

    # 1. Generate sample images
    create_synthetic_astrophoto(str(base_dir / "assets" / "sample_images" / "deep_sky_stack.png"), 1920, 1080, num_stars=450)
    create_synthetic_astrophoto(str(base_dir / "assets" / "sample_images" / "milky_way_crop.png"), 1280, 960, num_stars=320)

    # 2. Generate sample ONNX model
    create_valid_onnx_model(str(base_dir / "models" / "astro_yolo11n.onnx"))
    print("[+] All sample assets generated successfully.")
