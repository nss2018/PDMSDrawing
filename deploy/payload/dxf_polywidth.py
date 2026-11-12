# -*- coding: utf-8 -*-
"""DXF post-process: clear EVERY polyline width before the block pass.

2026-11-12: PDMS PLOT DXF polylines carry 0.25mm constant/vertex widths.
dxf_postengine.py runs polyzero() BEFORE dxf_block wraps entities into
INSERT blocks. A width pass running after block cannot reach those
entities, so EXPLODE in CAD still shows global width 0.25.

NOTE: this file keeps its historical name dxf_polywidth.py because the
EXE embedded-resource list and the engine import both reference it.
The "set a thick width" feature was removed on 2026-11-12 (never used).

Coverage: LWPOLYLINE group 43 + per-vertex 40/41, classic POLYLINE/VERTEX
40/41, entity-level default widths. Scanned via doc.entitydb (modelspace,
paper layouts, named and anonymous blocks). Idempotent, no layer filter.

Usage:
    python dxf_polywidth.py <dxf_path>     -> clear all widths, save in place
"""
import sys


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
        print("usage: dxf_polywidth.py <dxf_path>")
        return 2
    dxf_path = sys.argv[1]
    doc = ezdxf.readfile(dxf_path)
    polyzero(doc)
    out_path = dxf_path
    try:
        doc.saveas(out_path)
    except PermissionError:
        import os
        root, ext = os.path.splitext(dxf_path)
        out_path = root + "_zero" + (ext or ".dxf")
        doc.saveas(out_path)
    print("OK saved " + out_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
