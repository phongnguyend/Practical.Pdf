"""Regenerate the deterministic image regression fixture; no third-party packages required."""
from pathlib import Path


def stream(dictionary, data):
    return (f"<< {dictionary} /Length {len(data)} >>\nstream\n".encode()
            + data + b"\nendstream")


objects = [
    b"<< /Type /Catalog /Pages 2 0 R >>",
    b"<< /Type /Pages /Kids [3 0 R 8 0 R] /Count 2 >>",
    b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] "
    b"/Resources << /XObject << /Im 5 0 R /Form 6 0 R >> >> /Contents 4 0 R >>",
    stream("", b"q 40 0 0 20 30 700 cm /Im Do Q\n"
               b"q 0 60 -30 0 200 600 cm /Im Do Q\n"
               b"q 12 0 0 16 50 500 cm BI /W 1 /H 1 /CS /RGB /BPC 8 ID abc EI Q\n"
               b"q 1 0 0 1 250 400 cm /Form Do Q\n"),
    stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 "
           "/ColorSpace /DeviceRGB /BitsPerComponent 8", bytes([255, 0, 0])),
    stream("/Type /XObject /Subtype /Form /BBox [0 0 100 100] "
           "/Matrix [1 0 0 1 10 20] /Resources << /XObject << /Im 5 0 R >> >>",
           b"q 25 0 0 35 0 0 cm /Im Do Q"),
    stream("", b""),
    b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << >> /Contents 7 0 R >>",
]
pdf = bytearray(b"%PDF-1.7\n")
offsets = [0]
for number, obj in enumerate(objects, 1):
    offsets.append(len(pdf))
    pdf.extend(f"{number} 0 obj\n".encode() + obj + b"\nendobj\n")
xref = len(pdf)
pdf.extend(f"xref\n0 {len(offsets)}\n0000000000 65535 f \n".encode())
for offset in offsets[1:]:
    pdf.extend(f"{offset:010} 00000 n \n".encode())
pdf.extend(f"trailer\n<< /Size {len(offsets)} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode())
Path(__file__).with_name("images.pdf").write_bytes(pdf)
