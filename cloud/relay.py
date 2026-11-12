#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""relay.py - pdmsDrawing cloud relay (Plan A skeleton).

Cloud side of the drawing pipeline:
  - agents (local PDMS machines) dial OUT here via WebSocket - no inbound
    port ever opens on the PDMS machine
  - browsers (wizard page) connect here, submit tasks, watch progress
  - relay routes task/progress/result messages between them, stores
    uploaded PDFs under uploads/ and serves the deploy payload

Run on the cloud server:
  python relay.py --ws-port 8765 --http-port 8766 --token <shared-secret>

Protocol (JSON lines over WebSocket, one message per frame):
  client -> relay : {"type":"hello","role":"agent"|"web","token":...,
                     "machine_id":...}          (must be first message)
  web    -> relay : {"type":"submit_task","task_id","machine_id","op","params"}
                    {"type":"deploy","machine_id"}
  relay  -> agent : {"type":"task","task_id","op","params"}
                    {"type":"deploy","task_id"}
  agent  -> relay : {"type":"progress","task_id","log"}
                    {"type":"preview","task_id","b64"}
                    {"type":"pdf","task_id","filename","b64"}
                    {"type":"task_result","task_id","ok","error","output"}
  relay  -> web   : {"type":"agents","machines":[...]} / task_accepted /
                    progress / preview / pdf_ready{url} / task_result

