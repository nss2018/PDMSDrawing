# -*- coding: utf-8 -*-
"""DXF 配色后处理（2026-10-24 用户「设备黄色，中心线红色，设备名字紫色」）。

背景: PLOT <sheet> DXF 把 Draft 颜色全部压平成 BYLAYER(256)/白 → CAD 里全白。
本脚本按几何/文本特征回填 ACI 颜色:
  - 设备轮廓（轴对齐矩形回路，无内部文字）→ 黄 ACI 2
  - 设备中心线（GTFDOT/GTCHAIN/GT8004/GTDCHAIN/GTTCHAIN 点划线型）→ 红 ACI 1
  - 设备名字文本（含 POS EL / PDS EL / 已知名称）及其文本框 → 紫 ACI 6
  - 其余（尺寸/轴网/标题栏等）不动

用法:
    python dxf_colorize.py <dxf_path>            # 处理全部非匿名块（跳过「设备一览表」块）
    python dxf_colorize.py <dxf_path> --maxx 900 # 只处理 x < maxx 的实体（避开标题栏）

幂等可重复执行（重刷颜色无副作用）。退出码 0=成功。
"""
import sys
from collections import defaultdict

CENTER_LT = {"GTFDOT", "GTCHAIN", "GT8004", "GTDCHAIN", "GTTCHAIN"}
RED, YELLOW, PURPLE = 1, 2, 6


def _poly_pts(e):
    try:
        return [(v.dxf.location[0], v.dxf.location[1]) for v in e.vertices]
    except Exception:
        return []


def _rect_of(pts, tol=0.6):
    """点列若构成轴对齐矩形回路（含首尾重复/双圈重走），返回 (x0,y0,x1,y1)，否则 None。"""
    if len(pts) < 4:
        return None
    p = list(pts)
    if abs(p[0][0] - p[-1][0]) < tol and abs(p[0][1] - p[-1][1]) < tol:
        p = p[:-1]
    if len(p) < 4:
        return None
    xs = sorted({round(x, 1) for x, _ in p})
    ys = sorted({round(y, 1) for _, y in p})
    if len(xs) != 2 or len(ys) != 2:
        return None
    x0, x1 = xs
    y0, y1 = ys
    for x, y in p:
        if abs(x - x0) > tol and abs(x - x1) > tol:
            return None
        if abs(y - y0) > tol and abs(y - y1) > tol:
            return None
    return (x0, y0, x1, y1)


