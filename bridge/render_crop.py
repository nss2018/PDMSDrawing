# -*- coding: utf-8 -*-
"""pypdfium2 局部渲染：render_crop.py in.pdf out.png x0 y0 x1 y1 [dpi]  (pt 矩形, 左下原点)"""
import sys
import pypdfium2 as pdfium
inp, outp = sys.argv[1], sys.argv[2]
x0, y0, x1, y1 = map(float, sys.argv[3:7])
dpi = float(sys.argv[7]) if len(sys.argv) > 7 else 600.0
pdf = pdfium.PdfDocument(inp)
page = pdf[0]
W, H = page.get_size()
img = page.render(scale=dpi/72.0, crop=(x0, y0, W - x1, H - y1)).to_pil()
img.save(outp)
print(outp, img.size)
