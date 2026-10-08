#!/usr/bin/env python3
"""Package the transparent application artwork into native icon formats.

Run on macOS (sips and iconutil are included with the OS). Published builds use
the committed outputs and do not need to run this script or install Pillow.
"""
from pathlib import Path
import struct
import subprocess
import tempfile

root = Path(__file__).resolve().parent.parent
brand = root / "Resources" / "Brand"
source = brand / "ClipHarbor-source.png"


def png(size, destination):
    subprocess.run(["sips", "-s", "format", "png", "-z", str(size), str(size),
                    str(source), "--out", str(destination)], check=True, stdout=subprocess.DEVNULL)


with tempfile.TemporaryDirectory(prefix="clipharbor-icons-") as temporary:
    folder = Path(temporary)
    iconset = folder / "ClipHarbor.iconset"
    iconset.mkdir()
    for size in (16, 32, 128, 256, 512):
        png(size, iconset / f"icon_{size}x{size}.png")
        png(size * 2, iconset / f"icon_{size}x{size}@2x.png")
    subprocess.run(["iconutil", "-c", "icns", str(iconset), "-o", str(brand / "ClipHarbor.icns")], check=True)
    png(1024, brand / "ClipHarbor.png")
    sizes = (16, 24, 32, 48, 64, 128, 256)
    offset = 6 + 16 * len(sizes)
    entries, payloads = [], []
    for size in sizes:
        path = folder / f"windows-{size}.png"
        png(size, path)
        data = path.read_bytes()
        entries.append(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
        payloads.append(data)
        offset += len(data)
    (brand / "ClipHarbor.ico").write_bytes(struct.pack("<HHH", 0, 1, len(sizes)) + b"".join(entries) + b"".join(payloads))
print("Generated macOS ICNS, Windows multi-resolution ICO, and shared PNG.")
