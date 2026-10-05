# Deployment

The default setup is **closed to the internet**: no port is open to the outside and no
domain is needed. Only devices on a private Tailscale network (the salon tablets, the
owner's phone, your laptop) can reach the app. Tailscale also provides the HTTPS
certificate, so the PWA ("add to home screen") works.

If the devices should not need a VPN, there is a second publish mode: the same app
behind a domain and a shared password gate, see [Public mode](#public-mode-domain--shared-password-gate).
The mode is one line in `.env`.

## What you need

1. **A small VPS** - 2 vCPU / 4 GB is plenty (e.g. Hetzner CX22, about €5/month), Ubuntu 24.04.
2. **A Tailscale account** - [tailscale.com](https://tailscale.com); the free plan is more than enough.
3. **A Brevo account** (optional) - [brevo.com](https://www.brevo.com), free plan
   (300 mails/day), for alert and backup mails.

## 1. Server

```bash
ssh root@SERVER_IP

curl -fsSL https://get.docker.com | sh
curl -fsSL https://tailscale.com/install.sh | sh
tailscale up          # open the printed link and approve the machine
```

In the Tailscale admin console, under **DNS**, make sure **MagicDNS** is on and
**HTTPS Certificates** are enabled.

## 2. Firewall: nothing open to the outside

```bash
ufw default deny incoming
ufw default allow outgoing
ufw allow in on tailscale0     # everything from the Tailscale network
ufw allow OpenSSH              # for the first setup only, see below
ufw --force enable
```

Once SSH over Tailscale works (`ssh root@<machine-name>`), close public SSH with
`ufw delete allow OpenSSH`. The hosting provider's web console still works in an emergency.

## 3. Code and configuration

```bash
git clone --recurse-submodules https://github.com/AtakanUk/salon-tracker-dotnet.git /opt/friseur
cd /opt/friseur
cp .env.example .env
nano .env
```

| Variable | Value |
|---|---|
| `DB_PASSWORD` | output of `openssl rand -hex 24` |
| `JWT_SECRET` | output of `openssl rand -hex 32` |
| `SALON_TZ` | the salon's time zone, e.g. `Europe/Berlin` |
| `SMTP_USER` / `SMTP_PASS` | Brevo → **SMTP & API** → SMTP key (leave empty to run without mail) |
| `MAIL_FROM` | a sender address verified in Brevo |
| `ALERT_TO` | where alerts and nightly backups go |

## 4. Start

```bash
docker compose up -d --build
docker compose exec -e SEED_LOCALE=tr app dotnet SalonTracker.Api.dll seed   # admin + sample price list
```

Migrations run automatically when the container starts. The seed **generates the
passwords and prints them once** - there are no default passwords. If one gets lost,
`friseur password` resets it (step 6). `SEED_LOCALE` (`tr`, `de` or `en`) is the
interface language of the new accounts; everyone can switch it later.

## 5. Publish over Tailscale (HTTPS)

```bash
tailscale serve --bg --https=443 http://127.0.0.1:3001
tailscale serve status    # shows the address
```

The app is now at `https://<machine-name>.<tailnet>.ts.net`.

## 6. Admin tooling

```bash
bash /opt/friseur/ops/setup.sh
nano /etc/friseur-watchdog.env    # BREVO_API_KEY, MAIL_FROM, ALERT_TO
```

- `friseur` now works from anywhere and opens the admin menu (see the
  [operator guide](https://github.com/AtakanUk/salon-tracker/blob/main/docs/operator-guide.md) - the
  menu and the web app are the same in both versions).
- The watchdog runs every 5 minutes: is the app answering, is the disk filling up, did
  the nightly backup run? It mails **once** when something breaks and once when it
  recovers. It talks to the Brevo HTTP API directly, so it works even when the app is dead.
- Optional: put a [healthchecks.io](https://healthchecks.io) ping URL into `HEARTBEAT_URL`
  to also get an alert if **the server itself** goes down.

Check it:

```bash
friseur status    # everything green?
friseur mail      # does the test mail arrive?
friseur backup    # take the first backup by hand, look at the attachments
```

## 7. First sign-in

Open the address from step 5, sign in as `admin` with the password the seed printed.
Then create the real employee accounts, delete the sample ones (Ali, Mehmet, Deniz) and
enter the real prices.

## 8. Tablets

For each tablet, once:

1. Install the **Tailscale** app and sign in with the same Tailscale account.
2. In the Tailscale admin console, **disable key expiry** for the device - otherwise it
   asks to sign in again every few months.
3. Open the app address in the browser and sign in with the employee account.
4. **Add to home screen**: iPad Safari → Share → *Add to Home Screen*; Android Chrome →
   menu → *Install app*.

## Public mode (domain + shared password gate)

If the tablets and the owner should not need a VPN, the same app can be published on a
domain. It is a **second publish mode**, not a fork: one line in `.env` switches it on,
and removing the line switches it off again.

> In this mode the sign-in page is reachable from the internet. That is why a **shared
> password gate** (basic auth) sits in front of it and the hardening steps below are not
> optional.

**1. Domain.** A free one is fine: [duckdns.org](https://www.duckdns.org) → pick a name →
enter the server's IPv4. A cron job keeps the record current if the IP changes:

```bash
echo '*/30 * * * * root curl -fsS "https://www.duckdns.org/update?domains=NAME&token=TOKEN&ip=" >/dev/null' > /etc/cron.d/duckdns
```

**2. Gate password hash:**

```bash
docker run --rm caddy caddy hash-password --plaintext 'the-shared-password'
```

**3. `.env`** (the lines are already in `.env.example`, commented out):

```bash
COMPOSE_FILE=docker-compose.yml:docker-compose.public.yml
PUBLIC_DOMAIN=yoursalon.duckdns.org
BASIC_AUTH_USER=salon
BASIC_AUTH_HASH='$2a$14$...'      # SINGLE quotes, or compose mangles the $ signs
GATE_COOKIE=<output of openssl rand -hex 32>
PUBLIC_BIND_IP=203.0.113.10       # the server's public IP, see below
```

> **Why `PUBLIC_BIND_IP`:** with Tailscale on the same server, `tailscale serve` holds
> port 443 on its own IP, and Caddy binding `0.0.0.0:443` fails with
> `address already in use`. Binding Caddy to the public IP lets both run side by side:
> Tailscale devices use the `.ts.net` address, everyone else the domain. Without
> Tailscale on the server the line can stay empty.

**4. Hardening:**

```bash
ufw allow 80/tcp && ufw allow 443/tcp
apt install -y fail2ban unattended-upgrades
dpkg-reconfigure -plow unattended-upgrades
sed -i 's/^#\?PasswordAuthentication.*/PasswordAuthentication no/' /etc/ssh/sshd_config
sed -i 's/^#\?PermitRootLogin.*/PermitRootLogin prohibit-password/' /etc/ssh/sshd_config
systemctl restart ssh
```

Keep the current SSH session open and check from a second terminal that you can still
get in. Note that ports published by Docker bypass ufw: the `ufw allow` lines document
intent, but closing the ports means removing the `COMPOSE_FILE` line, not the ufw rule.

**5. Start:**

```bash
docker compose up -d
friseur status    # "PUBLIC -> https://..."
```

Caddy gets the Let's Encrypt certificate by itself (about 30 seconds on first start).

### The gate asks once per device

Once the password is right, Caddy leaves a **2-year cookie** (`friseur_gate`); requests
carrying it never see the gate again. Basic auth alone cannot do that - browsers keep
basic auth credentials only for the session.

**Revoking access** (lost phone, former employee) means rotating the cookie value,
**not** changing the password - a device with the cookie never sees the gate, so it
would never notice a new password:

```bash
sed -i "s/^GATE_COOKIE=.*/GATE_COOKIE=$(openssl rand -hex 32)/" .env
docker compose up -d --force-recreate caddy
```

Every device is asked once more after that.

**Back to private mode:** comment out the lines in `.env`, then

```bash
docker compose down --remove-orphans && docker compose up -d
```

`--remove-orphans` matters: once the lines are gone, compose no longer knows about the
Caddy container, and without the flag it would keep running - `friseur status` would
say "Private" while the app is still public.

## Backup and restore

- **Automatic:** every night at 03:00 (salon time) a DB dump, an Excel file and a JSON
  export go to `/opt/friseur/backups` and are mailed. The server keeps **60 days** of
  daily backups plus **one per month for good** (the oldest day of each month on disk).
- **By hand:** `friseur backup`
- **Full rollback:** `friseur restore` → pick a dump → type `YES`. A copy of the current
  state (`pre-restore-*.dump`) is taken first.
- **Recover missing records:** `friseur import` merges a `.json.gz` backup into the live
  database and only inserts rows whose id is missing - nothing is changed or deleted. The
  owner can do the same from the admin panel (System → Restore from backup).
- **Server gone:** set up a new one, put the `.dump` from the mail into
  `/opt/friseur/backups/`, run `friseur restore`.

## Updates

```bash
friseur update
```

Pulls (including the pinned frontend version), rebuilds and restarts; EF Core migrations run
on start, data is kept. It also prunes old
images and build cache. Skipping that is what actually fills the disk: on the live
server, 21 days of build cache took 5.5 GB while all of the salon's data and backups
took 680 KB.

## Troubleshooting

| Symptom | Action |
|---|---|
| Tablets cannot reach the site | Is Tailscale on on the tablet? `friseur status` |
| "The app is down" mail | `friseur restart`; if that fails, `friseur errors` |
| Certificate error | `tailscale serve status`; is HTTPS enabled in the Tailscale console? |
| No backup mail | `friseur mail`; SMTP values in `.env`; `friseur errors` |
| Nobody can sign in to the admin panel | `friseur password` → pick the user → new password |
| Which publish mode am I in? | `friseur status` → "Publish mode" |
| Domain does not load / no certificate | Does the DNS record point to the right IP? Is port 80 Caddy's (`docker compose logs caddy`)? |
| Everything is broken | Reboot the server from the hosting console - everything comes back up by itself |
