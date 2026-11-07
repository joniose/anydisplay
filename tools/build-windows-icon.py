"""Build the Windows ICO from the repository logo (requires FFmpeg with librsvg)."""
from pathlib import Path
import struct
import subprocess

ROOT = Path(__file__).resolve().parents[1]
SIZES = (16, 20, 24, 32, 40, 48, 64, 256)
logo = ROOT / "docs" / "assets" / "logo.svg"
output = ROOT / "tools" / "AnyDisplay.ico"

images = []
for size in SIZES:
    images.append(subprocess.check_output([
        "ffmpeg", "-v", "error", "-c:v", "librsvg", "-i", str(logo),
        "-vf", f"scale={size}:{size}:flags=lanczos,format=rgba",
        "-frames:v", "1", "-f", "image2pipe", "-c:v", "png", "-"
    ]))

header = bytearray(struct.pack("<HHH", 0, 1, len(images)))
offset = 6 + 16 * len(images)
for size, png in zip(SIZES, images):
    header.extend(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(png), offset))
    offset += len(png)
output.write_bytes(header + b"".join(images))
