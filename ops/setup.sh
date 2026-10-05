#!/usr/bin/env bash
# Run once on the server as root: bash ops/setup.sh
# Installs the 'friseur' command and the watchdog cron job.
set -eu

DIR="$(cd "$(dirname "$(readlink -f "$0")")/.." && pwd)"
echo "Project folder: $DIR"

chmod +x "$DIR/ops/friseur.sh" "$DIR/ops/watchdog.sh"
ln -sf "$DIR/ops/friseur.sh" /usr/local/bin/friseur
mkdir -p /var/lib/friseur "$DIR/backups"

if [ ! -f /etc/friseur-watchdog.env ]; then
  cat > /etc/friseur-watchdog.env <<EOF
# Watchdog alert settings (works independently of the app)
# Create an API key in your Brevo account: brevo.com -> SMTP & API -> API Keys
BREVO_API_KEY=
MAIL_FROM=
ALERT_TO=
APP_URL=http://127.0.0.1:3001
BACKUP_DIR=$DIR/backups
# Optional: a healthchecks.io ping URL, for an outside alert if the server itself dies
HEARTBEAT_URL=
EOF
  chmod 600 /etc/friseur-watchdog.env
  echo ""
  echo ">> Created /etc/friseur-watchdog.env."
  echo ">> Fill in BREVO_API_KEY, MAIL_FROM and ALERT_TO: nano /etc/friseur-watchdog.env"
fi

cat > /etc/cron.d/friseur <<EOF
# Friseur watchdog - every 5 minutes
*/5 * * * * root FRISEUR_DIR=$DIR $DIR/ops/watchdog.sh >/dev/null 2>&1
EOF
chmod 644 /etc/cron.d/friseur

echo ""
echo "Setup done ✓  Admin menu: friseur"
