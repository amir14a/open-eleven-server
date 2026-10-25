#!/bin/sh
# certbot deploy hook: copies the renewed certificate where the containers read it and
# restarts them. certbot keeps privkey.pem readable by root only; the containers run as
# the unprivileged "app" user (uid 1654), so they get their own readable copy.
#
# Install once, with the path to this repository's docker/ folder:
#   certbot certonly --standalone -d pes.example.com \
#     --deploy-hook "/path/to/repo/docker/certbot-deploy-hook.sh"
set -e
DOCKER_DIR="$(cd "$(dirname "$0")" && pwd)"
CERTS="$DOCKER_DIR/../certs"

mkdir -p "$CERTS"
cp -L "$RENEWED_LINEAGE/fullchain.pem" "$CERTS/fullchain.pem"
cp -L "$RENEWED_LINEAGE/privkey.pem" "$CERTS/privkey.pem"
chown 1654:1654 "$CERTS/fullchain.pem" "$CERTS/privkey.pem"
chmod 644 "$CERTS/fullchain.pem"
chmod 600 "$CERTS/privkey.pem"

docker compose -f "$DOCKER_DIR/docker-compose.yml" restart pes2010pc pes2011pc pes2012pc pes2013pc
