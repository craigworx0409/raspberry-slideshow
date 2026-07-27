# Step 3 — Deploy the slideshow server

*Prerequisites: Step 2 complete (VM with .NET runtime + Caddy, DNS record
`slideshow.your-domain.com → YOUR-VM-IP` on a gray cloud). On Windows you
need the [.NET 10 SDK](https://dotnet.microsoft.com/download), PowerShell 7,
and a clone of this repo.*

The pattern below — per-app Linux user, `/srv/<app>/` directory trio, an
`env` file for secrets, a systemd unit, a Caddy block, an atomic deploy
script — is the same for **every** app you'll ever put on this VM. Learn it
once here; [Step 7](07-new-app-runbook.md) turns it into a fill-in-the-blanks
recipe.

## 3.1 Provision on the VM

SSH in (`ssh root@YOUR-VM-IP`) and paste, one block at a time.

A dedicated low-privilege user and the directory layout:

```bash
useradd --system --home-dir /srv/slideshow --shell /usr/sbin/nologin slideshow
mkdir -p /srv/slideshow/{app,data,media,backups}
chown -R slideshow:slideshow /srv/slideshow
```

(`app/` = replaceable binaries, `data/` = the SQLite database, `media/` =
your images. Deploys replace `app/` atomically and never touch the rest.)

The environment file — this is where configuration and **secrets** live, and
the reason none of them are ever in git:

```bash
cat > /srv/slideshow/env <<'EOF'
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5002
DbPath=/srv/slideshow/data/slideshow.db
MediaPath=/srv/slideshow/media
DeviceKey=
AdminUser=admin
AdminPassword=
EOF
chown slideshow:slideshow /srv/slideshow/env && chmod 600 /srv/slideshow/env
```

Now fill in the two blank secrets: `nano /srv/slideshow/env` and set

- `DeviceKey=` — a long random string (25+ characters); every Pi will
  present this to authenticate. Generate one however you like, e.g. in
  PowerShell: `-join ((48..57)+(97..122) | Get-Random -Count 32 | % {[char]$_})`
- `AdminPassword=` — the password for the admin/library pages.

The systemd unit (the "Windows service" definition):

```bash
cat > /etc/systemd/system/slideshow.service <<'EOF'
[Unit]
Description=Raspberry Slideshow server
After=network.target

[Service]
Type=simple
User=slideshow
WorkingDirectory=/srv/slideshow/app
EnvironmentFile=/srv/slideshow/env
ExecStart=/usr/bin/dotnet /srv/slideshow/app/SlideshowServer.dll
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload && systemctl enable slideshow
```

The Caddy site block (replace the domain):

```bash
cat >> /etc/caddy/Caddyfile <<'EOF'

slideshow.your-domain.com {
    reverse_proxy 127.0.0.1:5002
    request_body {
        max_size 200MB
    }
}
EOF
caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile && systemctl reload caddy
```

That's the entire server config: Caddy owns ports 80/443 and HTTPS
certificates; your app listens only on localhost:5002.

## 3.2 First deploy from Windows

Edit `deploy/deploy-vm.ps1` in your repo clone — one line near the top:

```powershell
[CmdletBinding()] param([string]$SshHost='root@YOUR-VM-IP', ...
```

Put your real IP there (or pass `-SshHost` each time). Then:

```powershell
pwsh deploy/deploy-vm.ps1
```

What it does, every time, in ~1 minute: publishes the app for Linux, ships
one tarball over SSH, unpacks to `app.new`, stops the service, swaps
`app` → `app.old` → `app.new` becomes `app`, starts the service, and
health-checks it. Ends with:

```
service: active
http:    200
==> Deploy OK.
```

**Rollback** is the previous folder, one line:

```bash
ssh root@YOUR-VM-IP "systemctl stop slideshow && rm -rf /srv/slideshow/app && mv /srv/slideshow/app.old /srv/slideshow/app && systemctl start slideshow"
```

## 3.3 Verify

- `https://slideshow.your-domain.com/health` → `ok` (first hit may take a
  few seconds while Caddy obtains the certificate).
- `/library/` → browser prompts for the admin login → the Image Library.
  Create a collection, drag some photos in — they're resized server-side to
  1080p (a deliberate cap: it's all a frame can display, and all a 512 MB Pi
  should be asked to decode).
- `/slideshow/` → fullscreen slideshow of what you uploaded. Parameters:
  `?group=Name` (or `A,B`), `&delay=8000` (ms), `&fade=1200`, `&shuffle=0`.
- `/admin/` → device admin. Empty until Step 4 — the moment your first Pi
  boots, it appears here by itself.
- `/` → the per-owner view (assign owners to devices from `/admin/` later).

## 3.4 Back up the one thing that matters

Nightly database + media copy, kept 14 days, as root:

```bash
apt install -y sqlite3
cat > /etc/cron.daily/slideshow-backup <<'EOF'
#!/bin/sh
sqlite3 /srv/slideshow/data/slideshow.db ".backup /srv/slideshow/backups/db-$(date +%F).db"
tar -czf /srv/slideshow/backups/media-$(date +%F).tgz -C /srv/slideshow media
find /srv/slideshow/backups -mtime +14 -delete
EOF
chmod +x /etc/cron.daily/slideshow-backup
```

For off-box safety, periodically pull a copy to your PC:

```powershell
scp root@YOUR-VM-IP:/srv/slideshow/backups/db-*.db .
```

Next: [Step 4 — build the first frame](04-build-the-frame.md).
