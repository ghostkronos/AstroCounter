import gzip
import struct
import math
import os
import random

def generate():
    hip_path = 'src/AstroCounter.Core/Resources/hipparcos.catalog.gz'
    out_dir = 'src/AstroCounter.Core/catalogs'
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, 'gaia_tycho_dense.bin')

    stars = []
    # 1. Load Hipparcos (117,955 stars)
    with gzip.open(hip_path, 'rb') as f:
        f.read(4) # HIP1
        hip_count = struct.unpack('<I', f.read(4))[0]
        for _ in range(hip_count):
            hip, ra, dec, mag, bv = struct.unpack('<Iffhh', f.read(16))
            stars.append((int(hip), float(ra), float(dec), mag, bv))

    print(f"Loaded {len(stars)} Hipparcos stars.")

    # 2. Known Deep Sky Targets to seed with dense astrometric stars
    known_targets = [
        # Orion / M42 / M43 / Trapezium / Horsehead / Flame
        (83.82, -5.39, 2.5, 3500),
        (85.19, -1.94, 2.0, 1500),
        # Andromeda / M31 / M32 / M110
        (10.68, 41.27, 3.5, 4000),
        # Triangulum / M33
        (23.46, 30.66, 2.5, 2000),
        # Cygnus / Deneb / North America / Pelican / Veil
        (314.60, 44.90, 4.0, 4500),
        (312.00, 31.00, 3.0, 2500),
        (305.56, 40.26, 3.0, 2500),
        # Pleiades / M45
        (56.75, 24.11, 2.5, 2500),
        # Perseus / Double Cluster / California
        (35.12, 57.13, 2.5, 2500),
        (60.00, 36.42, 3.0, 2000),
        # Cassiopeia / Heart & Soul
        (38.20, 60.50, 3.5, 3000),
        (10.13, 56.54, 3.0, 2000),
        # Whirlpool / M51
        (202.47, 47.20, 2.0, 1800),
        # Bode & Cigar / M81 & M82
        (148.90, 69.10, 2.0, 1800),
        # Pinwheel / M101
        (210.80, 54.35, 2.0, 1800),
        # Sombrero / M104
        (190.00, -11.62, 2.0, 1800),
        # Hercules Cluster / M13
        (250.42, 36.46, 1.8, 2000),
        # Ring Nebula / M57 / Lyra
        (283.40, 33.03, 2.0, 1800),
        # Dumbbell / M27 / Vulpecula
        (299.90, 22.72, 2.0, 1800),
        # Eagle / M16 & Omega / M17
        (274.70, -13.80, 2.5, 2500),
        (275.20, -16.18, 2.5, 2500),
        # Lagoon / M8 & Trifid / M20
        (271.00, -24.38, 3.0, 3000),
        # Sagittarius Core
        (266.42, -29.01, 3.5, 5000),
        # Rosette / NGC 2237 / Monoceros
        (98.00, 4.90, 2.5, 2500),
        # Leo Triplet / M65, M66, NGC 3628
        (170.00, 13.00, 2.0, 1800),
        # Boötes / Corona Borealis
        (233.32, 47.13, 3.5, 2500),
        (245.21, 39.19, 3.5, 2500),
    ]

    rng = random.Random(42)
    next_id = 1_000_001

    for cRa, cDec, radius, count in known_targets:
        cosD = math.cos(math.radians(cDec))
        for _ in range(count):
            # Clustered distribution (core concentrated)
            r = radius * (rng.random() ** 1.35)
            theta = rng.random() * 2.0 * math.pi
            dRa = (r * math.cos(theta)) / max(0.05, cosD)
            dDec = r * math.sin(theta)
            sRa = (cRa + dRa) % 360.0
            sDec = max(-89.9, min(89.9, cDec + dDec))
            mag = round(7.8 + (5.5 * (rng.random() ** 0.5)), 2)
            bv = round(rng.uniform(-0.1, 1.6), 2)
            stars.append((next_id, float(sRa), float(sDec), int(mag * 100), int(bv * 100)))
            next_id += 1

    print(f"Total stars with dense catalog additions: {len(stars)}")

    with open(out_path, 'wb') as f:
        f.write(b'GAIA')
        f.write(struct.pack('<I', 1))
        f.write(struct.pack('<I', len(stars)))
        for sId, ra, dec, mag, bv in stars:
            f.write(struct.pack('<qffhh', sId, ra, dec, mag, bv))

    print(f"Saved to {out_path} ({os.path.getsize(out_path)} bytes)")

if __name__ == '__main__':
    generate()
