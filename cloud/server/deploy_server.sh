#!/usr/bin/env bash
# deploy_server.sh - one-shot installer for the cloud relay on a fresh
# Ubuntu/Debian server. Run as root from the repo root (pdmsDrawing/).
#   bash cloud/server/deploy_server.sh yourdomain.com STRONG_TOKEN
set -euo pipefail

DOMAIN="${1:?usage: deploy_server.sh <domain> <token>}"
TOKEN="${2:?usage: deploy_server.sh <domain> <token>}"
DEST=/opt/pdmsDrawing

# 1. sync code
mkdir -p "$DEST"
rsync -a --delete --exclude 'venv' --exclude '__pycache__' --exclude 'uploads' \
      "$(dirname "$0")/../.." "$DEST"/

# 2. venv + deps
cd "$DEST/cloud"
[ -x venv/bin/python ] || python3 -m venv venv
venv/bin/pip install -q -U pip
venv/bin/pip install -q -r requirements.txt

# 3. uploads dir + systemd unit with real token
mkdir -p uploads
sed "s/REPLACE_WITH_STRONG_SECRET/$TOKEN/" server/pdms-relay.service > /etc/systemd/system/pdms-relay.service
systemctl daemon-reload
systemctl enable --now pdms-relay

# 4. nginx
apt-get install -y -q nginx >/dev/null
sed "s/pdms.example.com/$DOMAIN/" server/nginx-pdms.conf > /etc/nginx/sites-available/pdms
ln -sf /etc/nginx/sites-available/pdms /etc/nginx/sites-enabled/pdms
rm -f /etc/nginx/sites-enabled/default
nginx -t && systemctl reload nginx

# 5. firewall (80/443 only; WS is proxied, no 8765/8766 exposure)
if command -v ufw >/dev/null; then ufw allow 80/tcp; ufw allow 443/tcp; fi

echo
echo "relay status : $(systemctl is-active pdms-relay)"
echo "health       : curl http://127.0.0.1:8766/health"
echo "next         : certbot --nginx -d $DOMAIN   (TLS for page + wss)"
echo "local agent  : python agent.py --url wss://$DOMAIN/ws --token $TOKEN"
