# -*- coding: utf-8 -*-
"""DXF post-process: set constant width on polylines of target layers.

User story (2026-10-26): PDMS PLOT DXF polylines print hairline-thin. User
manually selected GT_1 layer polylines in CAD and set global width = 1.0,
which plots with visible thickness. This script reproduces that at export
time so the produced DXF needs no manual touch.

Notes:
  - LWPOLYLINE: constant width via dxf.const_width (group 43)
  - classic POLYLINE: no entity-level attrib in ezdxf -> set per-vertex
    start_width/end_width (group 40/41) on every VERTEX instead

Usage:
    python dxf_polywidth.py <dxf_path> [width] [layer1,layer2,...]
    width  default 1.0
    layers default GT_1  (comma separated, exact match, case-insensitive)
    python dxf_polywidth.py <dxf_path> zero   -> polyzero(): clear ALL widths

polyzero (2026-11-12): PDMS PLOT DXF polylines carry 0.25mm widths. The
block pass moves them into INSERT blocks, so a width pass running AFTER
block cannot reach them and EXPLODE in CAD still shows global width 0.25.
polyzero() must run BEFORE block and scans the whole entity database.

Idempotent. Exit code 0 = done.
"""
import sys


def polywidth(doc, width=1.0, layers=("GT_1",), log=print):
    want = {s.strip().upper() for s in layers if s.strip()}
    changed = 0

    def handle(e):
        nonlocal changed
        t = e.dxftype()
        lay = (e.dxf.get("layer") or "").upper()
        if lay not in want:
            return
        if t == "LWPOLYLINE":
            try:
                cur = float(e.dxf.get("const_width", 0) or 0)
            except Exception:
                cur = 0.0
            if abs(cur - width) < 1e-9:
                return
            e.dxf.const_width = width
            changed += 1
        elif t == "POLYLINE":
            n = 0
            for v in e.vertices:
                try:
                    sw = float(v.dxf.get("start_width", 0) or 0)
                    ew = float(v.dxf.get("end_width", 0) or 0)
                    if abs(sw - width) < 1e-9 and abs(ew - width) < 1e-9:
                        continue
                except Exception:
                    pass
                try:
                    v.dxf.start_width = width
                    v.dxf.end_width = width
                    n += 1
                except Exception:
                    pass
            if n:
                changed += 1

    for e in doc.modelspace():
        handle(e)
    for blk in doc.blocks:
        if (blk.name or "").startswith("*"):
            continue
        for e in blk:
            handle(e)
    log("polywidth: width=%s layers=%s -> %d polylines updated" % (width, sorted(want), changed))
    return 0


def polyzero(doc, log=print):
    """Clear EVERY polyline width (global + per-vertex) before the block pass.

    Covers LWPOLYLINE group 43 (const_width) and group 40/41 on every vertex,
    classic POLYLINE/VERTEX group 40/41, and entity-level default widths.
    Scans doc.entitydb (modelspace, paper layouts, named and anonymous
    blocks) so entities later moved into INSERT blocks are already zeroed.
    Idempotent; no layer filter on purpose.
    """
    changed = 0
    hit_e = 0

    def _zero_dxf(e, names):
        n = 0
        for attr in names:
            try:
                if e.dxf.hasattr(attr) and abs(float(e.dxf.get(attr, 0) or 0)) > 1e-9:
                    e.dxf.set(attr, 0.0)
                    n += 1
            except Exception:
                pass
        return n

    def handle(e):
        nonlocal changed, hit_e
        t = e.dxftype()
        n = 0
        if t == "LWPOLYLINE":
            n += _zero_dxf(e, ("const_width",))
            try:
                pts = list(e.get_points("xyseb"))
                dirty = False
                for i, p in enumerate(pts):
                    if abs(float(p[2] or 0)) > 1e-9 or abs(float(p[3] or 0)) > 1e-9:
                        pts[i] = (p[0], p[1], 0.0, 0.0, p[4])
                        dirty = True
                if dirty:
                    e.set_points(pts, format="xyseb")
                    n += 1
            except Exception:
                pass
        elif t == "POLYLINE":
            n += _zero_dxf(e, ("start_width", "end_width",
                               "default_start_width", "default_end_width"))
            try:
                for v in e.vertices:
                    n += _zero_dxf(v, ("start_width", "end_width"))
            except Exception:
                pass
        elif t == "VERTEX":
            # standalone VERTEX in entitydb (R12 compound POLYLINE)
            n += _zero_dxf(e, ("start_width", "end_width"))
        if n:
            changed += n
            hit_e += 1

    ents = []
    try:
        ents = list(doc.entitydb.values())
    except Exception:
        containers = []
        for getter in (lambda: [doc.modelspace()],
                       lambda: list(doc.blocks),
                       lambda: list(doc.layouts)):
            try:
                containers.extend(getter())
            except Exception:
                pass
        for c in containers:
            try:
                ents.extend(list(c))
            except Exception:
                pass
    seen = set()
    for e in ents:
        try:
            k = id(e)
            if k in seen:
                continue
            seen.add(k)
            handle(e)
        except Exception:
            pass
    log("polyzero: %d entities -> width 0 (%d attribs, all containers, before block)"
        % (hit_e, changed))
    return hit_e


def main():
    try:
        import ezdxf
    except ImportError:
        print("NEED_EZDXF")
        return 1
    if len(sys.argv) < 2:
        print("usage: dxf_polywidth.py <dxf_path> [width] [layers]")
        return 2
    dxf_path = sys.argv[1]
    doc = ezdxf.readfile(dxf_path)
    if len(sys.argv) > 2 and sys.argv[2].strip().lower() == "zero":
        polyzero(doc)
    else:
        width = float(sys.argv[2]) if len(sys.argv) > 2 else 1.0
        layers = sys.argv[3].split(",") if len(sys.argv) > 3 else ["GT_1"]
        polywidth(doc, width, layers)
    out_path = dxf_path
    try:
        doc.saveas(out_path)
    except PermissionError:
        import os
        root, ext = os.path.splitext(dxf_path)
        out_path = root + "_width" + (ext or ".dxf")
        doc.saveas(out_path)
    print("OK saved " + out_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
