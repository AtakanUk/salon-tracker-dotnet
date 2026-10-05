#!/usr/bin/env bash
# Friseur admin command. Menu: friseur
# Direct: friseur status|restart|restart-all|logs|errors|backup|restore|import|mail|update|password|prune
set -u

SCRIPT="$(readlink -f "$0")"
FRISEUR_DIR="${FRISEUR_DIR:-$(dirname "$(dirname "$SCRIPT")")}"
cd "$FRISEUR_DIR" || { echo "Project folder not found: $FRISEUR_DIR"; exit 1; }

dc() { docker compose "$@"; }

# Reads a setting from .env (skips comments, strips quotes).
env_get() {
  sed -n "s/^[[:space:]]*$1=//p" .env 2>/dev/null | tail -1 | tr -d "\"'" | tr -d '\r'
}

# Private network or public internet? (see .env.example -> Publish mode)
publish_mode() {
  if env_get COMPOSE_FILE | grep -q 'docker-compose\.public\.yml'; then
    echo "PUBLIC  ->  https://$(env_get PUBLIC_DOMAIN)"
    echo "  (shared password gate first, then the app's own login)"
  else
    echo "Private - only devices on the Tailscale network can reach it."
  fi
}

status() {
  echo
  echo "-- Services ---------------------------------------"
  dc ps --format "table {{.Service}}\t{{.Status}}"
  echo
  echo -n "App health check: "
  if curl -fsS -m 5 http://127.0.0.1:3001/api/health >/dev/null 2>&1; then
    echo "RUNNING ✓"
  else
    echo "NO RESPONSE ✗   -> try 'friseur restart'"
  fi
  echo
  echo "-- Publish mode -----------------------------------"
  publish_mode
  echo
  echo "-- Disk -------------------------------------------"
  df -h / | awk 'NR==2 {print "Used: "$3" / "$2"  ("$5")"}'
  echo
  echo "-- Latest backups ---------------------------------"
  ls -lht backups/friseur-* 2>/dev/null | head -6 | awk '{print $9"  ("$5")"}' || true
  [ -z "$(ls backups/friseur-* 2>/dev/null)" ] && echo "No backups yet."
  echo
}

restart_app() { echo "Restarting the app..."; dc restart app && echo "Done ✓"; }
restart_all() { echo "Restarting everything..."; dc restart && echo "Done ✓"; }
logs() { echo "(Ctrl+C to quit)"; dc logs --tail 200 -f app; }
errors() { dc logs --tail 2000 app 2>/dev/null | grep -iE "error|err:|fatal" | tail -50; }
# The server-side tools are commands of the app itself (dotnet SalonTracker.Api.dll help)
api() { dc exec app dotnet SalonTracker.Api.dll "$@"; }
run_backup() { api backup; }
test_mail() { api test-mail; }
reset_password() { api reset-password "$@"; }
# Every build leaves new Docker layers behind and nothing removes them.
# This is what actually fills the disk: measured once, 21 days of build cache
# took 5.5 GB while all of the salon's data and backups took 680 KB.
# Data, backups and running containers are not touched.
prune_docker() {
  docker image prune -f >/dev/null 2>&1
  # --max-used-space exists in newer Docker releases; if unknown, clear the whole cache
  docker builder prune -f --max-used-space 2GB >/dev/null 2>&1 ||
    docker builder prune -f >/dev/null 2>&1
}

prune() {
  echo "Before:"
  docker system df
  echo
  echo "Removing old images and build leftovers..."
  prune_docker
  echo
  echo "After:"
  docker system df
  echo
  df -h / | awk 'NR==2 {print "Server disk: "$3" / "$2"  ("$5" used)"}'
  echo
  echo "Done ✓  Data, backups and running containers were not touched."
}

update() {
  if [ -d .git ]; then
    echo "Fetching new code (git pull)..."
    git pull --ff-only || { echo "git pull failed, update stopped."; return 1; }
    # the web app is a submodule pinned to a frontend version; follow the pin
    git submodule update --init || { echo "Frontend update failed, update stopped."; return 1; }
  fi
  echo "Rebuilding and starting..."
  dc up -d --build || { echo "Build failed, update stopped."; return 1; }
  echo "Done ✓"
  echo "Removing old build leftovers..."
  prune_docker
  echo "Done ✓"
}

