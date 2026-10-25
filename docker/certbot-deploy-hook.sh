#!/bin/sh
# Copies the Let's Encrypt certificate into certs/ (where the containers read it) and
# restarts the title services. Run it on the host after certbot issues or renews:
#
#   RENEWED_LINEAGE=/etc/letsencrypt/live/pes.example.com docker/certbot-deploy-hook.sh
set -e
DOCKER_DIR="$(cd "$(dirname "$0")" && pwd)"
CERTS="$DOCKER_DIR/../certs"

mkdir -p "$CERTS"
cp -L "$RENEWED_LINEAGE/fullchain.pem" "$CERTS/fullchain.pem"
cp -L "$RENEWED_LINEAGE/privkey.pem" "$CERTS/privkey.pem"
chmod 644 "$CERTS/fullchain.pem"
chmod 600 "$CERTS/privkey.pem"

docker compose -f "$DOCKER_DIR/docker-compose.yml" restart pes2010pc pes2011pc pes2012pc pes2013pc
