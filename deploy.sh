#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# BlockVerse Deployment Script
# Usage: ./deploy.sh [command]
#
# Commands:
#   up          - Start all services
#   down        - Stop all services
#   restart     - Restart backend only
#   logs        - Tail all logs
#   logs-api    - Tail API logs
#   status      - Show service status
#   backup      - Backup MongoDB
#   restore     - Restore latest backup
#   scale-game  - Scale game servers (usage: ./deploy.sh scale-game 3)
#   update-api  - Pull + rebuild + restart API only (zero-downtime)
#   ssl-renew   - Renew Let's Encrypt certificates
# ─────────────────────────────────────────────────────────────────────────────

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="$SCRIPT_DIR/docker-compose.yml"
ENV_FILE="$SCRIPT_DIR/../Backend/.env"
BACKUP_DIR="$SCRIPT_DIR/backups"
LOG_DIR="$SCRIPT_DIR/../logs"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

log()  { echo -e "${GREEN}[✓]${NC} $1"; }
warn() { echo -e "${YELLOW}[!]${NC} $1"; }
err()  { echo -e "${RED}[✗]${NC} $1" >&2; exit 1; }
info() { echo -e "${BLUE}[i]${NC} $1"; }

# ─── Preflight checks ─────────────────────────────────────────────────────────

check_deps() {
  command -v docker      &>/dev/null || err "docker not found"
  command -v docker-compose &>/dev/null || command -v docker &>/dev/null || err "docker compose not found"
  [[ -f "$ENV_FILE" ]] || err ".env file not found at $ENV_FILE — copy from .env.example and fill in values"
  log "Preflight checks passed"
}

compose() {
  docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"
}

# ─── Commands ─────────────────────────────────────────────────────────────────

cmd_up() {
  log "Starting BlockVerse services..."
  mkdir -p "$LOG_DIR" "$BACKUP_DIR"
  compose up -d --build
  compose ps
  log "All services started. API: http://localhost:3000/health"
}

cmd_down() {
  warn "Stopping all services..."
  compose down
  log "Services stopped."
}

cmd_restart() {
  log "Restarting API..."
  compose restart api
  log "API restarted."
}

cmd_logs() {
  compose logs -f --tail=100
}

cmd_logs_api() {
  compose logs -f --tail=200 api
}

cmd_status() {
  info "=== Service Status ==="
  compose ps
  echo ""
  info "=== Resource Usage ==="
  docker stats --no-stream --format "table {{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}\t{{.NetIO}}" \
    blockverse_api blockverse_mongo blockverse_redis blockverse_nginx 2>/dev/null || true
}

cmd_backup() {
  TIMESTAMP=$(date +%Y%m%d_%H%M%S)
  BACKUP_PATH="$BACKUP_DIR/mongo_$TIMESTAMP"
  mkdir -p "$BACKUP_PATH"

  info "Backing up MongoDB to $BACKUP_PATH..."

  docker exec blockverse_mongo mongodump \
    --username "${MONGO_ROOT_USER:-admin}" \
    --password "${MONGO_ROOT_PASS:-changeme}" \
    --authenticationDatabase admin \
    --db blockverse \
    --out /tmp/backup_$TIMESTAMP

  docker cp "blockverse_mongo:/tmp/backup_$TIMESTAMP" "$BACKUP_PATH"

  # Compress
  tar -czf "$BACKUP_PATH.tar.gz" -C "$BACKUP_DIR" "mongo_$TIMESTAMP"
  rm -rf "$BACKUP_PATH"

  log "Backup saved: $BACKUP_PATH.tar.gz"

  # Keep only last 7 backups
  ls -t "$BACKUP_DIR"/*.tar.gz 2>/dev/null | tail -n +8 | xargs rm -f || true
}

cmd_restore() {
  LATEST=$(ls -t "$BACKUP_DIR"/*.tar.gz 2>/dev/null | head -n1)
  [[ -z "$LATEST" ]] && err "No backups found in $BACKUP_DIR"

  warn "Restoring from: $LATEST"
  read -rp "Are you sure? This will OVERWRITE the current database! (yes/no): " CONFIRM
  [[ "$CONFIRM" != "yes" ]] && { info "Aborted."; exit 0; }

  RESTORE_DIR="$BACKUP_DIR/restore_tmp"
  mkdir -p "$RESTORE_DIR"
  tar -xzf "$LATEST" -C "$RESTORE_DIR"

  DUMP_DIR=$(ls "$RESTORE_DIR")
  docker cp "$RESTORE_DIR/$DUMP_DIR/blockverse" blockverse_mongo:/tmp/restore

  docker exec blockverse_mongo mongorestore \
    --username "${MONGO_ROOT_USER:-admin}" \
    --password "${MONGO_ROOT_PASS:-changeme}" \
    --authenticationDatabase admin \
    --db blockverse \
    --drop /tmp/restore

  rm -rf "$RESTORE_DIR"
  log "Restore complete."
}

cmd_scale_game() {
  REPLICAS="${2:-1}"
  info "Scaling game servers to $REPLICAS instances..."
  compose up -d --scale gameserver="$REPLICAS" gameserver
  log "Scaled to $REPLICAS game server(s)."
}

cmd_update_api() {
  log "Zero-downtime API update..."

  # Pull latest images
  compose pull api

  # Rebuild and restart with rolling update
  compose up -d --build --no-deps api

  # Wait for health check
  info "Waiting for API health check..."
  for i in {1..30}; do
    if curl -sf http://localhost:3000/health > /dev/null; then
      log "API is healthy after update."
      return 0
    fi
    sleep 2
    echo -n "."
  done

  err "API did not become healthy after update. Check logs with: ./deploy.sh logs-api"
}

cmd_ssl_renew() {
  info "Renewing SSL certificates..."
  docker run --rm \
    -v "$SCRIPT_DIR/nginx/ssl:/etc/letsencrypt" \
    -v "$SCRIPT_DIR/nginx/ssl-challenges:/var/www/certbot" \
    certbot/certbot renew --webroot \
    -w /var/www/certbot \
    --non-interactive \
    --agree-tos

  compose exec nginx nginx -s reload
  log "SSL certificates renewed and NGINX reloaded."
}

cmd_seed_items() {
  info "Seeding item database..."
  compose exec api node scripts/seed-items.js
  log "Items seeded."
}

# ─── Main ─────────────────────────────────────────────────────────────────────

check_deps

case "${1:-help}" in
  up)          cmd_up ;;
  down)        cmd_down ;;
  restart)     cmd_restart ;;
  logs)        cmd_logs ;;
  logs-api)    cmd_logs_api ;;
  status)      cmd_status ;;
  backup)      cmd_backup ;;
  restore)     cmd_restore ;;
  scale-game)  cmd_scale_game "$@" ;;
  update-api)  cmd_update_api ;;
  ssl-renew)   cmd_ssl_renew ;;
  seed-items)  cmd_seed_items ;;
  help|*)
    echo ""
    echo "BlockVerse Deployment Script"
    echo ""
    echo "Usage: $0 [command]"
    echo ""
    echo "Commands:"
    echo "  up           Start all services"
    echo "  down         Stop all services"
    echo "  restart      Restart API only"
    echo "  logs         Tail all logs"
    echo "  logs-api     Tail API logs only"
    echo "  status       Show service health + resources"
    echo "  backup       Backup MongoDB"
    echo "  restore      Restore latest backup"
    echo "  scale-game N Scale game servers to N instances"
    echo "  update-api   Zero-downtime API update"
    echo "  ssl-renew    Renew Let's Encrypt SSL certs"
    echo "  seed-items   Seed item database"
    echo ""
    ;;
esac
