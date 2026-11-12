# -*- coding: utf-8 -*-
# 解剖 PDF：解压内容流，找红色(RGB/灰度)笔画附近的 setdash ([a b] 0 d) 操作符
import re, zlib, sys

pdf = open(r'D:/pdmsAgent/bridge/out/=16385-1242.pdf', 'rb').read()
print('PDF size', len(pdf))

# 找所有 stream...endcopy 块并尝试 zlib 解压
streams = []
for m in re.finditer(rb'stream\r?\n', pdf):
    start = m.end()
    end = pdf.find(b'endstream', start)
    if end < 0: continue
    raw = pdf[start:end].rstrip(b'\r\n')
    try:
        dec = zlib.decompress(raw)
        streams.append(dec)
    except Exception:
        pass
print('decompressed streams:', len(streams))

dash = re.compile(rb'\[(\d+(?:\.\d+)?)\s+(\d+(?:\.\d+)?)\]\s*0\s*d')
red = re.compile(rb'(?:1\s+0\s+0|0\.8\s+0\s+0|1\s+0\s+0\.4)\s+(?:RG|rg)')  # 常见红
all_dash = re.compile(rb'\[[^\]]*\]\s*\d+\s*d')

total_dash = 0
for si, s in enumerate(streams):
    ds = list(all_dash.finditer(s))
    if ds: total_dash += len(ds)
    # 找红色笔画与其前的 setdash
    for m in re.finditer(rb'([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+(RG|rg)', s):
        r = float(m.group(1))
        if r > 0.7 and float(m.group(2)) < 0.3 and float(m.group(3)) < 0.3:
            seg = s[max(0, m.start()-120):m.start()+60]
            print('RED color op @stream%d: ...%s...' % (si, seg[-140:]))
            break
print('total setdash ops:', total_dash)
# 打印出现频率最高的 dash pattern
from collections import Counter
c = Counter()
for s in streams:
    for m in all_dash.finditer(s):
        c[m.group(0)] += 1
for k, v in c.most_common(8):
    print(v, k)
