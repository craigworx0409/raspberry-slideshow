# Step 6 — Operating the fleet

*Day-2 life: what to glance at, what the numbers mean, and what to do when a
frame misbehaves — especially one at someone else's house where you cannot
SSH.*

## 6.1 The two dashboards

- **`/admin/`** — every device: online badge, what it's showing, owner, a
  one-line health readout, command buttons, and an expandable **📶 signal
  history** graph (1h/6h/24h/7d).
- **`/library/`** — images and collections. Adding photos to a collection
  updates every frame showing it within ~5 minutes (the player refreshes its
  list itself); no touching devices.

## 6.2 Reading the health line

Example: `free 158M · swap 96M · load 0.64 · 47.8°C · wifi -45dBm · browser 9p · disk 10%`

| Metric | Fine | Worry | It means |
|---|---|---|---|
| free (MemAvailable) | >100M | <60M | RAM headroom |
| swap | 60–150M steady | climbing past 200M, red at 250M+ | *the stall predictor* — see below |
| load (1-min avg) | fluctuating 0.1–1.5 | sustained >3, or **pinned at exactly 1.00** | runnable processes; 4.0 = all 4 cores busy. A flat 1.00 + hot browser = one thread spinning |
| temp | <70°C | >80°C (throttles) | give it airflow |
| wifi | −30 to −65 dBm | below −70 | signal at the frame; weaker = closer to router |
| browser | 8–12p | 0–2p | Chromium process count; ~1 means it's crashing or blocked |
| UNDER-VOLTAGE | absent | any mention | **bad power supply** — the #1 SD-card killer. Replace the charger, not the card |

Think blood pressure: no single number diagnoses; the combination does. The
classic failure is **memory thrash**: swap ratchets upward over hours, then
load pins at 1.00 and the slideshow freezes on one image. Defenses already
built in: images capped at 1080p, browser cache in RAM, and the agent
restarts the browser every `RELOAD_EVERY_HOURS` (default 6, in
`/etc/kiosk.conf`) — each reload shows the splash for a few seconds and
resets the meter.

The 📶 graph doubles as a **Wi-Fi site survey**: samples land every 45 s, so
plug a frame in anywhere and two minutes later you know that spot's real
signal. Handy for "move it one room closer" support calls.

## 6.3 The command buttons

| Button | Does | Notes |
|---|---|---|
| Reload | restarts the browser | fixes 90% of weirdness; brief splash |
| Reboot | full OS reboot | the other 9% |
| Power off | `sync` + clean shutdown | **no remote power-on** — someone must replug it. Use before moving/boxing a frame |
| Forget Wi-Fi | wipes saved networks → setup hotspot | the "hand it to a new house" button |
| Forget device | removes it from the hub | it re-registers if it ever heartbeats again |

Commands are picked up on the next heartbeat — up to ~45 s of latency is
normal.

## 6.4 Troubleshooting table

| Symptom | Likely cause | Fix |
|---|---|---|
| Offline on admin, recently unplugged | it's unplugged | replug; it resumes its last URL by itself |
| Offline, recipient's Wi-Fi changed | can't join network | have them join `slideshow-<nn>` from a phone and re-onboard |
| Black screen, device online, browser ~1p | Chromium crash-looping (stale singleton lock — power cut) | Reload button; the session script also clears it on every launch, so a Reboot always cures it |
| Black screen, browser 9p healthy | TV input/HDMI handshake | TV input source; replug HDMI; worst case Reboot |
| Frozen on one image, swap 250M+ | memory thrash | Reload now; lower `delay`/image sizes; shrink `RELOAD_EVERY_HOURS` |
| UNDER-VOLTAGE in health | weak charger/cable | replace with the official 12.5 W supply |
| wifi < −70 dBm and dropouts | too far from router | move frame or router; the 📶 graph proves the improvement |
| Frame shows an error page | its URL is broken/moved | set a fresh URL from `/admin/` — the hub always wins on next heartbeat |
| Everything dead after a storm | corrupted SD card (rare with the hardening) | re-flash from `master-frame.vhd`, rename, reassign — 25 min total. This is why you keep the image |

## 6.5 When you CAN ssh (frame on your LAN)

```bash
ssh kiosk@frame1.local
systemctl status kiosk-agent          # agent running?
sudo journalctl -u kiosk-agent -n 20  # heartbeats, navigations, commands
pgrep -c chromium                     # ~9 = healthy browser
vcgencmd get_throttled                # 0x0 = clean power history
free -h                               # memory/swap right now
```

## 6.6 Server-side care (monthly, 5 minutes)

```bash
ssh root@YOUR-VM-IP
apt update && apt upgrade -y     # reboot if it mentions a kernel upgrade
df -h /                          # disk headroom
ls -la /srv/slideshow/backups/   # nightly backups landing?
journalctl -u slideshow -n 30    # anything alarming?
```

Deploying an app update is always just `pwsh deploy/deploy-vm.ps1`;
config-only changes are `nano /srv/slideshow/env` +
`systemctl restart slideshow`.

## 6.7 Updating the devices

The frames never update themselves (by design — apt timers are masked).
To ship a changed device script to a frame on your LAN:

```powershell
scp device/usr/local/bin/kiosk-session.sh kiosk@frame1.local:/tmp/
ssh kiosk@frame1.local "sudo install -m 755 /tmp/kiosk-session.sh /usr/local/bin/ && rm /tmp/kiosk-session.sh"
```

then Reload/Reboot from the admin page. For remote frames, queue a script
update as a feature idea — or accept that the device software is intentionally
frozen and changes ship by mailing a re-flashed card.

Final chapter: [Step 7 — reuse the VM for your next app](07-new-app-runbook.md).
