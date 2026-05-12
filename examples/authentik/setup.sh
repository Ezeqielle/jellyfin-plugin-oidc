#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

echo "==> Generating secrets..."
cat > "$SCRIPT_DIR/.env" <<EOF
PG_PASS=$(openssl rand -hex 16)
AUTHENTIK_SECRET_KEY=$(openssl rand -hex 32)
EOF

echo "==> Building plugin..."
cd "$REPO_ROOT"
make docker-build

echo "==> Copying plugin DLLs..."
cp dist/*.dll "$SCRIPT_DIR/plugin/"

echo "==> Starting stack..."
cd "$SCRIPT_DIR"
docker compose up -d

echo ""
echo "Stack is starting up. Wait ~30 seconds, then:"
echo ""
echo "  1. Jellyfin initial setup:  http://localhost:8096"
echo "  2. Authentik initial setup: http://localhost:9000/if/flow/initial-setup/"
echo "  3. Follow SETUP.md for the OIDC configuration"
echo ""