# Lists backup files with numbers; puts the chosen one into $PICKED.
pick_backup() {
  local pattern="$1" question="$2"
  PICKED=""
  mapfile -t files < <(ls -1t $pattern 2>/dev/null)
  if [ ${#files[@]} -eq 0 ]; then echo "No backup files found: $pattern"; return 1; fi
  local i=1
  for f in "${files[@]}"; do
    echo "  $i) $(basename "$f")  ($(du -h "$f" | cut -f1))"
    i=$((i + 1))
  done
  echo
  read -rp "$question (number, Enter to cancel): " num
  [ -z "$num" ] && { echo "Cancelled."; return 1; }
  PICKED="${files[$((num - 1))]:-}"
  [ -z "$PICKED" ] && { echo "Invalid number."; return 1; }
  return 0
}

# Adds the records of a JSON backup that are missing here (existing ones are left alone).
import_json() {
  echo "This adds back the records from a backup that are NOT in the system."
  echo "Existing records do not change, nothing is deleted."
  echo
  echo "Available JSON backups (newest first):"
  pick_backup "backups/*.json.gz" "Which backup should missing records come from?" || return 1
  # -T: the tool needs no input, so it also works without a TTY (scripts, ssh)
  dc exec -T app dotnet SalonTracker.Api.dll import-json "/backups/$(basename "$PICKED")"
}

restore() {
  echo "Available database backups (newest first):"
  pick_backup "backups/*.dump" "Which backup should the database go back to?" || return 1
  local file="$PICKED"
  echo
  echo "██ WARNING ███████████████████████████████████████████"
  echo "The database goes back to the moment '$(basename "$file")' was taken."
  echo "Every record entered AFTER this backup will be lost!"
  echo "██████████████████████████████████████████████████████"
  read -rp "Type YES in capitals to confirm: " confirm
  [ "$confirm" != "YES" ] && { echo "Cancelled."; return 0; }

  echo "Taking a copy of the current state first..."
  if dc exec -T db pg_dump -U friseur -Fc --no-owner friseur > "backups/pre-restore-$(date +%Y%m%d-%H%M%S).dump"; then
    echo "  -> backups/pre-restore-*.dump (go back to this one if you change your mind)"
  else
    echo "  (warning: the safety copy failed, continuing anyway)"
  fi
  echo "Stopping the app..."
  dc stop app
  echo "Restoring..."
  if dc exec -T db pg_restore -U friseur -d friseur --clean --if-exists --no-owner "/backups/$(basename "$file")"; then
    echo "Restore done ✓"
  else
    echo "pg_restore reported warnings (mostly harmless); check the status."
  fi
  echo "Starting the app..."
  dc start app
  sleep 3
  status
}

menu() {
  while true; do
    cat <<'EOF'

  ╔═══════════════════════════════════════╗
  ║            FRISEUR ADMIN              ║
  ╠═══════════════════════════════════════╣
  ║  1) Show status                       ║
  ║  2) Restart the app                   ║
  ║  3) Restart everything                ║
  ║  4) Follow logs (quit: Ctrl+C)        ║
  ║  5) Show error logs                   ║
  ║  6) Back up now                       ║
  ║  7) Restore a backup (everything)     ║
  ║  8) Add missing records from backup   ║
  ║  9) Send a test mail                  ║
  ║ 10) Update (rebuild)                  ║
  ║ 11) Reset a password (locked out)     ║
  ║ 12) Disk cleanup (frees space)        ║
  ║  0) Quit                              ║
  ╚═══════════════════════════════════════╝
EOF
    read -rp "  Your choice: " choice
    case "$choice" in
      1) status ;;
      2) restart_app ;;
      3) restart_all ;;
      4) logs ;;
      5) errors ;;
      6) run_backup ;;
      7) restore ;;
      8) import_json ;;
      9) test_mail ;;
      10) update ;;
      11) reset_password ;;
      12) prune ;;
      0) exit 0 ;;
      *) echo "  Invalid choice." ;;
    esac
  done
}

case "${1:-menu}" in
  menu) menu ;;
  status) status ;;
  restart) restart_app ;;
  restart-all) restart_all ;;
  log | logs) logs ;;
  errors) errors ;;
  backup) run_backup ;;
  restore) restore ;;
  import) import_json ;;
  mail) test_mail ;;
  update) update ;;
  password) shift; reset_password "$@" ;;
  prune) prune ;;
  *)
    echo "Usage: friseur [status|restart|restart-all|logs|errors|backup|restore|import|mail|update|password|prune]"
    echo "  restore                      -> returns the database to a .dump backup (everything goes back)"
    echo "  import                       -> adds the MISSING records from a .json.gz backup"
    echo "  password                     -> pick a user from a list and reset their password"
    echo "  password <user> <password>   -> reset directly"
    echo "  prune                        -> removes old Docker build leftovers (frees space)"
    exit 1
    ;;
esac
