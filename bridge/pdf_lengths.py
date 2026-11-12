# -*- coding: utf-8 -*-
# 按颜色汇总 PDF 笔画总长度与段数（m/l/S 路径）
import re, zlib
from collections import Counter

pdf = open(r'D:/pdmsAgent/bridge/out/=16385-1242.pdf', 'rb').read()
streams = []
for m in re.finditer(rb'stream\r?\n', pdf):
    start = m.end()
    end = pdf.find(b'endstream', start)
    if end < 0: continue
    try: streams.append(zlib.decompress(pdf[start:end].rstrip(b'\r\n')))
    except Exception: pass

# 逐流解析：跟踪当前颜色，遇 m...l...S 累加段长
seg_count = Counter(); total_len = Counter()
pt = re.compile(rb'(-?[\d.]+)\s+(-?[\d.]+)\s+(m|l)\b|(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(RG|rg)')
for s in streams:
    cur = None  # (r,g,b)
    path = []
    i = 0
    toks = re.finditer(rb'(-?[\d.]+)\s+(-?[\d.]+)\s+(m|l)\b|(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(RG|rg)\b|S\b', s)
    for t in toks:
        if t.group(3):  # m or l
            path.append((float(t.group(1)), float(t.group(2)), t.group(3).decode()))
        elif t.group(7):  # color
            cur = (float(t.group(4)), float(t.group(5)), float(t.group(6)))
        elif t.group(0) == b'S':
            # stroke path
            key = None
            if cur:
                r, g, b = cur
                if r > 0.5 and g < 0.3 and b < 0.3: key = 'RED'
                elif r > 0.8 and g > 0.7 and b < 0.3: key = 'YELLOW'
                elif r < 0.3 and g < 0.3 and b < 0.3: key = 'BLACK'
                else: key = 'other(%.2f,%.2f,%.2f)' % cur
            else:
                key = 'unspecified'
            for j in range(1, len(path)):
                if path[j][2] == 'l' and path[j-1][2] in ('m','l'):
                    dx = path[j][0]-path[j-1][0]; dy = path[j][1]-path[j-1][1]
                    L = (dx*dx+dy*dy) ** 0.5
                    seg_count[key] += 1
                    total_len[key] += L
            path = []

print('%-12s %8s %12s' % ('color', 'segments', 'total_len(pt)'))
for k in sorted(set(seg_count) | set(total_len), key=lambda x: -total_len[x]):
    print('%-12s %8d %12.0f' % (k, seg_count[k], total_len[k]))
