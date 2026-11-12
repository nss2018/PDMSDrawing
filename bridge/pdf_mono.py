#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""pdf_mono.py — 把 PDMS 导出的 PDF 转黑白（保矢量）。

背景（2026-10-20）：PDMS 12.1 PLOT 命令的 PLOTSTYLE "MONOCHROME"/"GREYSCALE"/
"BLACK AND WHITE" 对 PDF 导出全部无效（三样式输出像素级相同，实测 0.9839% 彩色
像素），故走后处理：递归改写页面与 Form XObject 内容流中的彩色算子。

规则：
  RGB (rg/RG)：max-min > 0.15（有彩度，如红管线）→ 0 0 0（黑）；近白保持（白遮罩不变黑）
  灰度 (g/G)：保持（已是单色）
  CMYK (k/K)：任一分量 > 0.15 → 0 0 0 1 K/k，否则保持
  scn/SCN（3 操作数 RGB 形式）：同 RGB 规则
用法：pdf_mono.py <in.pdf> [out.pdf]  # 省略 out.pdf 则原位覆盖
"""
import sys, re
import pikepdf

SAT_T = 0.15   # 彩度阈值
CMYK_T = 0.15

def _lum(r, g, b):
    return 0.299 * r + 0.587 * g + 0.114 * b

def mono_bytes(raw: bytes) -> bytes:
    def rgb_op(m):
        r, g, b = float(m.group(1)), float(m.group(2)), float(m.group(3))
        op = m.group(4)
        if (max(r, g, b) - min(r, g, b)) > SAT_T:
            return b"0 0 0 " + op
        # 近白保持；深灰已是单色保持
        return m.group(0)
    raw = re.sub(rb'([\d.]+)[\s]+([\d.]+)[\s]+([\d.]+)[\s]+(rg|RG)\b', rgb_op, raw)

    def cmyk_op(m):
        c_, m_, y_, k_ = (float(m.group(i)) for i in range(1, 5))
        op = m.group(5)
        if max(c_, m_, y_, k_) > CMYK_T:
            return b"0 0 0 1 " + op
        return m.group(0)
    raw = re.sub(rb'([\d.]+)[\s]+([\d.]+)[\s]+([\d.]+)[\s]+([\d.]+)[\s]+(k|K)\b', cmyk_op, raw)

    # scn/SCN：仅处理 3 操作数 RGB 形式（PDMS PDF 未见使用，防御性处理）
    raw = re.sub(rb'([\d.]+)[\s]+([\d.]+)[\s]+([\d.]+)[\s]+(scn|SCN)\b', rgb_op, raw)
    return raw

def scan_stream(stream_obj, depth=0, stats=None):
    if depth > 6:
        return
    try:
        raw = stream_obj.read_bytes()
    except Exception:
        return
    new = mono_bytes(raw)
    if new != raw:
        stream_obj.write(new, filter=pikepdf.Name("/FlateDecode"))
        if stats is not None:
            stats["streams"] += 1
            stats["bytes"] += len(raw)

def walk_xobjects(res, depth=0, stats=None):
    """从 Resources（Page 级或 Form 级）递归所有 Form XObject。"""
    if depth > 6 or res is None:
        return
    try:
        xo = res.XObject
    except Exception:
        return
    for name in xo.keys():
        sub = xo[name]
        if sub.get("/Subtype") == "/Form":
            scan_stream(sub, depth, stats)
            try:
                walk_xobjects(sub.Resources, depth + 1, stats)
            except Exception:
                pass

def main():
    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) > 2 else src
    stats = {"streams": 0, "bytes": 0}
    pdf = pikepdf.open(src, allow_overwriting_input=True)
    for page in pdf.pages:
        c = page.Contents
        if isinstance(c, pikepdf.Array):
            for el in c:
                scan_stream(el, 0, stats)
        else:
            scan_stream(c, 0, stats)
        try:
            walk_xobjects(page.Resources, 0, stats)
        except Exception:
            pass
    pdf.save(dst)
    print("PDF-MONO OK streams=%d bytes=%d -> %s" % (stats["streams"], stats["bytes"], dst))

if __name__ == "__main__":
    main()
