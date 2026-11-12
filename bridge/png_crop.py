# -*- coding: utf-8 -*-
"""纯标准库 PNG 裁剪器（8-bit RGB/RGBA，无隔行）。用法: png_crop.py in.png out.png x y w h"""
import sys, zlib, struct

def read_png(path):
    data = open(path, 'rb').read()
    assert data[:8] == b'\x89PNG\r\n\x1a\n', 'not png'
    pos, idat, meta = 8, b'', None
    while pos < len(data):
        ln, typ = struct.unpack('>I4s', data[pos:pos+8]); pos += 8
        chunk = data[pos:pos+ln]; pos += ln + 4
        if typ == b'IHDR':
            w, h, bd, ct, comp, filt, inter = struct.unpack('>IIBBBBB', chunk)
            meta = (w, h, bd, ct, inter)
        elif typ == b'IDAT':
            idat += chunk
        elif typ == b'IEND':
            break
    w, h, bd, ct, inter = meta
    assert bd == 8 and inter == 0, 'unsupported bitdepth/interlace: %r' % (meta,)
    ch = {0:1, 2:3, 4:2, 6:4}[ct]
    raw = zlib.decompress(idat)
    stride = w * ch
    out = bytearray(h * stride)
    prev = bytearray(stride)
    p = 0
    for y in range(h):
        f = raw[p]; p += 1
        line = bytearray(raw[p:p+stride]); p += stride
        if f == 1:
            for i in range(ch, stride): line[i] = (line[i] + line[i-ch]) & 0xFF
        elif f == 2:
            for i in range(stride): line[i] = (line[i] + prev[i]) & 0xFF
        elif f == 3:
            for i in range(stride):
                a = line[i-ch] if i >= ch else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif f == 4:
            for i in range(stride):
                a = line[i-ch] if i >= ch else 0
                b = prev[i]
                c = prev[i-ch] if i >= ch else 0
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        out[y*stride:(y+1)*stride] = line
        prev = line
    return w, h, ch, ct, out

def write_png(path, w, h, ch, ct, rows):
    ct_out = 2 if ch == 3 else (6 if ch == 4 else ct)
    stride = w * ch
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw += rows[y*stride:(y+1)*stride]
    def chunk(typ, data):
        c = struct.pack('>I', len(data)) + typ + data
        return c + struct.pack('>I', zlib.crc32(typ + data) & 0xFFFFFFFF)
    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, ct_out, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(bytes(raw), 6))
    png += chunk(b'IEND', b'')
    open(path, 'wb').write(png)

if __name__ == '__main__':
    inp, outp = sys.argv[1], sys.argv[2]
    x, y, w, h = map(int, sys.argv[3:7])
    W, H, ch, ct, px = read_png(inp)
    x = max(0, min(x, W-1)); y = max(0, min(y, H-1))
    w = min(w, W - x); h = min(h, H - y)
    stride = W * ch
    rows = bytearray(w * ch * h)
    for r in range(h):
        src = (y + r) * stride + x * ch
        rows[r*w*ch:(r+1)*w*ch] = px[src:src + w*ch]
    write_png(outp, w, h, ch, ct, rows)
    print('cropped', w, 'x', h, '->', outp)
