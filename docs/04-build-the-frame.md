# Step 4 — Build the first frame

*Prerequisites: the hardware (Step 1), the server live (Step 3). This takes
about 45 minutes, most of it waiting on downloads. You build ONE golden
frame here; Step 5 clones it.*

## 4.1 Flash the SD card

1. Install [Raspberry Pi Imager](https://www.raspberrypi.com/software/) —
   download from the site, or `winget install
   RaspberryPiFoundation.RaspberryPiImager` (winget installs it without a
   Start-menu entry; launch it from a **new** terminal with `rpi-imager`,
   then pin it to your taskbar).
2. Choose Device: **Raspberry Pi 3**. Choose OS: **Raspberry Pi OS (other) →
   Raspberry Pi OS Lite (32-bit)** — "Lite" means no desktop; the kiosk
   brings its own display stack. Choose Storage: your SD card.
3. In the customization step, set:
   - hostname: `frame1`
   - username `kiosk` + a password you'll remember
   - your Wi-Fi SSID/password + country
   - **enable SSH** (password authentication)
4. Write, verify, eject. Card into the Pi, HDMI to a TV, power last. First
   boot takes ~2 minutes and ends at a login prompt on screen.

## 4.2 Give yourself passwordless SSH

From PowerShell (one-time per fresh flash; enter the kiosk password when
asked):

```powershell
Get-Content "$env:USERPROFILE\.ssh\id_ed25519.pub" | ssh kiosk@frame1.local "mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys"
```

(If `frame1.local` doesn't resolve, find the Pi's IP in your router's client
list and use that.) Test: `ssh kiosk@frame1.local` — no password prompt.

## 4.3 Install the display stack

```powershell
ssh kiosk@frame1.local
```

then on the Pi:

```bash
sudo apt update
sudo apt install -y --no-install-recommends cage wlrctl
sudo apt install -y --no-install-recommends chromium-browser || sudo apt install -y --no-install-recommends chromium
```

> **Why `cage` (Wayland) and not the traditional X11 kiosk recipe?** On
> Raspberry Pi OS Trixie with the Pi 3's V3D 2.1 GPU, Xorg renders perfectly
> *internally* while scanning out **pure black** to the HDMI port — a
> maddening failure where screenshots look fine and the screen shows
> nothing. The vc4 driver mishandles tiled GPU buffers on this old hardware.
> `cage` is a single-app Wayland compositor, and the kiosk script runs it
> with `WLR_RENDERER=pixman` (CPU rendering into plain linear buffers),
> which displays correctly and is ample for a slideshow.

## 4.4 Install the kiosk software

From PowerShell, in your repo clone:

```powershell
scp -r device kiosk@frame1.local:~/
ssh kiosk@frame1.local "cd device && bash install.sh"
```

Then configure this device — on the Pi, `sudo nano /etc/kiosk.conf`:

- `HUB_URL=https://slideshow.your-domain.com`
- `DEVICE_KEY=` — the same value you put in the server's env file
- `DEVICE_NAME=frame1`
- `DEFAULT_URL=https://slideshow.your-domain.com/slideshow/` — what it shows
  until told otherwise

Console autologin + start the kiosk on login:

```bash
sudo raspi-config nonint do_boot_behaviour B2
cat > ~/.bash_profile <<'EOF'
if [ -z "$WAYLAND_DISPLAY" ] && [ -z "$DISPLAY" ] && [ "$(tty)" = "/dev/tty1" ]; then
  exec /usr/local/bin/kiosk.sh
fi
EOF
sudo reboot
```

Two minutes later the TV is showing your slideshow, and the device has
appeared on `https://slideshow.your-domain.com/admin/` with live health
stats. Everything from here on is remote controlled.

**How it works:** `kiosk.sh` keeps the compositor alive; `kiosk-session.sh`
loops Chromium inside it (so reloads show a friendly splash, never console
text) and clears Chromium's singleton lock each launch (a stale lock after
cloning or a power cut otherwise blocks startup — invisibly). The agent
(`kiosk-agent.py`, a systemd service) heartbeats the hub every 45 s: it
reports vitals, receives the URL it should display, and executes queued
commands (reload / reboot / poweroff / forget-wifi). Navigation = write the
URL to a file + restart the browser. The chosen URL persists across reboots
in `/var/lib/kiosk/url`.

## 4.5 Wi-Fi onboarding for other people's houses (comitup)

Right now the frame only knows *your* Wi-Fi. `comitup` adds a captive-portal
fallback: when no known network is reachable, the Pi broadcasts its own
hotspot; whoever owns the frame joins it from a phone and picks their home
network from a list. That's the entire "setup" a gift recipient does.

```powershell
ssh kiosk@frame1.local "cd device && bash install-comitup.sh && sudo reboot"
```

> The installer deliberately does **not** mask `wpa_supplicant`, despite
> older comitup guides saying to — on current Raspberry Pi OS, NetworkManager
> drives Wi-Fi *through* wpa_supplicant, and masking it permanently strands
> a Wi-Fi-only board.

**Test it** (worth doing once): `ssh kiosk@frame1.local`, then
`sudo nmcli connection delete "<your-ssid>"`. The frame drops offline; within
~2 minutes a `slideshow-<nn>` network appears on your phone. Join it, pick
your Wi-Fi on the portal page (browse to `10.41.0.1` if it doesn't pop up),
and watch the frame come back online on the admin page.

## 4.6 Harden against power-cuts

People unplug photo frames. Make that as boring as possible:

```powershell
ssh kiosk@frame1.local "cd device && bash harden.sh"
```

This puts logs in RAM, stops zram spilling to the card, masks the background
apt/man-db/logrotate timers (they write to disk *and* could install package
updates behind your back — this appliance should never change itself), sets
batched filesystem commits, verifies boot-time auto-fsck, and installs a
nightly self-healing reboot. The kiosk session already keeps Chromium's
cache in RAM. Optionally add a quiet boot (no scrolling text):

```bash
sudo sed -i '1s/$/ quiet loglevel=3 vt.global_cursor_default=0/' /boot/firmware/cmdline.txt
```

## 4.7 Sign-off checklist

- [ ] TV shows the slideshow after an unattended reboot
- [ ] Device on `/admin/` with green health line
- [ ] Changing its URL from `/admin/` switches the TV within a minute
- [ ] Reload / Reboot buttons work
- [ ] comitup hotspot test passed
- [ ] Pull the plug mid-slideshow, plug back in → recovers by itself

All green? This card is your **golden master**.
[Step 5 — clone it](05-clone-more-frames.md).
