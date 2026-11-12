# pdmsDrawing 云端化骨架（A 方案）

## 架构

```
浏览器(wizard页)          云服务器                    本地 PDMS 机器
┌────────────┐   ws    ┌──────────────┐    ws 出站   ┌─────────────────┐
│ cloud/web/ │ ──────▶ │ cloud/       │ ◀────────── │ bridge/agent.py │
│ index.html │  8765   │ relay.py     │  (反连,     │  ├─ wizard.py   │
└────────────┘         │  ├ 任务路由   │   无入站口)  │  │  (流水线复用) │
   PDF预览 ◀────────── │  ├ PDF落盘   │ ──────────▶ │  └─ 18753       │
   (uploads/静态)       │  └ payload  │   本地       │     PdmsBridge  │
                       └──────────────┘  bridge执行  └─────────────────┘
```

关键点：
- PdmsBridge 插件永远在本地 PDMS 进程内（127.0.0.1:18753），搬不动。
- agent.py 驻留 PDMS 机器，**出站** WebSocket 连云端——不需要公网 IP、不开入站端口。
- `deploy` 指令 = 从云端下载 payload + 装 DesignAddins.xml（复用 deploy.py 的
  `find_pdms()/install()`），新机器"装 agent → 一键装插件"。

## 文件

| 文件 | 跑在哪 | 作用 |
|---|---|---|
| `cloud/relay.py` | 云服务器 | WS 中继：agent 注册/任务路由/进度转发/PDF 落盘/payload 托管 |
| `cloud/web/index.html` | relay 静态托管 | 云端 wizard 页（10 段动画与本地版同一套标记匹配） |
| `bridge/agent.py` | 本地 PDMS 机器 | 驻留 agent：反连云端，复用 wizard.py 流水线 + deploy.py 装机 |
| `cloud/smoke_test.py` | 任意机器 | 无 PDMS 冒烟：web→relay→agent→bridge→relay→web 全环验证 |
| `deploy/deploy.py` | 本地 | `install()` 已抽函数，CLI 与云端 deploy 共用 |

## 协议（JSON，每帧一条）

| 方向 | 消息 |
|---|---|
| 双方→relay | `hello {role:"agent"|"web", token, machine_id}`（首条，token 不对即断） |
| web→relay | `submit_task {task_id, machine_id, op, params}` / `deploy {machine_id}` |
| relay→agent | `task {task_id, op, params}` / `deploy {task_id}` |
| agent→relay | `progress {task_id, log}` / `preview {task_id, b64}` / `pdf {task_id, filename, b64}` / `task_result {task_id, ok, error, output}` |
| relay→web | `agents {machines}` / `task_accepted` / 上行透传 progress/preview/task_result / `pdf_ready {url}` |

op 取值：`pipeline_draw`（全流水线，参数同本地 wizard /draw 表单）、`get_ce`、
`material_report`、`exec_cmds`、`export_pdf` 等任意 PdmsBridge op 原样透传。

## 跑法

云端（Linux 服务器均可，仅依赖 `pip install websockets`）：
```bash
python relay.py --ws-port 8765 --http-port 8766 --token <强随机串>
# 建议 nginx/wss 反代 + 防火墙只放行 443
```

本地 PDMS 机器（开机自启可用任务计划）：
```bash
python bridge/agent.py --url wss://你的域名 --http https://你的域名 \
  --token <同一串> --machine 车间-01
```

验证（无需 PDMS）：`python cloud/smoke_test.py` → 输出 PASS 即全环通。

## 服务器一键部署（cloud/server/）

| 文件 | 用途 |
|---|---|
| `deploy_server.sh` | 一键安装：同步代码 → venv + websockets → systemd 常驻 → nginx 反代 → 防火墙 |
| `pdms-relay.service` | systemd 单元（relay 崩了 3 秒自动拉起） |
| `nginx-pdms.conf` | 反代：页面/uploads/payload → 8766，`/ws`（含 Upgrade 头）→ 8765 |

全新 Ubuntu/Debian 服务器上，把 pdmsDrawing 仓库传上去后一条命令：
```bash
bash cloud/server/deploy_server.sh pdms.你的域名.com <强随机token>
# 然后签 TLS：certbot --nginx -d pdms.你的域名.com
```
完成后：浏览器开 `https://域名` 即平台页面（https 自动走 wss://域名/ws，已内置）；
本地 PDMS 机器上 `python bridge/agent.py --url wss://域名/ws --token <同串> --machine 车间-01`。
防火墙只开 80/443，8765/8766 不暴露。

## 接入公司官网（yuansutansuo.ltd，2026-10-22）

官网=nginx/1.20.1 静态站 + 子目录应用（filmstudio/、globe/ 同款模式）。平台照此接入，
**同一台服务器、同域同证书，无需新域名/DNS**：`https://api.yuansutansuo.ltd/pdms/`

- `server/deploy_yuansu.sh` — 服务器上一键装 relay（systemd，仅监听 127.0.0.1，`--url-prefix /pdms`）；
  **不碰生产 nginx 和首页**，只打印两段待粘贴内容
- `server/nginx-yuansu-pdms.conf` — 贴进 api.yuansutansuo.ltd 的 server{} 块：
  `/pdms/`→8766（前缀透传，relay 兼容带/不带前缀两种路径），`/pdms/ws`→8765（WS Upgrade）
- `server/homepage-card.html` — 官网首页产品区加的同款 `pcard` 卡片（工业软件 · PDMS 智能出图）
- relay.py 新增 `--url-prefix`：uploads/payload 链接自动带前缀；页面 JS 在 `/pdms` 路径下自动连 `wss://host/pdms/ws`
- 本地 PDMS 机器：`python bridge/agent.py --url wss://api.yuansutansuo.ltd/pdms/ws --token <同串> --machine 车间-01`

步骤：服务器上 `sudo bash cloud/server/deploy_yuansu.sh <强token>` → 按提示贴 nginx 片段 + 首页卡片 → reload nginx。

## 无人操控（v5）

本地 `/control` 页与云端「无人操控」页 = 运行状态自检 + PML 控制台。
铁律落地：指令**逐行下发**（一次多条曾 RunInPdms failed），默认出错即停（可切换继续），
每行结果/错误实时回显；云端经 agent `exec_pml` 同样逐行 + progress 实时推流。
注意：查询行 Q CE/Q NAME/Q REF/Q TYPE/Q CREF 已自动改走 get_ce op 回读真值（v5.1）；
其余查询行回显在 PDMS 命令窗口；执行类指令（SAVEWORK/UPDATE/建库建模宏）看 ok/error 即可。
PDMS 里别弹模态框，否则宏线程挂起。

## 安全与 TODO

- 鉴权：共享 token（hello 首条校验）；HTTP 下载带 `?t=` 校验。上线前换强随机串。
- TODO（骨架未做，按需加）：
  1. ~~wss/nginx 反代~~（已做：cloud/server/nginx-pdms.conf + 页面自动 wss）
  2. 任务持久化 + 断线重挂（现在 relay 重启任务即丢）
  3. payload 版本管理（drawingPDMS.dll 编译号下发比对）
  4. wizard.py 的 OUTDIR/LOG 常量参数化（目前写死 D:\pdmsAgent\bridge\，换机器部署时改）
  5. relay 挂 HTTPS 时 agent 需校验证书（生产建议 certbot 真证书而非自签）
