#!/usr/bin/env bash
# Health watchdog - runs every 5 minutes from cron (installed by ops/setup.sh).
# Mails through the Brevo HTTP API when the app is down, the disk fills up or
# the nightly backup is late. Independent of the app: it can still send mail
# while the app is dead.
set -u

ENV_FILE="/etc/friseur-watchdog.env"
# shellcheck disable=SC1090
[ -f "$ENV_FILE" ] && . "$ENV_FILE"

APP_URL="${APP_URL:-http://127.0.0.1:3001}"
BACKUP_DIR="${BACKUP_DIR:-/opt/friseur/backups}"
STATE_DIR="/var/lib/friseur"
STATE_FILE="$STATE_DIR/watchdog.state"
mkdir -p "$STATE_DIR"
touch "$STATE_FILE"

get_state() { grep "^$1=" "$STATE_FILE" 2>/dev/null | cut -d= -f2; }
set_state() {
  grep -v "^$1=" "$STATE_FILE" > "$STATE_FILE.tmp" 2>/dev/null || true
  echo "$1=$2" >> "$STATE_FILE.tmp"
  mv "$STATE_FILE.tmp" "$STATE_FILE"
}

send_mail() { # $1=subject $2=text
  [ -z "${BREVO_API_KEY:-}" ] && return 0
  [ -z "${ALERT_TO:-}" ] && return 0
  curl -fsS -m 20 "https://api.brevo.com/v3/smtp/email" \
    -H "api-key: $BREVO_API_KEY" \
    -H "Content-Type: application/json" \
    -d "{\"sender\":{\"email\":\"${MAIL_FROM:-$ALERT_TO}\",\"name\":\"Friseur Watchdog\"},\"to\":[{\"email\":\"$ALERT_TO\"}],\"subject\":\"$1\",\"textContent\":\"$2\"}" \
    >/dev/null 2>&1
}

# Mails once when a check flips between FAIL and OK (no spam every 5 minutes)
check() { # $1=key $2=OK/FAIL $3=fail-subject $4=fail-text $5=recovered-subject
  local prev
  prev="$(get_state "$1")"
  prev="${prev:-OK}"
  if [ "$2" != "$prev" ]; then
    set_state "$1" "$2"
    if [ "$2" = "FAIL" ]; then
      send_mail "$3" "$4 ($(date '+%d.%m.%Y %H:%M'))"
    else
      send_mail "$5" "The problem went away, by itself or after a fix. ($(date '+%d.%m.%Y %H:%M'))"
    fi
  fi
}

# 1) App health (a second try after 15 s, to ride out a short blip)
health=FAIL
if curl -fsS -m 10 "$APP_URL/api/health" >/dev/null 2>&1; then
  health=OK
else
  sleep 15
  curl -fsS -m 10 "$APP_URL/api/health" >/dev/null 2>&1 && health=OK
fi
check app "$health" \
  "[Friseur] ALERT: the app is down" \
  "The app does not answer its health check. Connect to the server and run 'friseur restart'. Docker already tries to restart it on its own." \
  "[Friseur] Recovered: the app is running again"

# 2) Disk usage
usage="$(df -P / | awk 'NR==2 {gsub("%",""); print $5}')"
disk=OK
[ "${usage:-0}" -ge 90 ] && disk=FAIL
check disk "$disk" \
  "[Friseur] ALERT: disk filling up ($usage%)" \
  "The server disk is $usage% full. Run 'friseur prune'; old backups or logs may need cleaning too." \
  "[Friseur] Recovered: disk usage is back to normal"

# 3) Backup age (newest backup older than 26 hours)
newest="$(ls -t "$BACKUP_DIR"/friseur-*.dump "$BACKUP_DIR"/friseur-*.xlsx 2>/dev/null | head -1)"
if [ -n "$newest" ]; then
  age_h=$(( ( $(date +%s) - $(stat -c %Y "$newest") ) / 3600 ))
  bk=OK
  [ "$age_h" -gt 26 ] && bk=FAIL
  check backup "$bk" \
    "[Friseur] ALERT: the nightly backup did not run" \
    "The newest backup is $age_h hours old. Try 'friseur backup' by hand and check the logs." \
    "[Friseur] Recovered: backups are running again"
fi

# 4) Optional outside heartbeat (healthchecks.io): if the whole server dies,
#    this ping stops and the mail comes from there
[ -n "${HEARTBEAT_URL:-}" ] && curl -fsS -m 10 "$HEARTBEAT_URL" >/dev/null 2>&1

exit 0