def colorize(doc, max_x=None, log=print, skip_rect=None):
    polys, texts = [], []
    blocks_seen = []
    # (10y-v3) modelspace entity collection: with native path (dxf_block not enabled),
    # PLOT DXF entities are all in *Model_Space, scanning named blocks only would hit nothing
    for e in doc.modelspace():
        dx = e.dxftype()
        if dx == "POLYLINE":
            polys.append(e)
        elif dx == "TEXT":
            texts.append(e)
    for blk in doc.blocks:
        name = blk.name or ""
        if name.startswith("*"):
            continue
        if "一览表" in name:  # 表格块整体不参与配色（保持原样）
            continue
        blocks_seen.append(name)
        for e in blk:
            dx = e.dxftype()
            if dx == "POLYLINE":
                polys.append(e)
            elif dx == "TEXT":
                texts.append(e)

    # (10y-v3) table protection zone: native path has no "一览表 block" isolation, table cell small rectangles
    # would be falsely colored purple/yellow by the geometric rules below → skip if any point falls inside the rectangle
    def in_skip(e):
        if skip_rect is None:
            return False
        try:
            x0, y0, x1, y1 = skip_rect
        except Exception:
            return False
        pts = None
        try:
            pts = [(v.dxf.location[0], v.dxf.location[1]) for v in e.vertices]
        except Exception:
            pass
        if not pts:
            try:
                pts = [(e.dxf.insert[0], e.dxf.insert[1])]
            except Exception:
                return False
        for px, py in pts:
            if x0 - 0.5 <= px <= x1 + 0.5 and y0 - 0.5 <= py <= y1 + 0.5:
                return True
        return False

    def in_zone(e):
        if in_skip(e):
            return False
        if max_x is None:
            return True
        try:
            bb = e.__weakref__  # noqa
        except Exception:
            pass
        try:
            xs = [v.dxf.location[0] for v in e.vertices]
        except Exception:
            try:
                xs = [e.dxf.insert[0]]
            except Exception:
                return True
        return max(xs) < max_x if xs else True

    # ---- 紫：设备名字文本（POS EL / PDS EL / 名称前缀） ----
    prefixes = set()
    for t in texts:
        s = t.dxf.text or ""
        if " POS EL" in s:
            prefixes.add(s.split(" POS EL")[0].strip())
    n_pur_t = 0
    for t in texts:
        if not in_zone(t):
            continue
        s = t.dxf.text or ""
        if "POS EL" in s or "PDS EL" in s or s.strip() in prefixes:
            t.dxf.color = PURPLE
            n_pur_t += 1

    # ---- 紫：单/双圈文本框（GLABEL 盒子：矩形回路且内部有 TEXT） ----
    tpts = [(t.dxf.insert[0], t.dxf.insert[1]) for t in texts]
    n_pur_box = 0
    box_rects = []
    for e in polys:
        if not in_zone(e):
            continue
        r = _rect_of(_poly_pts(e))
        if r is None:
            continue
        x0, y0, x1, y1 = r
        w, h = x1 - x0, y1 - y0
        if w > 90 or h > 90 or w < 3 or h < 2.5:
            continue
        if any(x0 < px < x1 and y0 < py < y1 for px, py in tpts):
            e.dxf.color = PURPLE
            n_pur_box += 1
            box_rects.append(r)

    def in_box(r):
        for b in box_rects:
            if r[0] >= b[0] - 0.5 and r[1] >= b[1] - 0.5 and r[2] <= b[2] + 0.5 and r[3] <= b[3] + 0.5:
                return True
        return False

    # ---- 黄：矩形回路且无内部文字（设备轮廓，含拼段/连排/双圈） ----
    n_yel = 0
    eq_rects = []
    for e in polys:
        if not in_zone(e):
            continue
        r = _rect_of(_poly_pts(e))
        if r is None or in_box(r):
            continue
        x0, y0, x1, y1 = r
        w, h = x1 - x0, y1 - y0
        if w > 90 or h > 90 or w < 3 or h < 2.5:
            continue
        if any(x0 < px < x1 and y0 < py < y1 for px, py in tpts):
            continue
        try:
            if e.dxf.get("linetype", "") in CENTER_LT:
                continue
        except Exception:
            pass
        e.dxf.color = YELLOW
        n_yel += 1
        eq_rects.append(r)

    # ---- 黄：零散线段拼成的设备矩形（4 条 2 点线段围合） ----
    Hs, Vs = defaultdict(list), defaultdict(list)
    for e in polys:
        if not in_zone(e):
            continue
        try:
            if e.dxf.get("linetype", "") in CENTER_LT:
                continue
        except Exception:
            pass
        pts = _poly_pts(e)
        if len(pts) != 2:
            continue
        (xa, ya), (xb, yb) = pts
        if abs(ya - yb) < 0.6 and abs(xa - xb) > 0.6:
            Hs[round((ya + yb) / 2, 1)].append((min(xa, xb), max(xa, xb), e))
        elif abs(xa - xb) < 0.6 and abs(ya - yb) > 0.6:
            Vs[round((xa + xb) / 2, 1)].append((min(ya, yb), max(ya, yb), e))
    # ---- 黄：零散线段拼成的设备矩形（2 横 + 2 竖线段围合） ----
    n_yel_seg = 0
    used = set()
    hlist = []
    for y, hs in Hs.items():
        for (x0, x1, e) in hs:
            hlist.append((y, x0, x1, e))
    vlist = []
    for x, vs in Vs.items():
        for (y0, y1, e) in vs:
            vlist.append((x, y0, y1, e))
    for i in range(len(hlist)):
        y1v, x0, x1, e1 = hlist[i]
        if id(e1) in used:
            continue
        for j in range(len(hlist)):
            if j == i:
                continue
            y2v, a0, a1, e2 = hlist[j]
            if id(e2) in used or abs(a0 - x0) > 0.6 or abs(a1 - x1) > 0.6:
                continue
            h = abs(y2v - y1v)
            w = x1 - x0
            if not (3 <= w <= 90 and 2.5 <= h <= 90):
                continue
            ry0, ry1 = min(y1v, y2v), max(y1v, y2v)
            vA = vB = None
            for (xv, yy0, yy1, ev) in vlist:
                if abs(xv - x0) <= 0.6 and abs(yy0 - ry0) <= 0.6 and abs(yy1 - ry1) <= 0.6:
                    vA = ev
                elif abs(xv - x1) <= 0.6 and abs(yy0 - ry0) <= 0.6 and abs(yy1 - ry1) <= 0.6:
                    vB = ev
            if vA is None or vB is None:
                continue
            for ev in (e1, e2, vA, vB):
                if id(ev) not in used:
                    ev.dxf.color = YELLOW
                    used.add(id(ev))
                    n_yel_seg += 1
            eq_rects.append((x0, ry0, x1, ry1))

    # ---- 黄：设备矩形内的圆（竖直圆柱轮廓；轴网气泡不在矩形内不受影响） ----
    n_yel_c = 0
    for e in polys:
        if not in_zone(e):
            continue
        pts = _poly_pts(e)
        if len(pts) < 10:
            continue
        xs = [p[0] for p in pts]
        ys = [p[1] for p in pts]
        cx, cy = (min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2
        for (x0, y0, x1, y1) in eq_rects:
            if x0 + 1 < cx < x1 - 1 and y0 + 1 < cy < y1 - 1:
                e.dxf.color = YELLOW
                n_yel_c += 1
                break

    # ---- 红：中心线线型 ----
    n_red = 0
    for e in polys:
        try:
            if e.dxf.get("linetype", "") in CENTER_LT and in_zone(e):
                e.dxf.color = RED
                n_red += 1
        except Exception:
            pass

    log("colorize: 紫(文本)=%d 紫(文本框)=%d 黄(矩形)=%d 黄(拼段)=%d 黄(圆)=%d 红(中心线)=%d blocks=%s"
        % (n_pur_t, n_pur_box, n_yel, n_yel_seg, n_yel_c, n_red, blocks_seen))
    return 0


def main():
    try:
        import ezdxf
    except ImportError:
        print("NEED_EZDXF")
        return 1
    dxf_path = sys.argv[1]
    max_x = None
    skip_rect = None
    if "--maxx" in sys.argv:
        max_x = float(sys.argv[sys.argv.index("--maxx") + 1])
    if "--skiprect" in sys.argv:
        i9 = sys.argv.index("--skiprect")
        skip_rect = tuple(float(v) for v in sys.argv[i9 + 1:i9 + 5])
    doc = ezdxf.readfile(dxf_path)
    colorize(doc, max_x, skip_rect=skip_rect)
    # 目标被 CAD 占用时自动另存 *_彩色.dxf
    out_path = dxf_path
    try:
        doc.saveas(out_path)
    except PermissionError:
        import os
        root, ext = os.path.splitext(dxf_path)
        out_path = root + "_彩色" + (ext or ".dxf")
        doc.saveas(out_path)
    print("OK saved " + out_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
