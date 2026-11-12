# -*- coding: utf-8 -*-
"""DXF post-process: mark DEFPOINTS layer as non-plotting.

Root cause (2026-10-26): PDMS `PLOT <sheet> DXF` writes the DEFPOINTS layer
WITHOUT the no-plot flag (DXF LAYER group 70, bit 0x10). That layer carries
the dimension definition points (PDMODE=0 -> drawn as solid dots). Because the
layer is printable, those points show up as small black dots when plotted.

Native AutoCAD's DEFPOINTS layer is non-plotting by default; PDMS' DXF writer
omits the bit. This script restores it at the export source so the produced
DXF is clean before anyone opens or prints it.

Idempotent. Exit code 0 = done (even if nothing changed).
"""
import sys


def nodots(doc, log=print):
    changed = 0
    layers = getattr(doc, "layers", None)
    if layers is None:
        log("nodots: no layers table")
        return 0
    for layer in layers:
        name = (layer.dxf.get("name") or "").upper()
        if name != "DEFPOINTS" and not name.startswith("DEFPOINTS"):
            continue
        try:
            flags = int(layer.dxf.get("flags", 0))
        except Exception:
            flags = 0
        if flags & 16:
            log("nodots: %s already no-plot" % name)
            continue
        try:
            layer.dxf.flags = flags | 16
        except Exception:
            try:
                layer.dxf.plot = 0
            except Exception:
                log("nodots: %s set-flag failed" % name)
                continue
        changed += 1
        log("nodots: %s -> no-plot (flags=%d)" % (name, flags | 16))
    if changed == 0:
        log("nodots: no DEFPOINTS layer changed (already clean or absent)")
    return 0


def main():
    try:
        import ezdxf
    except ImportError:
        print("NEED_EZDXF")
        return 1
    if len(sys.argv) < 2:
        print("usage: dxf_nodots.py <dxf_path>")
        return 2
    dxf_path = sys.argv[1]
    doc = ezdxf.readfile(dxf_path)
    nodots(doc)
    out_path = dxf_path
    try:
        doc.saveas(out_path)
    except PermissionError:
        import os
        root, ext = os.path.splitext(dxf_path)
        out_path = root + "_nodots" + (ext or ".dxf")
        doc.saveas(out_path)
    print("OK saved " + out_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
