#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""agent.py - pdmsDrawing local resident agent (Plan A skeleton).

Runs on the PDMS machine, next to wizard.py. Dials OUT to the cloud relay
(no inbound port needed), executes tasks against the local PdmsBridge
(127.0.0.1:18753), streams progress and uploads PDF/preview back.

Usage:
  python agent.py --url ws://<cloud>:8765 --http http://<cloud>:8766 \
                  --token <secret> [--machine <id>]

Reuse (single source of truth, no drift):
  - pipeline_draw  -> wizard.pipeline() straight from wizard.py
  - other ops      -> wizard.bridge_req() passthrough to PdmsBridge
  - deploy         -> deploy.py find_pdms()/install() from ../deploy/

Pipeline progress: while wizard.pipeline runs in a worker thread, the
agent tails the replace_symbols log (same RSYM-START rule as the local
wizard page) and pushes it every 3s - the cloud page reuses the exact
same 10-stage marker matching.
"""
import argparse
import asyncio
import base64
import json
import os
import re
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request

import websockets

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)                                   # for wizard
DEPLOY_DIR = os.path.join(os.path.dirname(HERE), "deploy")
sys.path.insert(0, DEPLOY_DIR)                             # for deploy module

import wizard            # noqa: E402  (reuse bridge_req / pipeline / PIPE / LOG)
import deploy as deploy_mod   # noqa: E402  (reuse find_pdms / install)

TOKEN = "changeme"
HTTP_BASE = "http://127.0.0.1:8766"

LOOP = None          # main asyncio loop (for run_coroutine_threadsafe)
CURRENT = {"ws": None}


def log_line(msg):
    print("[agent]", time.strftime("%H:%M:%S"), msg, flush=True)


def send(obj):
    """Push a message to the relay from any thread."""
    ws = CURRENT.get("ws")
    loop = LOOP
    if ws is None or loop is None:
        return

    async def _s():
        try:
            await ws.send(json.dumps(obj, ensure_ascii=False))
        except Exception:
            pass
    asyncio.run_coroutine_threadsafe(_s(), loop)


def log_tail():
    """Same rule as local wizard /api/log: from last RSYM-START, last 40 lines."""
    try:
        with open(wizard.LOG, "r", encoding="utf-8", errors="replace") as f:
            lines = f.readlines()
        idx = max((i for i, l in enumerate(lines) if "RSYM-START" in l), default=None)
        if idx is None:
            return ""
        return "".join(lines[idx:][-40:])
    except Exception:
        return ""


# ---------------- vision (pdms-vision skill: local PDMS desktop windows) ----------------
# Drives scripts/pdmswin.ps1 on the PDMS machine (lock + screenshot + input),
# so the cloud page can SEE and CLICK the real PDMS UI without a bridge op.

def find_vision_ps1():
    cands = [
        os.environ.get("PDMS_VISION_PS1", ""),
        os.path.expanduser("~/.workbuddy/skills/pdms-vision/scripts/pdmswin.ps1"),
        r"C:\Users\Nie\.workbuddy\skills\pdms-vision\scripts\pdmswin.ps1",
    ]
    for c in cands:
        if c and os.path.exists(c):
            return c
    base = os.path.expanduser("~/.workbuddy/skills")
    if os.path.isdir(base):
        for root, _, files in os.walk(base):
            if "pdmswin.ps1" in files:
                return os.path.join(root, "pdmswin.ps1")
    return None


def _vision_env():
    """Minimal env for the child powershell - avoids the 65535-byte env-block
    limit some hosts hit when spawning powershell.exe from a huge environment."""
    keys = ["SystemRoot", "windir", "SystemDrive", "PATH", "PATHEXT", "TEMP",
            "TMP", "USERPROFILE", "ComSpec", "NUMBER_OF_PROCESSORS",
            "PROCESSOR_ARCHITECTURE", "OS", "HOMEDRIVE", "HOMEPATH"]
    return {k: os.environ[k] for k in keys if k in os.environ}


def _run_ps(args, timeout=90):
    return subprocess.run(args, capture_output=True, text=True, timeout=timeout,
                          env=_vision_env(),
                          creationflags=subprocess.CREATE_NO_WINDOW)


def vision_worker(tid, sub, params):
    send({"type": "progress", "task_id": tid, "log": "[agent] vision %s\n" % sub})
    ps1 = find_vision_ps1()
    if not ps1:
        send({"type": "task_result", "task_id": tid, "ok": False,
              "error": "pdmswin.ps1 not found (set PDMS_VISION_PS1)"})
        return
    p = params or {}
    base = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", ps1]
    try:
        if sub == "shot":
            match = str(p.get("match", ""))
            out = os.path.join(tempfile.gettempdir(),
                               "pdmsvision_%d.png" % int(time.time()))
            args = base + ["shot", match, out]
            for k in ("x", "y", "w", "h"):
                v = str(p.get(k, "")).strip()
                if v:
                    args.append(v)
            r = _run_ps(args)
            ok = r.returncode == 0
            img = None
            if ok and os.path.exists(out):
                with open(out, "rb") as f:
                    img = base64.b64encode(f.read()).decode()
                try:
                    os.remove(out)
                except OSError:
                    pass
            m = re.search(r'winLeftTop=(\d+),(\d+)', r.stdout)
            win = {"left": int(m.group(1)), "top": int(m.group(2))} if m else None
            send({"type": "task_result", "task_id": tid, "ok": ok,
                  "output": r.stdout.strip(),
                  "error": None if ok else (r.stderr.strip() or r.stdout.strip()),
                  "image": ("data:image/png;base64," + img) if img else None,
                  "win": win})
            return
        if sub in ("list", "lock", "rect", "dismiss"):
            args = base + [sub]
            if sub in ("lock", "rect", "dismiss"):
                args.append(str(p.get("match", "")))
            r = _run_ps(args)
            send({"type": "task_result", "task_id": tid, "ok": r.returncode == 0,
                  "output": r.stdout.strip(),
                  "error": None if r.returncode == 0 else (r.stderr.strip() or r.stdout.strip())})
            return
        if sub in ("click", "type", "key"):
            args = base + [sub]
            if sub == "click":
                args += [str(p.get("sx", "0")), str(p.get("sy", "0"))]
            elif sub == "type":
                args += [str(p.get("text", ""))]
            elif sub == "key":
                args += [str(p.get("key", "Enter"))]
            args += ["--expect", str(p.get("expect", ""))]
            r = _run_ps(args, timeout=30)
            send({"type": "task_result", "task_id": tid, "ok": r.returncode == 0,
                  "output": r.stdout.strip(),
                  "error": None if r.returncode == 0 else (r.stderr.strip() or r.stdout.strip())})
            return
        send({"type": "task_result", "task_id": tid, "ok": False,
              "error": "unknown vision sub: %s" % sub})
    except Exception as e:
        send({"type": "task_result", "task_id": tid, "ok": False,
              "error": "vision error: %r" % e})


# ---------------- task workers (blocking, run in threads) ----------------

def op_worker(tid, op, params):
    send({"type": "progress", "task_id": tid,
          "log": "[agent] bridge op: %s\n" % op})
    try:
        if op == "create_equipment":
            # agent-side: PML generated by wizard (single source), exec on bridge
            out = wizard.run_equipment(params or {})
            send({"type": "task_result", "task_id": tid, "ok": bool(out.get("ok")),
                  "output": out.get("msg", ""), "error": out.get("error"),
                  "pml": out.get("pml", "")})
            return
        if op == "env_report":
            rep = wizard.env_report()
            send({"type": "task_result", "task_id": tid, "ok": True,
                  "output": json.dumps(rep, ensure_ascii=False), "error": None})
            return
        if op == "exec_pml":
            # unattended console, line by line; query lines (Q CE/Q NAME/...)
            # are routed to the get_ce op for REAL values - evidence: $M macro
            # CP errors never fail RunInPdms, so exec ok:true proves nothing
            # for query lines, and query echo is only in the PDMS command window
            code = str((params or {}).get("code", ""))
            stop = str((params or {}).get("stop_on_error", "1")) != "0"
            res = wizard.exec_pml_lines(code, stop,
                on_line=lambda seg: send({"type": "progress", "task_id": tid, "log": seg + "\n"}))
            send({"type": "task_result", "task_id": tid, "ok": res["ok"],
                  "output": wizard.format_pml_results(res),
                  "error": None if res["ok"] else "some lines failed, see output"})
            return
        r = wizard.bridge_req({"id": int(time.time()), "kind": "op", "op": op,
                               "params": {k: str(v) for k, v in (params or {}).items()}},
                              timeout=3600)
        out = r.get("output", "")
        ok = bool(r.get("ok"))
        # 2026-10-26: export_dxf -> upload the produced DXF to the relay so the
        # web page gets a download link (same channel as pipeline PDF).
        # ExportDxf output format: "DXF OK file=NNNB <path>\nSHEET <name>"
        if op == "export_dxf" and ok:
            try:
                m = re.search(r"file=\d+B (.+\.dxf)", out)
                if m and os.path.exists(m.group(1)):
                    with open(m.group(1), "rb") as f:
                        send({"type": "pdf", "task_id": tid,
                              "filename": os.path.basename(m.group(1)),
                              "b64": base64.b64encode(f.read()).decode()})
                    send({"type": "progress", "task_id": tid,
                          "log": "[agent] dxf uploaded: " + os.path.basename(m.group(1)) + "\n"})
            except Exception as eup:
                log_line("dxf upload failed: %r" % eup)
        send({"type": "task_result", "task_id": tid, "ok": ok,
              "output": out, "error": r.get("error")})
    except Exception as e:
        send({"type": "task_result", "task_id": tid, "ok": False,
              "error": "bridge unreachable (PDMS not running?): %r" % e})


def pipeline_worker(tid, params):
    send({"type": "progress", "task_id": tid, "log": "[agent] pipeline started\n"})
    wizard.PIPE.update({"running": True, "done": False, "ok": False,
                        "preview": None, "pdf": None, "log": []})
    t = threading.Thread(target=wizard.pipeline, args=(dict(params),), daemon=True)
    t.start()
    while t.is_alive():
        time.sleep(3)
        steps = "".join(wizard.PIPE.get("log", []))
        send({"type": "progress", "task_id": tid, "log": log_tail() + "\n" + steps})
    steps = "".join(wizard.PIPE.get("log", []))
    send({"type": "progress", "task_id": tid, "log": log_tail() + "\n" + steps})
    ok = bool(wizard.PIPE.get("ok"))
    pv = wizard.PIPE.get("preview")
    if pv and pv.startswith("data:image/png;base64,"):
        send({"type": "preview", "task_id": tid, "b64": pv.split(",", 1)[1]})
    pdf = wizard.PIPE.get("pdf")
    if ok and pdf and os.path.exists(pdf):
        try:
            with open(pdf, "rb") as f:
                send({"type": "pdf", "task_id": tid, "filename": os.path.basename(pdf),
                      "b64": base64.b64encode(f.read()).decode()})
        except Exception as e:
            log_line("pdf upload failed: %r" % e)
    send({"type": "task_result", "task_id": tid, "ok": ok,
          "error": None if ok else steps[-500:] or "pipeline failed",
          "pdf_local": pdf})


def tool_deploy_worker(tid):
    """Deploy the free PML tool into <PDMS>\\PMLLIB\\tool, then refresh the
    PML library exactly once via PML REHASH ALL (silent: details stay in the
    agent console / progress stream, never in the web UI)."""
    import zipfile
    try:
        send({"type": "progress", "task_id": tid,
              "log": "[agent] downloading PMLLIB tool payload...\n"})
        zpath = os.path.join(tempfile.gettempdir(), "pmllib_tool.zip")
        urllib.request.urlretrieve(HTTP_BASE + "/payload/pmllib_tool.zip?t=" + TOKEN, zpath)
        pdms = deploy_mod.find_pdms()
        if not pdms:
            raise Exception("PDMS 12.1 dir not found (need PMLLIB under install dir)")
        dst = os.path.join(pdms, "PMLLIB")
        os.makedirs(dst, exist_ok=True)
        with zipfile.ZipFile(zpath) as z:
            n = len([x for x in z.namelist() if not x.endswith("/")])
            z.extractall(dst)
        send({"type": "progress", "task_id": tid,
              "log": "[agent] tool extracted to %s (%d files)\n"
                     % (os.path.join(dst, "tool"), n)})
        # PMLLIB is rescanned on PDMS startup; REHASH only matters for a live
        # session - fire it once and move on, failure is non-fatal
        rehash = "skipped (bridge offline, applies at next PDMS start)"
        try:
            r = wizard.bridge_req({"id": int(time.time()), "kind": "exec",
                                   "code": "PML REHASH ALL"}, timeout=60)
            rehash = "done" if r.get("ok") else "failed"
        except Exception:
            pass
        send({"type": "progress", "task_id": tid,
              "log": "[agent] PML rehash: " + rehash + "\n"})
        send({"type": "task_result", "task_id": tid, "ok": True,
              "output": "部署完成：PMLLIB\\tool（%d 个文件）" % n, "error": None})
    except Exception as e:
        send({"type": "task_result", "task_id": tid, "ok": False, "error": str(e)})


def deploy_worker(tid):
    try:
        send({"type": "progress", "task_id": tid, "log": "[agent] downloading payload...\n"})
        dll = os.path.join(tempfile.gettempdir(), "drawingPDMS.dll")
        urllib.request.urlretrieve(HTTP_BASE + "/payload/drawingPDMS.dll?t=" + TOKEN, dll)
        pdms = deploy_mod.find_pdms()
        if not pdms:
            raise Exception("PDMS 12.1 dir not found (need DesignAddins.xml)")
        send({"type": "progress", "task_id": tid, "log": "[agent] PDMS dir: " + pdms + "\n"})
        ok = deploy_mod.install(pdms, dll)
        # 2026-10-26: ship DXF post-process scripts next to the DLL - the DLL
        # resolves them via BridgeFile(<AssemblyDir>) and silently skips the
        # DEFPOINTS no-plot / polyline width fixes when missing
        for py in ("dxf_nodots.py", "dxf_polywidth.py", "dxf_colorize.py", "dxf_block.py"):
            try:
                dst_py = os.path.join(pdms, py)
                urllib.request.urlretrieve(HTTP_BASE + "/payload/" + py + "?t=" + TOKEN, dst_py)
                send({"type": "progress", "task_id": tid, "log": "[agent] script deployed: " + py + "\n"})
            except Exception as epy:
                send({"type": "progress", "task_id": tid,
                      "log": "[agent] script " + py + " skipped: %r\n" % epy})
        send({"type": "task_result", "task_id": tid, "ok": bool(ok),
              "error": None if ok else "install failed, see agent console"})
    except Exception as e:
        send({"type": "task_result", "task_id": tid, "ok": False, "error": str(e)})


# ---------------- ws session ----------------

async def handle_msg(ws, msg):
    t = msg.get("type")
    if t in ("task", "deploy"):
        tid = msg.get("task_id")
        CURRENT["ws"] = ws
        if t == "deploy" or msg.get("op") == "deploy":
            log_line("deploy task " + tid)
            threading.Thread(target=deploy_worker, args=(tid,), daemon=True).start()
        elif msg.get("op") == "pipeline_draw":
            log_line("pipeline task " + tid)
            threading.Thread(target=pipeline_worker,
                             args=(tid, msg.get("params") or {}), daemon=True).start()
        elif msg.get("op") == "deploy_tool":
            log_line("pmllib tool deploy task " + tid)
            threading.Thread(target=tool_deploy_worker, args=(tid,), daemon=True).start()
        elif str(msg.get("op", "")).startswith("vision_"):
            sub = str(msg.get("op"))[7:]
            log_line("vision task " + tid + ": " + sub)
            threading.Thread(target=vision_worker,
                             args=(tid, sub, msg.get("params") or {}),
                             daemon=True).start()
        else:
            log_line("op task " + tid + ": " + str(msg.get("op")))
            threading.Thread(target=op_worker,
                             args=(tid, msg.get("op"), msg.get("params") or {}),
                             daemon=True).start()
    elif t == "ping":
        send({"type": "pong", "ts": time.time()})


async def amain(url, machine):
    global LOOP
    LOOP = asyncio.get_running_loop()
    backoff = 2
    while True:
        try:
            async with websockets.connect(url, max_size=64 * 2 ** 20,
                                          ping_interval=20, ping_timeout=20) as ws:
                await ws.send(json.dumps({"type": "hello", "role": "agent",
                                          "token": TOKEN, "machine_id": machine}))
                ack = json.loads(await ws.recv())
                if ack.get("type") != "hello_ok":
                    log_line("auth rejected: " + json.dumps(ack))
                    return
                CURRENT["ws"] = ws
                log_line("connected to %s as %s" % (url, ack.get("machine_id", machine)))
                backoff = 2
                async for raw in ws:
                    try:
                        await handle_msg(ws, json.loads(raw))
                    except Exception as e:
                        log_line("msg error: %r" % e)
        except Exception as e:
            log_line("disconnected (%r), retry in %ds" % (e, backoff))
            await asyncio.sleep(backoff)
            backoff = min(backoff * 2, 60)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="ws://127.0.0.1:8765", help="cloud relay ws url")
    ap.add_argument("--http", default="http://127.0.0.1:8766",
                    help="cloud relay http base (payload download)")
    ap.add_argument("--token", default=TOKEN)
    ap.add_argument("--machine", default=os.environ.get("COMPUTERNAME", "pdms-machine"))
    args = ap.parse_args()
    TOKEN = args.token
    HTTP_BASE = args.http
    log_line("agent start: url=%s http=%s machine=%s" % (args.url, HTTP_BASE, args.machine))
    try:
        asyncio.run(amain(args.url, args.machine))
    except KeyboardInterrupt:
        pass
