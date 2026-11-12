#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""smoke_test.py - Plan A skeleton round-trip test (no PDMS needed).

Starts relay.py + bridge/agent.py locally, acts as a browser client, and
submits a get_ce task. PDMS is not running, so the agent must come back
with ok:false and a bridge-unreachable error - which still proves the
full web -> relay -> agent -> (bridge attempt) -> agent -> relay -> web
loop: auth, registration, task routing, result routing.

Run:  python smoke_test.py
PASS  = round trip works; FAIL lines say which leg broke.
"""
import asyncio
import json
import os
import subprocess
import sys
import time

import websockets

HERE = os.path.dirname(os.path.abspath(__file__))
PY = sys.executable
TOKEN = "smoke-token"
WS_PORT = 18875
HTTP_PORT = 18876

RELAY = os.path.join(HERE, "relay.py")
AGENT = os.path.join(os.path.dirname(HERE), "bridge", "agent.py")


async def main():
    relay = subprocess.Popen([PY, RELAY, "--ws-port", str(WS_PORT),
                              "--http-port", str(HTTP_PORT), "--token", TOKEN])
    time.sleep(1.5)
    agent = subprocess.Popen([PY, AGENT,
                              "--url", "ws://127.0.0.1:%d" % WS_PORT,
                              "--http", "http://127.0.0.1:%d" % HTTP_PORT,
                              "--token", TOKEN, "--machine", "smoke-machine"])
    ok = False
    try:
        await asyncio.sleep(3)
        async with websockets.connect("ws://127.0.0.1:%d" % WS_PORT,
                                      max_size=64 * 2 ** 20) as ws:
            await ws.send(json.dumps({"type": "hello", "role": "web", "token": TOKEN}))
            hello = json.loads(await asyncio.wait_for(ws.recv(), 10))
            assert hello.get("type") == "hello_ok", hello
            print("[1] web auth OK, machines seen:", hello.get("machines"))
            assert "smoke-machine" in (hello.get("machines") or []), \
                "agent not registered - agent leg broken"
            print("[2] agent registered OK")
            await ws.send(json.dumps({"type": "submit_task", "task_id": "t1",
                                      "op": "get_ce", "params": {},
                                      "machine_id": "smoke-machine"}))
            result = None
            deadline = time.time() + 30
            while time.time() < deadline:
                msg = json.loads(await asyncio.wait_for(ws.recv(), 30))
                if msg.get("type") == "task_result" and msg.get("task_id") == "t1":
                    result = msg
                    break
                if msg.get("type") == "task_accepted":
                    print("[3] task routed via relay OK (machine=%s)" % msg.get("machine"))
            assert result is not None, "no task_result - result leg broken"
            print("[4] result:", json.dumps(result, ensure_ascii=False)[:180])
            if result.get("ok"):
                if str(result.get("output", "")).strip():
                    print("[note] get_ce returned real data - bridge DLL is current")
                else:
                    print("[note] ok:true but EMPTY output -> running drawingPDMS.dll"
                          " predates get_ce (rebuild pdmsDrawing/src, deploy, restart PDMS)")
                print("[note] bridge answered ok:true - PdmsBridge is ALIVE on this machine")
            else:
                print("[note] bridge unreachable (PDMS off) - expected on CI machines")
            # leg 2: env_report round trip (read-only, exercises agent-side op)
            await ws.send(json.dumps({"type": "submit_task", "task_id": "t2",
                                      "op": "env_report", "params": {},
                                      "machine_id": "smoke-machine"}))
            r2 = None; deadline = time.time() + 60
            while time.time() < deadline:
                msg = json.loads(await asyncio.wait_for(ws.recv(), timeout=60))
                if msg.get("type") == "task_result" and msg.get("task_id") == "t2":
                    r2 = msg; break
            assert r2 is not None and r2.get("ok"), "env_report failed: %r" % r2
            rep = json.loads(r2.get("output") or "{}")
            print("[5] env_report ok: bridge=%s procs=%d dlls=%d" %
                  (rep.get("bridge"), len(rep.get("procs") or []), len(rep.get("dlls") or [])))
            print("PASS: web -> relay -> agent -> bridge -> relay -> web round trip OK")
            ok = True
    finally:
        for p in (agent, relay):
            try:
                p.terminate()
            except Exception:
                pass
    if not ok:
        sys.exit(1)


asyncio.run(main())
