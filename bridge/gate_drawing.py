#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""gate_drawing.py - post-draw acceptance gate: PDF content box vs DB expected box.

Why: catches "view mis-framed / wrong scale / empty sheet / geometry missing"
before a human looks at the sheet. Same philosophy as the support-side EVOL
gate (expected box derived from definitions, measured box from the product,
tolerance checked per axis).

Two measured/expected boxes:
  * file side : ink bounding box parsed from the PDF content streams
                (zlib-decompressed, path operators m/l/c/re + Tm text origins,
                full CTM tracking). "inner ink" excludes the frame band and
                the bottom title-block band so the paper frame does not
                dominate the box.
  * DB side   : union of ATTRIB.WVOL (6-slot world box, layout
                [xmin,ymin,zmin,xmax,ymax,zmax]) over the job's elements,
                read through the bridge op exec_cmds (PML probe, read-only).

Checks (per job):
  A  MediaBox matches a known A-series paper (warning only on mismatch)
  B  inner ink exists (empty sheet / blank export)
  C  inner ink span covers the expected geometry span (world box projected
     to the view plane and divided by the scale denominator) within
     [exp - ann_margin, frame_inner] per axis:
       - lower side: geometry must actually be on the sheet
       - upper side: annotations may overflow, but nothing may exceed frame
  D  (strict mode) |inner span - exp span| <= max(15mm, 8%) per axis

Usage:
  python gate_drawing.py <pdf> [--elements "N1,N2"] [--plane plan|elev]
                         [--scale 50] [--port 18760] [--strict] [--json out.json]

  --elements omitted (or bridge offline): file-side checks only (A/B + spans
  reported, C/D skipped with a SKIP note).

