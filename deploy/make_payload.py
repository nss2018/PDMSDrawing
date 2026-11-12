#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""make_payload.py - build deploy payloads for the cloud relay.

Outputs (into deploy/payload/):
  pmllib_tool.zip  free PML tool folder (from C:/AVEVA/.../PMLLIB/tool),
                   zip layout "tool/..." so agent extracts straight into PMLLIB
  agent.zip        agent distribution: bridge/agent.py + bridge/wizard.py +
                   deploy/deploy.py + Start-Agent.bat (token baked in) + README

Run:  python deploy/make_payload.py
"""
import base64
import io
import os
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PAYLOAD = os.path.join(HERE, "payload")
TOKEN_FILE = os.path.expanduser("~/.workbuddy/pdms_cloud_token.txt")
TOOL_SRC = r"C:\AVEVA\Plant\PDMS12.1.SP4\PMLLIB\tool"
WS_URL = "wss://api.yuansutansuo.ltd/pdms/ws"


def read_token():
    try:
        with open(TOKEN_FILE, "r", encoding="utf-8") as f:
            for line in f:
                if line.startswith("token="):
                    return line.strip()[6:]
    except OSError:
        pass
    return "PUT_TOKEN_HERE"


def zip_dir(zf, src_dir, prefix=""):
    n = 0
    for root, _dirs, files in os.walk(src_dir):
        for fn in files:
            full = os.path.join(root, fn)
            arc = prefix + os.path.relpath(full, src_dir).replace("\\", "/")
            zf.write(full, arc)
            n += 1
    return n


def build_tool_zip():
    out = os.path.join(PAYLOAD, "pmllib_tool.zip")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zf:
        n = zip_dir(zf, TOOL_SRC, prefix="tool/")
    print("pmllib_tool.zip: %d files, %d bytes" % (n, os.path.getsize(out)))


BAT = """@echo off
rem PDMS Drawing cloud agent launcher
if not exist "%~dp0bridge\\agent.py" (
  echo [ERROR] bridge\agent.py not found next to this bat.
  echo Do NOT double-click Start-Agent.bat inside the zip viewer window
  echo ^(360zip/WinRAR only extracts the bat itself to a temp folder^).
  echo Extract the WHOLE zip to a real folder first, then run it again.
  pause
  exit /b 1
)
cd /d %~dp0
where python >nul 2>nul || (echo Python not found in PATH & pause & exit /b 1)
python -c "import websockets" 2>nul || pip install websockets
python bridge\\agent.py --url {ws} --token {tok} --machine %COMPUTERNAME%
pause
exit /b 0
"""

INSTALLER = r"""@echo off
rem PDMS Drawing cloud agent - self-extracting installer (payload embedded)
rem ASCII only by rule. Installs to %USERPROFILE%\pdmsAgent and starts agent.
setlocal EnableExtensions
set "DEST=%USERPROFILE%\pdmsAgent"
echo Installing PDMS cloud agent to %DEST% ...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$raw=[IO.File]::ReadAllText('%~f0');$idx=$raw.LastIndexOf('PDMS_AGENT_PAYLOAD_BASE64_BEGIN');$nl=$raw.IndexOf([char]10,$idx);$b64=(($raw.Substring($nl+1)).Split([char]10) | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^[A-Za-z0-9+/=]+$' }) -join '';$z=Join-Path $env:TEMP 'pdms_agent_install.zip';[IO.File]::WriteAllBytes($z,[Convert]::FromBase64String($b64));if(Test-Path '%DEST%'){Remove-Item -Recurse -Force '%DEST%'};Expand-Archive -Force $z '%DEST%';Remove-Item $z"
if errorlevel 1 (echo [ERROR] extract failed & pause & exit /b 1)
if not exist "%DEST%\bridge\agent.py" (echo [ERROR] extract verify failed & pause & exit /b 1)
echo Installed. Starting agent ...
cd /d "%DEST%"
where python >nul 2>nul || (echo Python not found in PATH & pause & exit /b 1)
python -c "import websockets" 2>nul || pip install websockets
python bridge\agent.py --url {ws} --token {tok} --machine %COMPUTERNAME%
pause
exit /b 0
rem payload base64 below - must stay the LAST content of this file
rem PDMS_AGENT_PAYLOAD_BASE64_BEGIN
{b64}
"""

README = """pdmsDrawing 云端 Agent 部署包
=============================

1. 解压本包到 PDMS 机器任意目录（如 D:\\pdmsAgent）
2. 双击 Start-Agent.bat（token 已内置，机器名自动取 COMPUTERNAME）
3. 回到云端平台 https://api.yuansutansuo.ltd/pdms/ 刷新，
   机器出现在「连接与机器」列表后选为出图机

要求：Python 3.9+（其余依赖 pip 自动装）
目录结构：bridge\\agent.py + bridge\\wizard.py，deploy\\deploy.py
"""


def build_agent_zip():
    out = os.path.join(PAYLOAD, "agent.zip")
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.write(os.path.join(ROOT, "bridge", "agent.py"), "bridge/agent.py")
        zf.write(os.path.join(ROOT, "bridge", "wizard.py"), "bridge/wizard.py")
        zf.write(os.path.join(HERE, "deploy.py"), "deploy/deploy.py")
        zf.writestr("Start-Agent.bat",
                    BAT.format(ws=WS_URL, tok=read_token()).replace("\n", "\r\n"))
        zf.writestr("README.txt", README)
    data = buf.getvalue()
    with open(out, "wb") as f:
        f.write(data)
    print("agent.zip: %d bytes" % len(data))
    # self-extracting single-file installer: double-click anywhere works
    b64 = base64.b64encode(data).decode("ascii")
    b64_lines = "\r\n".join(b64[i:i + 100] for i in range(0, len(b64), 100))
    inst = (INSTALLER.replace("{ws}", WS_URL).replace("{tok}", read_token())
            .replace("{b64}", b64_lines))
    inst = inst.replace("\r\n", "\n").replace("\n", "\r\n")  # all CRLF
    ipath = os.path.join(PAYLOAD, "install_agent.bat")
    with open(ipath, "w", encoding="ascii", newline="") as f:
        f.write(inst)
    print("install_agent.bat: %d bytes" % os.path.getsize(ipath))


if __name__ == "__main__":
    os.makedirs(PAYLOAD, exist_ok=True)
    build_tool_zip()
    build_agent_zip()
