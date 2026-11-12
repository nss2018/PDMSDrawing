#!/usr/bin/env python3
# -*- coding: utf-8 -*-
import socket, json, sys, subprocess, os
PORT=18753; HOST="127.0.0.1"
WATCHER=os.path.join(os.path.dirname(os.path.abspath(__file__)),"dialog_watcher.py")
try:
    subprocess.Popen([sys.executable, WATCHER, "--ensure"], creationflags=subprocess.CREATE_NO_WINDOW, close_fds=True)
except Exception:
    pass
req={"id":1,"kind":"op","op":"replace_symbols","params":{"view":"=16385/1249","sheet":"=16385/1242"}}
s=socket.create_connection((HOST,PORT), timeout=300)
s.sendall((json.dumps(req)+"\n").encode("utf-8"))
s.settimeout(2400)
buf=b""
while b"\n" not in buf:
    c=s.recv(65536)
    if not c: break
    buf+=c
print(buf.split(b"\n",1)[0].decode("utf-8","replace"))
s.close()