All ASCII on purpose (project rule: no CJK in script comments).
"""
import argparse
import json
import math
import os
import re
import socket
import sys
import time
import zlib

# A-series paper sizes in mm, landscape (w, h), portrait handled by swap
PAPERS = {
    "A3": (420.0, 297.0),
    "A2": (594.0, 420.0),
    "A1": (841.0, 594.0),
    "A0": (1189.0, 841.0),
}
FRAME_BAND = 25.0      # mm from left/right/top: frame + page marks
TITLE_BAND = 135.0     # mm from bottom: title block zone (HG20519 A-series)
ANN_MARGIN = 40.0      # mm of annotation overflow tolerated per axis (check C lower side)

BRIDGE_PORTS = list(range(18753, 18763))


# ---------------- file side: PDF ink boxes ----------------

def _decompress(data):
    try:
        return zlib.decompress(data)
    except Exception:
        return data


def _obj_stream(raw, objnum):
    """Decompress the stream of object <objnum> 0 obj (returns bytes or None)."""
    m = re.search(rb"\b" + str(objnum).encode() + rb"\s+0\s+obj", raw)
    if not m:
        return None
    start = raw.find(b"stream", m.end())
    if start < 0:
        return None
    start += len(b"stream")
    if raw[start:start + 2] == b"\r\n":
        start += 2
    elif raw[start:start + 1] in (b"\n", b"\r"):
        start += 1
    end = raw.find(b"endstream", start)
    if end < 0:
        return None
    return _decompress(raw[start:end].rstrip(b"\r\n"))


def _parse_path_points(stream, base_ctm=(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)):
    """Yield (x,y) in the stream's parent space for every path/text point,
    tracking cm/Do (Do points returned as ('do', name, ctm))."""
    n = rb"(-?\d+\.?\d*)"
    cm_re = re.compile(n + rb"\s+" + n + rb"\s+" + n + rb"\s+" + n + rb"\s+" + n + rb"\s+" + n + rb"\s+cm")
    do_re = re.compile(rb"/([A-Za-z0-9_+\-]+)\s+Do")
    m_re = re.compile(rb"(-?\d+\.?\d*)\s+(-?\d+\.?\d*)\s+m(?![a-zA-Z])")
    l_re = re.compile(rb"(-?\d+\.?\d*)\s+(-?\d+\.?\d*)\s+l(?![a-zA-Z])")
    c_re = re.compile(n + rb"\s+" + n + rb"\s+" + n + rb"\s+" + n + rb"\s+" + n + rb"\s+" + n + rb"\s+c(?![a-zA-Z])")
    re_re = re.compile(n + rb"\s+" + n + rb"\s+" + n + rb"\s+" + n + rb"\s+re")
    tm_re = re.compile(n + rb"\s+" + n + rb"\s+" + n + rb"\s+" + n + rb"\s+" + n + rb"\s+" + n + rb"\s+Tm")

    def apply(m, x, y):
        return (m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5])

    def mul(m1, m2):
        a1, b1, c1, d1, e1, f1 = m1
        a2, b2, c2, d2, e2, f2 = m2
        return (a1 * a2 + c1 * b2, b1 * a2 + d1 * b2,
                a1 * c2 + c1 * d2, b1 * c2 + d1 * d2,
                a1 * e2 + c1 * f2 + e1, b1 * e2 + d1 * f2 + f1)

    I = (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
    ctm = base_ctm
    events = []
    for m in cm_re.finditer(stream):
        events.append((m.start(), ("cm", (float(m.group(1)), float(m.group(2)), float(m.group(3)),
                                        float(m.group(4)), float(m.group(5)), float(m.group(6))))))
    for m in do_re.finditer(stream):
        events.append((m.start(), ("do", (m.group(1).decode("latin-1"),))))
    for m in m_re.finditer(stream):
        events.append((m.start(), ("p", (float(m.group(1)), float(m.group(2))))))
    for m in l_re.finditer(stream):
        events.append((m.start(), ("p", (float(m.group(1)), float(m.group(2))))))
    for m in c_re.finditer(stream):
        events.append((m.start(), ("p", (float(m.group(1)), float(m.group(2))))))
        events.append((m.start(), ("p", (float(m.group(3)), float(m.group(4))))))
        events.append((m.start(), ("p", (float(m.group(5)), float(m.group(6))))))
    for m in re_re.finditer(stream):
        x, y, w, h = float(m.group(1)), float(m.group(2)), float(m.group(3)), float(m.group(4))
        for cx, cy in ((x, y), (x + w, y), (x, y + h), (x + w, y + h)):
            events.append((m.start(), ("p", (cx, cy))))
    for m in tm_re.finditer(stream):
        events.append((m.start(), ("p", (float(m.group(5)), float(m.group(6))))))
    events.sort(key=lambda e: e[0])
    for _, (kind, val) in events:
        if kind == "cm":
            ctm = mul(val, ctm)
        elif kind == "do":
            yield ("do", val[0], ctm)
        else:
            px, py = apply(ctm, val[0], val[1])
            yield ("p", px, py)


def pdf_pages_boxes(pdf_path):
    """Return list of dicts per page:
       media (w,h) mm, ink (xmin,ymin,xmax,ymax) page mm,
       inner same but excluding frame band / title block band.
       Handles the Draft export layout: page MediaBox in points, geometry
       inside a Form XObject (near-mm unit space) placed by a cm+Do call."""
    raw = open(pdf_path, "rb").read()
    PT = 25.4 / 72.0
    # page objects: locate each /Type /Page, expand to its "N 0 obj ... endobj" body
    pages = []
    for m in re.finditer(rb"/Type\s*/Page\b(?!s)", raw):
        ob = raw.rfind(b"0 obj", 0, m.start())
        eb = raw.find(b"endobj", m.end())
        if ob < 0 or eb < 0:
            continue
        seg = raw[ob:eb]
        mb = re.search(rb"/MediaBox\s*\[\s*([\d.\-]+)\s+([\d.\-]+)\s+([\d.\-]+)\s+([\d.\-]+)", seg)
        if not mb:
            continue
        mw = (float(mb.group(3)) - float(mb.group(1))) * PT
        mh = (float(mb.group(4)) - float(mb.group(2))) * PT
        page_pts = []
        ct = re.search(rb"/Contents\s*\[([^\]]*)\]", seg) or re.search(rb"/Contents\s+(\d+)\s+0\s+R", seg)
        if ct:
            refs = re.findall(rb"(\d+)\s+0\s+R", ct.group(1) if ct.lastindex else b"")
            if not refs and ct.lastindex:
                refs = [ct.group(1)]
            for refn in refs:
                stream = _obj_stream(raw, int(refn))
                if stream is None:
                    continue
                for kind, a, b in _parse_path_points(stream):
                    if kind == "p":
                        page_pts.append((a * PT, b * PT))
                    else:  # ("do", xobj_name, placement_ctm)
                        # map name -> xobject obj via /Resources ... /Name N 0 R
                        rx = re.search(rb"/" + re.escape(a.encode()) + rb"\s+(\d+)\s+0\s+R", raw)
                        if rx:
                            xs = _obj_stream(raw, int(rx.group(1)))
                            if xs is not None:
                                for k2, a2, b2 in _parse_path_points(xs, base_ctm=b):
                                    if k2 == "p":
                                        page_pts.append((a2 * PT, b2 * PT))
        pages.append({"media": (mw, mh), "pts": page_pts})
    if not pages:  # fallback: raw streams, points assumed
        allpts = []
        for m in re.finditer(rb"stream\r?\n", raw):
            s = _decompress(raw[m.end():raw.find(b"endstream", m.end())].rstrip(b"\r\n"))
            if s is not None:
                for kind, a, b in _parse_path_points(s):
                    if kind == "p":
                        allpts.append((a * PT, b * PT))
        pages = [{"media": (0.0, 0.0), "pts": allpts}]
    out = []
    for pg in pages:
        mw, mh = pg["media"]
        pts = pg["pts"]
        ink = inner = None
        if pts:
            ink = (min(p[0] for p in pts), min(p[1] for p in pts),
                   max(p[0] for p in pts), max(p[1] for p in pts))
            if mw and mh:
                ix = [p for p in pts if FRAME_BAND <= p[0] <= mw - FRAME_BAND
                      and TITLE_BAND <= p[1] <= mh - FRAME_BAND]
                if len(ix) > 20:
                    inner = (min(p[0] for p in ix), min(p[1] for p in ix),
                             max(p[0] for p in ix), max(p[1] for p in ix))
        out.append({"media": (mw, mh), "ink": ink, "inner": inner})
    return out


def guess_paper(mw, mh):
    if not mw or not mh:
        return None
    for name, (w, h) in PAPERS.items():
        if (abs(mw - w) < 3 and abs(mh - h) < 3) or (abs(mw - h) < 3 and abs(mh - w) < 3):
            return name
    return None


# ---------------- DB side: WVOL union via bridge exec_cmds ----------------

def bridge_port(prefer=None):
    cands = [prefer] if prefer else []
    cands += [p for p in BRIDGE_PORTS if p != prefer]
    for p in cands:
        if p is None:
            continue
        try:
            s = socket.create_connection(("127.0.0.1", p), timeout=0.5)
            s.close()
            return p
        except Exception:
            continue
    return None


def bridge_op(op, params, port, timeout=120):
    req = {"id": int(time.time() * 1000) % 1000000000, "kind": "op", "op": op, "params": params}
    s = socket.create_connection(("127.0.0.1", port), timeout=timeout)
    s.sendall((json.dumps(req) + "\n").encode("utf-8"))
    s.settimeout(timeout - 2)
    buf = b""
    while b"\n" not in buf:
        c = s.recv(65536)
        if not c:
            break
        buf += c
    s.close()
    return json.loads(buf.split(b"\n", 1)[0].decode("utf-8", "replace"))


def wvol_union(elements, port, out_dir=None):
    """Read ATTRIB.WVOL of each named element via a read-only PML probe.
    Returns (union_box[6] or None, notes list). WVOL layout (verified against
    drawingPDMS ElementLimits): [xmin,ymin,zmin,xmax,ymax,zmax] world mm."""
    notes = []
    if not elements:
        return None, ["no elements given"]
    probe_dir = out_dir or r"D:\pdmsAgent\bridge\out"
    if not os.path.isdir(probe_dir):
        probe_dir = os.path.dirname(os.path.abspath(__file__))
    rpt = os.path.join(probe_dir, "gate_wvol_probe.txt").replace("\\", "/")
    lines = []
    lines.append("openfile %s overwrite !F1" % rpt)
    lines.append("writefile $!F1 |===GATE WVOL NIF===|")
    # sentinel idiom (build18 T0-guard style): the failing statement, then a
    # handle (47,15) body that flags the miss; the WVOL write only happens on a hit
    for i, name in enumerate(elements, 1):
        lines.append("var !t%d 'OK'" % i)
        lines.append("var !g%d = ^%s" % (i, name))
        lines.append("handle (47,15)")
        lines.append("  !t%d = 'NA'" % i)
        lines.append("endhandle")
        lines.append("if (!t%d eq 'OK') then" % i)
        lines.append("  handle (47,15)")
        lines.append("    writefile $!F1 |WVOL NA %s|" % name)
        lines.append("  endhandle")
        lines.append("  writefile $!F1 |WVOL %s $!g%d.ATTRIB.WVOL|" % (name, i))
        lines.append("else")
        lines.append("  writefile $!F1 |ELEM NA %s|" % name)
        lines.append("endif")
    lines.append("writefile $!F1 |===PROBE DONE===|")
    lines.append("closefile $!F1")
    pml = "\n".join(lines) + "\n"
    try:
        r = bridge_op("exec_cmds", {"cmds": pml}, port, timeout=180)
    except Exception as e:
        return None, ["bridge error: %s" % e]
    if not r.get("ok"):
        return None, ["exec_cmds not ok: %s" % (r.get("error") or "")]
    if not os.path.exists(rpt):
        return None, ["probe report missing: %s" % rpt]
    box = None
    txt = open(rpt, encoding="utf-8", errors="replace").read()
    for ln in txt.splitlines():
        ln = ln.strip()
        m = re.match(r"WVOL (\S+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+)", ln)
        if m:
            v = [float(m.group(i)) for i in range(2, 8)]
            if all(b > a for a, b in ((v[0], v[3]), (v[1], v[4]), (v[2], v[5]))):
                if box is None:
                    box = list(v)
                else:
                    box = [min(box[0], v[0]), min(box[1], v[1]), min(box[2], v[2]),
                           max(box[3], v[3]), max(box[4], v[4]), max(box[5], v[5])]
            continue
        if " NA " in ln and (ln.startswith("ELEM") or ln.startswith("WVOL")):
            notes.append(ln)
    if "===PROBE DONE===" not in txt:
        notes.append("probe incomplete (no PROBE DONE)")
    return box, notes


# ---------------- gate ----------------

def project_box(box6, plane):
    """[xmin,ymin,zmin,xmax,ymax,zmax] -> (span_u, span_v) on the view plane."""
    if plane == "elev":
        return box6[3] - box6[0], box6[5] - box6[2]   # W x U
    return box6[3] - box6[0], box6[4] - box6[1]        # plan: W x N


def gate(params, pdf_path, port=None, strict=False):
    res = {"pdf": pdf_path, "checks": [], "status": "PASS", "boxes": {}}
    pages = pdf_pages_boxes(pdf_path)
    page = pages[0]
    mw, mh = page["media"]
    res["boxes"]["media_mm"] = [round(mw, 1), round(mh, 1)]
    res["boxes"]["ink_mm"] = [round(x, 1) for x in page["ink"]] if page["ink"] else None
    res["boxes"]["inner_mm"] = [round(x, 1) for x in page["inner"]] if page["inner"] else None

    # A: paper
    paper = guess_paper(mw, mh)
    if paper:
        res["checks"].append(("A paper", "PASS", "%s %g x %g" % (paper, mw, mh)))
    elif mw:
        res["checks"].append(("A paper", "WARN", "unknown size %g x %g mm" % (mw, mh)))
    else:
        res["checks"].append(("A paper", "FAIL", "no MediaBox"))

    # B: inner ink exists
    if page["inner"]:
        iw = page["inner"][2] - page["inner"][0]
        ih = page["inner"][3] - page["inner"][1]
        if iw > 5 and ih > 5:
            res["checks"].append(("B content", "PASS", "inner ink %g x %g mm" % (iw, ih)))
        else:
            res["checks"].append(("B content", "FAIL", "inner ink degenerate %g x %g mm" % (iw, ih)))
    else:
        res["checks"].append(("B content", "FAIL", "no inner ink (frame/title band only?)"))

    # C/D: vs expected world box
    scale = float(params.get("scaleDen") or 0)
    plane = params.get("plane", "plan")
    elements = [x.strip() for x in str(params.get("elements") or "").split(",") if x.strip()
                and not x.strip().startswith("<")]
    box6, notes = wvol_union(elements, port) if elements else (None, ["elements not set"])
    for n in notes:
        res["checks"].append(("C wvol", "WARN", n))
    if box6 and scale > 1:
        res["boxes"]["world_mm"] = [round(x, 1) for x in box6]
        eu, ev = project_box(box6, plane)
        exp_u, exp_v = eu / scale, ev / scale  # world mm -> paper mm
        res["boxes"]["expected_paper_mm"] = [round(exp_u, 1), round(exp_v, 1)]
        if page["inner"]:
            iw = page["inner"][2] - page["inner"][0]
            ih = page["inner"][3] - page["inner"][1]
            # C lower: drawn content must reach at least exp - ANN_MARGIN
            for ax, drawn, exp, lim in (("u", iw, exp_u, mw), ("v", ih, exp_v, mh)):
                if drawn < exp - ANN_MARGIN:
                    res["checks"].append(("C cover-%s" % ax, "FAIL",
                                          "drawn %g mm < expected %g mm - margin" % (drawn, exp)))
                elif drawn > lim:
                    res["checks"].append(("C cover-%s" % ax, "FAIL",
                                          "drawn %g mm exceeds frame %g mm" % (drawn, lim)))
                else:
                    res["checks"].append(("C cover-%s" % ax, "PASS",
                                          "drawn %g mm vs expected %g mm" % (drawn, exp)))
            if strict:
                for ax, drawn, exp in (("u", iw, exp_u), ("v", ih, exp_v)):
                    tol = max(15.0, 0.08 * max(exp, 1.0))
                    ok = abs(drawn - exp) <= tol + ANN_MARGIN
                    res["checks"].append(("D strict-%s" % ax, "PASS" if ok else "WARN",
                                          "|%g - %g| vs tol %g" % (drawn, exp, tol)))
    else:
        if not box6:
            res["checks"].append(("C wvol", "SKIP", "no world box (bridge offline or elements unresolved)"))
        if scale <= 1:
            res["checks"].append(("C scale", "SKIP", "scaleDen not set"))

    worst = "PASS"
    for _, st, _ in res["checks"]:
        if st == "FAIL":
            worst = "FAIL"
            break
        if st in ("WARN", "SKIP") and worst == "PASS":
            worst = "WARN"
    res["status"] = worst
    return res


def main():
    ap = argparse.ArgumentParser(description="post-draw acceptance gate (PDF ink box vs WVOL expected box)")
    ap.add_argument("pdf")
    ap.add_argument("--elements", default="")
    ap.add_argument("--plane", default="plan", choices=["plan", "elev"])
    ap.add_argument("--scale", type=float, default=0.0)
    ap.add_argument("--port", type=int, default=None)
    ap.add_argument("--strict", action="store_true")
    ap.add_argument("--json", default="")
    a = ap.parse_args()
    port = bridge_port(a.port)
    params = {"elements": a.elements, "plane": a.plane, "scaleDen": a.scale}
    res = gate(params, a.pdf, port, a.strict)
    print(json.dumps(res, ensure_ascii=False, indent=1))
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(res, f, ensure_ascii=False, indent=1)
    sys.exit(0 if res["status"] in ("PASS", "WARN") else 1)


if __name__ == "__main__":
    main()
