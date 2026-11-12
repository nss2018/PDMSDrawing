#!/usr/bin/env bash
# deploy_yuansu.sh - install the pdmsDrawing relay on the yuansutansuo.ltd
# server (same box as the official site, nginx/1.20.1). Does NOT touch the
# production nginx config or the homepage - it prints the two snippets to
# paste (nginx location block + homepage product card).
#   sudo bash deploy_yuansu.sh <strong-token>
set -euo pipefail

TOKEN="${1:?usage: deploy_yuansu.sh <strong-token>}"
DEST=/opt/pdmsDrawing

# 1. sync code (run from repo root on the server)
mkdir -p "$DEST"
rsync -a --delete --exclude 'venv' --exclude '__pycache__' --exclude 'uploads' \
      "$(dirname "$0")/../.." "$DEST"/

# 2. venv + deps
cd "$DEST/cloud"
[ -x venv/bin/python ] || python3 -m venv venv
venv/bin/pip install -q -U pip
venv/bin/pip install -q -r requirements.txt

# 3. systemd (relay on loopback only, sub-path /pdms)
mkdir -p uploads
sed "s|^ExecStart=.*|ExecStart=$DEST/cloud/venv/bin/python relay.py --ws-port 8765 --http-port 8766 --token $TOKEN --url-prefix /pdms|" \
    server/pdms-relay.service > /etc/systemd/system/pdms-relay.service
systemctl daemon-reload
systemctl enable --now pdms-relay
sleep 1
echo "relay: $(systemctl is-active pdms-relay)  health: $(curl -s http://127.0.0.1:8766/health)"

cat <<'EOF'

== 下一步两段手工粘贴（脚本不碰你的生产配置） ==

[1] nginx：把 cloud/server/nginx-yuansu-pdms.conf 的内容贴进
    api.yuansutansuo.ltd 的 server{} 块（宝塔：网站设置->配置文件），
    然后 nginx -t && systemctl reload nginx。

[2] 官网首页：把 cloud/server/homepage-card.html 的内容贴进
    index.html 的 <div class="grid"> 产品区（与 filmstudio/globe 卡片并列）。

完成后浏览器打开 https://api.yuansutansuo.ltd/pdms/ 验证页面；
本机 PDMS 机器上跑：
  python bridge/agent.py --url wss://api.yuansutansuo.ltd/pdms/ws \
    --token <同一串> --machine 车间-01
EOF
