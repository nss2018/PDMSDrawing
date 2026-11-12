# -*- coding: utf-8 -*-
"""MediaBox 裁剪：把 PDF 的 /MediaBox 改为指定矩形（600dpi 像素坐标，原点左上）→ 用 gs 渲染即得局部放大图。
用法: pdf_crop.py in.pdf out.pdf x y w h（px @600dpi）"""
import sys, re

inp, outp, x, y, w, h = sys.argv[1], sys.argv[2], *map(int, sys.argv[3:7])
S = 72.0 / 600.0  # px@600dpi -> pt
d = open(inp, 'rb').read()
# 页高（pt）：取第一个 MediaBox
m = re.search(rb'/MediaBox\s*\[\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*\]', d)
x0, y0, x1, y1 = map(float, m.groups())
PH = y1 - y0
nx0 = x0 + x * S
ny1 = y1 - y * S            # 顶部往下
nx1 = nx0 + w * S
ny0 = ny1 - h * S
nb = b'/MediaBox [%.2f %.2f %.2f %.2f]' % (nx0, ny0, nx1, ny1)
d2 = d[:m.start()] + nb + d[m.end():]
open(outp, 'wb').write(d2)
print('crop pt:', nx0, ny0, nx1, ny1)
