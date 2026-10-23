"""Generate our tiny test font: A is a rectangle and B is a triangle. No third-party font data."""
from pathlib import Path
import struct


def u16(value):
    return struct.pack('>H', value)


def glyph(points):
    data = struct.pack('>hhhhhHH', 1, 0, 0, 500, 700, len(points) - 1, 0)
    data += bytes([1] * len(points))
    for axis in (0, 1):
        last = 0
        for point in points:
            data += struct.pack('>h', point[axis] - last)
            last = point[axis]
    return data + bytes(len(data) % 2)


def checksum(data):
    data += bytes((-len(data)) % 4)
    return sum(struct.unpack('>' + 'I' * (len(data) // 4), data)) & 0xffffffff


rectangle = glyph([(0, 0), (500, 0), (500, 700), (0, 700)])
triangle = glyph([(0, 0), (500, 0), (250, 700)])
head = bytearray(54)
struct.pack_into('>IIIIHH', head, 0, 0x10000, 0x10000, 0, 0x5f0f3cf5, 3, 1000)
struct.pack_into('>hhhhHHhhh', head, 36, 0, 0, 500, 700, 0, 8, 2, 0, 0)
hhea = bytearray(36)
struct.pack_into('>IhhhHhhh', hhea, 0, 0x10000, 800, -200, 0, 600, 0, 100, 500)
struct.pack_into('>h', hhea, 18, 1)
struct.pack_into('>H', hhea, 34, 3)
maxp = bytearray(32)
struct.pack_into('>IHHH', maxp, 0, 0x10000, 3, 4, 1)
struct.pack_into('>H', maxp, 14, 2)
os2 = bytearray(78)
struct.pack_into('>HhHH', os2, 0, 0, 600, 400, 5)
os2[58:62] = b'TEST'
struct.pack_into('>HHHhhhHH', os2, 62, 64, 65, 66, 800, -200, 0, 800, 200)
post = bytearray(32)
struct.pack_into('>I', post, 0, 0x30000)
names = ['PdfKitTest', 'Regular', 'PdfKitTest-Regular', 'PdfKitTest Regular', 'Version 1.0', 'PdfKitTest-Regular']
strings = b''
records = b''
for index, name in enumerate(names, 1):
    encoded = name.encode('utf-16-be')
    records += struct.pack('>HHHHHH', 3, 1, 0x409, index, len(encoded), len(strings))
    strings += encoded
subtable = struct.pack('>HHHHHHH', 4, 32, 0, 4, 4, 1, 0)
subtable += struct.pack('>HHHHHHHHH', 66, 0xffff, 0, 65, 0xffff, 0xffc0, 1, 0, 0)
tables = {
    'head': bytes(head), 'hhea': bytes(hhea), 'maxp': bytes(maxp),
    'hmtx': struct.pack('>HhHhHh', 600, 0, 600, 0, 600, 0),
    'glyf': rectangle + triangle,
    'loca': b''.join(u16(value // 2) for value in [0, 0, len(rectangle), len(rectangle) + len(triangle)]),
    'cmap': struct.pack('>HHHHI', 0, 1, 3, 1, 12) + subtable,
    'name': struct.pack('>HHH', 0, 6, 78) + records + strings,
    'OS/2': bytes(os2), 'post': bytes(post),
}
count = len(tables)
power = 2 ** (count.bit_length() - 1)
output = bytearray(struct.pack('>IHHHH', 0x10000, count, power * 16, power.bit_length() - 1, (count - power) * 16))
offset = 12 + count * 16
payload = b''
head_offset = 0
for tag, data in sorted(tables.items()):
    output += tag.encode('ascii') + struct.pack('>III', checksum(data), offset, len(data))
    if tag == 'head':
        head_offset = offset
    padded = data + bytes((-len(data)) % 4)
    payload += padded
    offset += len(padded)
output += payload
struct.pack_into('>I', output, head_offset + 8, (0xb1b0afba - checksum(output)) & 0xffffffff)
Path(__file__).with_name('RenderingTest.ttf').write_bytes(output)
