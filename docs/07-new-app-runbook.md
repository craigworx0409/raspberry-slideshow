# Step 7 — The runbook: any new .NET app on the same VM, in 15 minutes

The slideshow server occupies one port on a box that can host a dozen apps.
This chapter is the deploy pattern from Step 3 distilled into a
fill-in-the-blanks recipe, so your next idea goes from `dotnet new` to
`https://idea.your-domain.com` in a coffee break — plus a note on teaching
it to an AI assistant so you never type it by hand at all.

## 7.1 Parameters

Decide these up front:

```text
SERVICE  = ____   # short slug: linux user, /srv/<SERVICE>, <SERVICE>.service   e.g. recipebox
PROJECT  = ____   # path to the web .csproj in your repo    e.g. src/RecipeBox/RecipeBox.csproj
DLL      = ____   # published entry dll                     e.g. RecipeBox.dll
DOMAIN   = ____   # public FQDN                             e.g. recipes.your-domain.com
PORT     = ____   # next free localhost port — see registry below
SECRETS  = ____   # env var KEY NAMES the app needs (never write values down)
```

**Port registry** — keep a table like this in your own notes; every app gets
its own localhost port behind Caddy:

| Port | Service | Domain |
|---|---|---|
| 5002 | slideshow | slideshow.your-domain.com |
| 5003 | *(next free)* | |

Check before claiming: `ssh root@YOUR-VM-IP 'ss -ltnp | grep :5003'` must
print nothing.

## 7.2 The recipe

**A. DNS (Cloudflare, 1 min):** A record, name = subdomain, IPv4 =
`YOUR-VM-IP`, **DNS only / gray cloud**.

**B. Provision on the VM** (substitute your parameters):

```bash
useradd --system --home-dir /srv/SERVICE --shell /usr/sbin/nologin SERVICE
mkdir -p /srv/SERVICE/{app,data,backups}
chown -R SERVICE:SERVICE /srv/SERVICE

cat > /srv/SERVICE/env <<'EOF'
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:PORT
ConnectionStrings__DefaultConnection=Data Source=/srv/SERVICE/data/app.db
EOF
chown SERVICE:SERVICE /srv/SERVICE/env && chmod 600 /srv/SERVICE/env
# nano /srv/SERVICE/env  -> add each SECRET key=value by hand, on the box only

cat > /etc/systemd/system/SERVICE.service <<'EOF'
[Unit]
Description=SERVICE
After=network.target
[Service]
Type=simple
User=SERVICE
WorkingDirectory=/srv/SERVICE/app
EnvironmentFile=/srv/SERVICE/env
ExecStart=/usr/bin/dotnet /srv/SERVICE/app/DLL
Restart=always
RestartSec=5
[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload && systemctl enable SERVICE

cat >> /etc/caddy/Caddyfile <<'EOF'

DOMAIN {
    reverse_proxy 127.0.0.1:PORT
}
EOF
caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile && systemctl reload caddy
```

**C. Deploy script in the new repo:** copy `deploy/deploy-vm.ps1` from this
repo and edit its one config line (`$Service`, `$Project`, `$Dll`, `$Port`,
and a `$HealthPath` your app answers with 200/302). Then, forever:

```powershell
pwsh deploy/deploy-vm.ps1
```

**D. Verify:** `https://DOMAIN/` responds; `systemctl status SERVICE` is
active; add the row to your port registry.

Notes that keep this pattern trouble-free:

- Env-var names map `:` in .NET config to `__` (double underscore):
  `Smtp:Password` → `Smtp__Password`.
- Secrets live only in `/srv/SERVICE/env` (chmod 600) — never in git, never
  in chat logs, never echoed to a terminal.
- SQLite databases and uploads always go under `/srv/SERVICE/data` (outside
  `app/`), so deploys can never touch them. Back that directory up.
- Rollback is always: stop service, swap `app.old` back in, start.

## 7.3 Teach it to your AI assistant

The parameters + recipe above are deliberately shaped like a checklist an AI
coding assistant (Claude Code, etc.) can drive: paste the recipe into your
assistant's skill/instructions folder as e.g. `setup-new-vm-site`, with
guardrails spelled out:

- Ask for any unknown parameter one question at a time; infer what's
  inferable (DLL from the csproj, DOMAIN from the service name) and confirm.
- Verify the port is free before claiming it; update the registry after.
- The assistant runs DNS-independent steps itself (provisioning, deploy
  script, verification) but **pauses for the human** on the two things that
  should stay human: the Cloudflare record and typing secret **values** into
  the env file. Key *names* may appear anywhere; values nowhere.

That split — assistant does the mechanical 90%, human holds the secrets —
is how the projects in this repo were actually built, and it's the single
biggest speed-up available once the pattern exists: the second app on your
VM costs 15 minutes; the fifth costs five.

---

That's the end of the path. You have frames on walls, a server you
understand, backups, health telemetry, and a repeatable recipe for whatever
you build next. Enjoy.
