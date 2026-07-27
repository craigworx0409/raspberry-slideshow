# Step 2 — Rent a Linux VM and learn to drive it

*Audience check: you've written software on Windows for years, you're fine in
PowerShell, but you've never been responsible for a Linux box. This chapter
gets you from zero to "I own a server on the internet and I know what I'm
doing on it." Budget an evening.*

## 2.1 Rent the VM (Hetzner)

[Hetzner Cloud](https://www.hetzner.com/cloud) is the value pick: ~$5/month
for 2 vCPU / 4 GB / 40 GB NVMe and 20 TB of traffic. (DigitalOcean and AWS
Lightsail are fine alternatives at ~2× the price with gentler dashboards —
everything below works the same on any Ubuntu VPS.)

1. Create an account at [console.hetzner.com](https://console.hetzner.com)
   (expect an identity check on signup — normal for hosting providers).
2. **Before creating the server**, make an SSH key on your Windows machine.
   PowerShell:
   ```powershell
   ssh-keygen -t ed25519
   ```
   Accept the default path (`C:\Users\you\.ssh\id_ed25519`). A passphrase is
   optional. This creates a key **pair**: `id_ed25519` (private — never
   leaves your machine, never gets pasted anywhere) and `id_ed25519.pub`
   (public — freely shareable).
3. Hetzner console → your project → **Security → SSH keys → Add SSH key** →
   paste the contents of `id_ed25519.pub` (open it in Notepad; it's one line
   starting `ssh-ed25519`).
4. **Add server**:
   - Location: nearest to you (or your frames' recipients).
   - Image: **Ubuntu 24.04** (LTS = supported for years; the version all
     tutorials assume).
   - Type: shared vCPU, **x86** — the cheapest (CX22/CPX11 class) is plenty.
   - SSH key: select the key you just added. **Do not use password auth.**
   - Everything else: defaults. Create.
5. Note the server's public IP address. Everywhere this guide says
   `YOUR-VM-IP`, that's it.

## 2.2 First login

From PowerShell:

```powershell
ssh root@YOUR-VM-IP
```

First time, it asks to trust the host's fingerprint — answer `yes`. And
you're in: that `#` prompt is a root shell on your own internet-connected
Linux machine.

**Windows-to-Linux translation table** — the ten commands that cover 90% of
server life:

| You want | Windows habit | Linux |
|---|---|---|
| List files | `dir` | `ls -la` |
| Change directory | `cd` | `cd` (same!) |
| Show a file | `type file` | `cat file` |
| Edit a file | notepad | `nano file` (Ctrl+O save, Ctrl+X exit) |
| Copy / move / delete | `copy`/`move`/`del` | `cp` / `mv` / `rm` |
| Make a directory | `mkdir` | `mkdir -p path` (`-p` = create parents) |
| Find text in files | findstr | `grep -r "text" /path` |
| See running services | services.msc | `systemctl status NAME` |
| See logs | Event Viewer | `journalctl -u NAME -n 50` |
| Free disk / memory | Task Manager | `df -h` / `free -h` |

Three concepts that don't map from Windows:

- **Everything is a file** under one tree rooted at `/`. No drive letters.
  Your app will live in `/srv/slideshow/`, config in `/etc/`, logs in the
  journal.
- **`sudo`** = "run as administrator" for one command. As root you don't
  need it; later, per-app users do.
- **systemd** is Services + Task Scheduler + Event Log in one. `systemctl`
  manages services; `journalctl` reads their logs. You'll use both weekly.

## 2.3 Baseline setup (run once)

Paste these as root, one block at a time.

Update everything (Linux equivalent of Windows Update; takes a few minutes):

```bash
apt update && apt upgrade -y
```

Firewall — allow SSH and web traffic, block everything else:

```bash
ufw allow OpenSSH
ufw allow 80/tcp
ufw allow 443/tcp
ufw enable
```

(Answer `y`. `ufw status` shows the result. Nothing else on this box will be
reachable from the internet — apps bind to localhost and are only exposed
through the web server.)

Automatic security patches:

```bash
apt install -y unattended-upgrades
```

A reboot to pick up any kernel update, and to prove to yourself the box comes
back on its own:

```bash
reboot
```

Wait 30 seconds, `ssh root@YOUR-VM-IP` again. It's back, the firewall is on,
and everything you install from here restarts itself. That resilience is
systemd's job, and it's why this stack needs essentially zero babysitting.

## 2.4 Install the two platform pieces

**The .NET runtime** (your apps deploy framework-dependent, so builds are
small and the runtime updates via apt):

```bash
add-apt-repository ppa:dotnet/backports -y
apt update
apt install -y aspnetcore-runtime-10.0
```

Check: `dotnet --list-runtimes` shows `Microsoft.AspNetCore.App 10.x`.

**Caddy** — the web server. If you know IIS: Caddy is IIS bindings + reverse
proxy + certificate management in one 40 MB binary, configured in a text
file, with HTTPS certificates obtained and renewed **automatically** from
Let's Encrypt. It's the single biggest quality-of-life difference from
Windows hosting.

```bash
apt install -y debian-keyring debian-archive-keyring apt-transport-https curl
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | tee /etc/apt/sources.list.d/caddy-stable.list
apt update
apt install -y caddy
```

Check: `systemctl status caddy` says `active (running)`, and browsing to
`http://YOUR-VM-IP` shows Caddy's placeholder page.

## 2.5 DNS at Cloudflare

You need a domain (any registrar, ~$10/year) with its nameservers pointed at
[Cloudflare](https://dash.cloudflare.com) (free plan). Then, for each site
this VM will host:

1. Cloudflare → your zone → **DNS → Add record**.
2. Type **A**, name = the subdomain (e.g. `slideshow`), IPv4 = `YOUR-VM-IP`.
3. **Proxy status: DNS only (gray cloud).** This matters: with the orange
   cloud on, Let's Encrypt's domain check can't reach Caddy directly and
   certificate issuance fails. Gray cloud = Cloudflare is just DNS.
4. TTL: Auto.

Propagation is minutes. `nslookup slideshow.your-domain.com` from PowerShell
should return your VM's IP.

## 2.6 What you now have

A patched, firewalled Ubuntu box that survives reboots, runs .NET, and can
serve HTTPS for any subdomain you point at it — the platform for this
project's server ([Step 3](03-server-deploy.md)) and for every app idea you
have after it ([Step 7](07-new-app-runbook.md)).

**Golden rule going forward:** the only irreplaceable thing on this VM will
be its data directories (`/srv/*/data`, `/srv/*/media`). Apps redeploy from
git in minutes; the VM itself can be rebuilt from this page in an hour. Back
up data, not machines.
