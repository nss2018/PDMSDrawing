#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Minimal PDMS bridge TCP client with multi-module discovery.

Each PDMS module (Design/Paragon/Draft) runs its own bridge process and writes a
discovery manifest to <BRIDGE_DIR>/instances/<module>_<pid>.json with its port.
This client picks the right port by module so that when Design+Paragon+Draw are
all open, you can target the correct one.

Usage:
  bridge_client.py --kind exec --codefile <pml> --id <n> [--module DESIGN]
  bridge_client.py --kind op --op <name> --id <n> [--params '{"k":"v"}'] [--module PARAGON]
  bridge_client.py --list            # list live bridge modules/ports

Module selection priority:
  1) --module / PDMS_BRIDGE_MODULE env (DESIGN/PARAGON/DRAFT/...; DRAW == DRAFT)
  2) PDMS_BRIDGE_PORT env (legacy explicit port)
  3) exactly one live instance -> its port
  4) multiple -> DESIGN if present else first
  5) fallback 127.0.0.1:18753
"""
import sys, json, socket, argparse, subprocess, os

HOST = "127.0.0.1"
WATCHER = os.path.join(os.path.dirname(os.path.abspath(__file__)), "dialog_watcher.py")


def resolve_bridge_dir():
    if os.environ.get("PDMS_BRIDGE_DIR"):
        return os.environ["PDMS_BRIDGE_DIR"].replace("\\", "/")
    for c in ["C:/pdmsAgent/bridge", "D:/pdmsAgent/bridge"]:
        if os.path.exists(c):
            return c
    return "C:/pdmsAgent/bridge"


def discover_instances():
    """Read manifests under <bridge_dir>/instances and keep only live ports."""
    out = []
    d = os.path.join(resolve_bridge_dir(), "instances")
    if not os.path.isdir(d):
        return out
    for f in sorted(os.listdir(d)):
        if not f.endswith(".json"):
            continue
        try:
            with open(os.path.join(d, f), "r", encoding="utf-8") as fh:
                j = json.load(fh)
            if not (j.get("module") and j.get("port")):
                continue
            if _probe(j["port"]):
                out.append(j)
        except Exception:
            pass
    return out


def _probe(port, timeout=0.4):
    try:
        s = socket.create_connection((HOST, int(port)), timeout=timeout)
        s.close()
        return True
    except Exception:
        return False


def resolve_port(module=None):
    if os.environ.get("PDMS_BRIDGE_PORT"):
        return int(os.environ["PDMS_BRIDGE_PORT"])
    inst = discover_instances()
    want = (module or os.environ.get("PDMS_BRIDGE_MODULE") or "").strip().upper()
    if want:
        if want == "DRAW":
            want = "DRAFT"
        for j in inst:
            if str(j.get("module")).upper() == want:
                return int(j["port"])
    if len(inst) == 1:
        return int(inst[0]["port"])
    for j in inst:
        if j.get("module") == "DESIGN":
            return int(j["port"])
    if inst:
        return int(inst[0]["port"])
    return int(os.environ.get("PDMS_BRIDGE_PORT", "18753"))


def ensure_dialog_watcher():
    """Auto-close watchdog for PDMS modal error boxes (prevents pipeline hang)."""
    try:
        subprocess.Popen([sys.executable, WATCHER, "--ensure"],
                         creationflags=subprocess.CREATE_NO_WINDOW, close_fds=True)
    except Exception:
        pass


def send(req, port):
    s = socket.create_connection((HOST, port), timeout=300)
    try:
        payload = (json.dumps(req) + "\n").encode("utf-8")
        s.sendall(payload)
        s.settimeout(300)
        buf = b""
        while b"\n" not in buf:
            chunk = s.recv(65536)
            if not chunk:
                break
            buf += chunk
        return buf.split(b"\n", 1)[0].decode("utf-8", "replace")
    finally:
        s.close()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--kind", required=False, choices=["exec", "op"], help="exec or op (omit with --list)")
    ap.add_argument("--codefile", help="PML file for exec")
    ap.add_argument("--code", help="inline PML for exec")
    ap.add_argument("--op", help="op name for op")
    ap.add_argument("--params", default="{}", help="JSON params for op")
    ap.add_argument("--id", type=int, default=1)
    ap.add_argument("--module", help="target module: DESIGN/PARAGON/DRAFT/... (DRAW==DRAFT)")
    ap.add_argument("--list", action="store_true", help="list live bridge modules/ports and exit")
    args = ap.parse_args()

    if args.list:
        inst = discover_instances()
        port = resolve_port(args.module)
        print(json.dumps({"modules": inst, "resolvedPort": port}, ensure_ascii=False, indent=2))
        return

    ensure_dialog_watcher()

    port = resolve_port(args.module)
    if args.kind == "exec":
        if args.codefile:
            with open(args.codefile, "r", encoding="utf-8") as f:
                code = f.read()
        else:
            code = args.code or ""
        req = {"id": args.id, "kind": "exec", "code": code}
    else:
        try:
            prm = json.loads(args.params) if args.params else {}
        except Exception:
            prm = {}
        req = {"id": args.id, "kind": "op", "op": args.op, "params": prm}

    out = send(req, port)
    print(out)


if __name__ == "__main__":
    main()
