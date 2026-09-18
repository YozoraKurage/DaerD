#!/usr/bin/env python3
"""xwd (XWDFile, 24/32bpp direct color) -> PNG. stdlib only."""
import struct, sys, zlib
def main(src, dst):
    b = open(src, 'rb').read()
    f = struct.unpack('>25I', b[:100])
    hdr, w, h = f[0], f[4], f[5]
    byte_order, bpp, bpl = f[7], f[11], f[12]
    rmask, gmask, bmask, ncolors = f[14], f[15], f[16], f[19]
    off = hdr + ncolors * 12
    def shift(m):
        s = 0
        while m and not (m & 1): m >>= 1; s += 1
        return s
    rs, gs, bs = shift(rmask), shift(gmask), shift(bmask)
    Bpp = bpp // 8
    fmt = '>' if byte_order == 1 else '<'
    rows = []
    for y in range(h):
        row = bytearray([0])
        base = off + y * bpl
        for x in range(w):
            px = b[base + x * Bpp: base + x * Bpp + Bpp]
            v = int.from_bytes(px, 'big' if byte_order == 1 else 'little')
            row += bytes(((v & rmask) >> rs & 0xff, (v & gmask) >> gs & 0xff, (v & bmask) >> bs & 0xff))
        rows.append(bytes(row))
    def chunk(t, d):
        c = t + d
        return struct.pack('>I', len(d)) + c + struct.pack('>I', zlib.crc32(c) & 0xffffffff)
    png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)) \
        + chunk(b'IDAT', zlib.compress(b''.join(rows), 6)) + chunk(b'IEND', b'')
    open(dst, 'wb').write(png)
    print(f'{w}x{h} bpp={bpp} order={"MSB" if byte_order else "LSB"} -> {dst}')
main(sys.argv[1], sys.argv[2])