HTTP side (http-port): GET /            -> web/index.html
                       GET /uploads/*   -> stored PDFs (token query param)
                       GET /payload/*   -> deploy payload files (token)
                       GET /health      -> status json
"""
import argparse
import asyncio
import base64
import json
import os
import secrets
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import websockets

HERE = os.path.dirname(os.path.abspath(__file__))
WEB_DIR = os.path.join(HERE, "web")
UPLOAD_DIR = os.path.join(HERE, "uploads")
PAYLOAD_DIR = os.environ.get("PDMS_PAYLOAD_DIR") or \
    os.path.join(os.path.dirname(HERE), "deploy", "payload")
TOKEN = os.environ.get("PDMS_CLOUD_TOKEN", "changeme")
# optional URL prefix for reverse-proxy sub-path deployment, e.g. /pdms
# (nginx: location /pdms/ { proxy_pass http://127.0.0.1:8766; }  - no strip,
#  relay accepts both prefixed and raw paths and emits prefixed links)
URL_PREFIX = os.environ.get("PDMS_URL_PREFIX", "").rstrip("/")

AGENTS = {}   # machine_id -> websocket
WEBS = set()  # browser websockets
TASKS = {}    # task_id -> {"web": ws, "machine": str, "op": str, "ts": float}


async def to_webs(msg):
    if not WEBS:
        return
    data = json.dumps(msg, ensure_ascii=False)
    await asyncio.gather(*(w.send(data) for w in list(WEBS)), return_exceptions=True)


async def to_web(ws, msg):
    try:
        await ws.send(json.dumps(msg, ensure_ascii=False))
    except Exception:
        pass


async def handle_agent_msg(machine, msg):
    t = msg.get("type")
    tid = msg.get("task_id")
    if t in ("progress", "preview"):
        task = TASKS.get(tid)
        if task and task.get("web"):
            await to_web(task["web"], msg)
    elif t == "pdf":
        tid = tid or time.strftime("%Y%m%d%H%M%S") + "-" + secrets.token_hex(3)
        fname = os.path.basename(msg.get("filename") or (tid + ".pdf"))
        # 2026-10-26: keep real extension for DXF/DWG uploads (export_dxf flow);
        # only fall back to .pdf when the name has no known drawing extension
        if not fname.lower().endswith((".pdf", ".dxf", ".dwg")):
            fname += ".pdf"
        os.makedirs(UPLOAD_DIR, exist_ok=True)
        stored = tid + "_" + fname
        with open(os.path.join(UPLOAD_DIR, stored), "wb") as f:
            f.write(base64.b64decode(msg.get("b64", "")))
        print("[relay] pdf stored:", stored)
        task = TASKS.get(tid)
        if task and task.get("web"):
            await to_web(task["web"],
                         {"type": "pdf_ready", "task_id": tid,
                          "url": URL_PREFIX + "/uploads/" + stored})
    elif t == "task_result":
        task = TASKS.pop(tid, None) or {}
        if task.get("web"):
            await to_web(task["web"], msg)
        print("[relay] task done:", tid, "ok=", msg.get("ok"))
    else:
        print("[relay] ignored agent msg type:", t)


async def handle_web_msg(ws, msg):
    t = msg.get("type")
    if t == "submit_task":
        tid = msg.get("task_id") or (time.strftime("%Y%m%d%H%M%S") + "-" + secrets.token_hex(3))
        mid = msg.get("machine_id") or (next(iter(AGENTS)) if AGENTS else None)
        if not mid or mid not in AGENTS:
            await to_web(ws, {"type": "task_result", "task_id": tid, "ok": False,
                              "error": "agent offline: %s" % mid})
            return
        TASKS[tid] = {"web": ws, "machine": mid, "op": msg.get("op"), "ts": time.time()}
        await AGENTS[mid].send(json.dumps(
            {"type": "task", "task_id": tid, "op": msg.get("op"),
             "params": msg.get("params") or {}}, ensure_ascii=False))
        await to_web(ws, {"type": "task_accepted", "task_id": tid, "machine": mid})
    elif t == "deploy":
        tid = "deploy-" + secrets.token_hex(3)
        mid = msg.get("machine_id") or (next(iter(AGENTS)) if AGENTS else None)
        if not mid or mid not in AGENTS:
            await to_web(ws, {"type": "task_result", "task_id": tid, "ok": False,
                              "error": "agent offline: %s" % mid})
            return
        TASKS[tid] = {"web": ws, "machine": mid, "op": "deploy", "ts": time.time()}
        await AGENTS[mid].send(json.dumps({"type": "deploy", "task_id": tid}))
        await to_web(ws, {"type": "task_accepted", "task_id": tid, "machine": mid})
    else:
        await to_web(ws, {"type": "error", "error": "unknown type: %s" % t})


async def sock(ws):
    role = None
    ident = None
    try:
        hello = json.loads(await ws.recv())
        if hello.get("type") != "hello" or hello.get("token") != TOKEN:
            await to_web(ws, {"type": "auth_error", "error": "bad token"})
            return
        role = hello.get("role")
        if role == "agent":
            ident = hello.get("machine_id") or ("agent-" + secrets.token_hex(3))
            old = AGENTS.get(ident)
            if old is not None and old is not ws:
                try:
                    await old.close(code=4001, reason="replaced by new connection")
                except Exception:
                    pass
            AGENTS[ident] = ws
            print("[relay] agent online:", ident)
            await to_web(ws, {"type": "hello_ok", "machine_id": ident})
            await to_webs({"type": "agents", "machines": sorted(AGENTS)})
            async for raw in ws:
                try:
                    await handle_agent_msg(ident, json.loads(raw))
                except Exception as e:
                    print("[relay] agent msg error:", repr(e))
        elif role == "web":
            WEBS.add(ws)
            await to_web(ws, {"type": "hello_ok", "machines": sorted(AGENTS)})
            async for raw in ws:
                try:
                    await handle_web_msg(ws, json.loads(raw))
                except Exception as e:
                    print("[relay] web msg error:", repr(e))
        else:
            await to_web(ws, {"type": "auth_error", "error": "bad role"})
    except websockets.exceptions.ConnectionClosed:
        pass
    finally:
        if role == "agent" and AGENTS.get(ident) is ws:
            del AGENTS[ident]
            print("[relay] agent offline:", ident)
            await to_webs({"type": "agents", "machines": sorted(AGENTS)})
        WEBS.discard(ws)


class HttpH(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _tok_ok(self):
        from urllib.parse import urlparse, parse_qs
        q = parse_qs(urlparse(self.path).query)
        return q.get("t", [""])[0] == TOKEN

    def _file(self, path, ctype):
        try:
            data = open(path, "rb").read()
        except Exception:
            self.send_response(404)
            self.end_headers()
            return
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        path, _, _q = self.path.partition("?")
        if URL_PREFIX:
            if path == URL_PREFIX or path == URL_PREFIX + "/":
                path = "/"
            elif path.startswith(URL_PREFIX + "/"):
                path = path[len(URL_PREFIX):]
        if path in ("/", "/index.html", "/draw"):
            self._file(os.path.join(WEB_DIR, "index.html"), "text/html; charset=utf-8")
        elif path == "/health":
            body = json.dumps({"ok": True, "agents": sorted(AGENTS), "tasks": len(TASKS)}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        elif path.startswith("/uploads/"):
            if not self._tok_ok():
                self.send_response(403)
                self.end_headers()
                return
            # content-type by extension: inline PDF preview needs application/pdf,
            # DXF/DWG are downloads (octet-stream)
            up = os.path.basename(path)
            ctype = "application/pdf"
            if up.lower().endswith((".dxf", ".dwg")):
                ctype = "application/octet-stream"
            self._file(os.path.join(UPLOAD_DIR, up), ctype)
        elif path.startswith("/payload/"):
            if not self._tok_ok():
                self.send_response(403)
                self.end_headers()
                return
            self._file(os.path.join(PAYLOAD_DIR, os.path.basename(path)), "application/octet-stream")
        else:
            self.send_response(404)
            self.end_headers()


def start_http(port):
    os.makedirs(UPLOAD_DIR, exist_ok=True)
    srv = ThreadingHTTPServer(("0.0.0.0", port), HttpH)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    print("[relay] http on 0.0.0.0:%d (page /uploads /payload)" % port)


async def main():
    global TOKEN, URL_PREFIX
    ap = argparse.ArgumentParser()
    ap.add_argument("--ws-port", type=int, default=8765)
    ap.add_argument("--http-port", type=int, default=8766)
    ap.add_argument("--token", default=TOKEN)
    ap.add_argument("--url-prefix", default=os.environ.get("PDMS_URL_PREFIX", "").rstrip("/"),
                    help="reverse-proxy sub-path prefix, e.g. /pdms")
    args = ap.parse_args()
    TOKEN = args.token
    URL_PREFIX = args.url_prefix.rstrip("/")
    start_http(args.http_port)
    async with websockets.serve(sock, "0.0.0.0", args.ws_port,
                                max_size=64 * 2 ** 20,
                                ping_interval=20, ping_timeout=20):
        print("[relay] ws on 0.0.0.0:%d, payload dir: %s" % (args.ws_port, PAYLOAD_DIR))
        await asyncio.Future()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
