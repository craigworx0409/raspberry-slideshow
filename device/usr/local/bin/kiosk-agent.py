#!/usr/bin/env python3
"""Kiosk agent: reports this device to the hub and applies its answers.

Every POLL_SECS it POSTs a heartbeat (device id, name, current URL, uptime)
and the hub replies with the desired URL plus any queued one-shot commands
(reload, reboot). Navigation = write /tmp/kiosk-url + kill Chromium; the
kiosk.sh respawn loop relaunches with the new URL.

Stdlib only — nothing to pip-install on the Pi. Runs as root via systemd
so it can kill Chromium and reboot.
"""
import json
import os
import socket
import subprocess
import time
import urllib.request
import uuid

CONF = "/etc/kiosk.conf"
# Persistent across reboots (we run with a writable root — no OverlayFS —
# so the frame comes back up on its last URL without a correction cycle)
URL_FILE = "/var/lib/kiosk/url"


def load_conf():
    cfg = {}
    with open(CONF) as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                k, v = line.split("=", 1)
                cfg[k.strip()] = v.strip()
    return cfg


def device_id():
    """Stable id derived from the wlan0 MAC address."""
    try:
        with open("/sys/class/net/wlan0/address") as f:
            return "pi-" + f.read().strip().replace(":", "")[-6:]
    except OSError:
        return "pi-" + hex(uuid.getnode())[-6:]


def uptime_secs():
    with open("/proc/uptime") as f:
        return int(float(f.read().split()[0]))


def health():
    """Cheap vitals for the hub — this is the only view we get once a device
    lives on someone else's network and SSH is out of reach."""
    h = {}
    try:
        mem = {}
        with open("/proc/meminfo") as f:
            for line in f:
                k, _, v = line.partition(":")
                mem[k] = int(v.split()[0])  # kB
        h["memAvailMb"] = mem.get("MemAvailable", 0) // 1024
        h["swapUsedMb"] = (mem.get("SwapTotal", 0) - mem.get("SwapFree", 0)) // 1024
    except OSError:
        pass
    try:
        with open("/proc/loadavg") as f:
            h["load1"] = float(f.read().split()[0])
    except OSError:
        pass
    try:
        with open("/sys/class/thermal/thermal_zone0/temp") as f:
            h["tempC"] = round(int(f.read().strip()) / 1000.0, 1)
    except OSError:
        pass
    try:
        # Bit 0 = under-voltage now, bit 16 = under-voltage since boot. A flaky
        # power supply is the classic cause of SD-card corruption, and it is
        # invisible from the hub without this.
        out = subprocess.run(["vcgencmd", "get_throttled"], capture_output=True,
                             text=True, timeout=5).stdout.strip()
        h["throttled"] = out.split("=")[-1]
    except (OSError, subprocess.SubprocessError, IndexError):
        pass
    try:
        st = os.statvfs("/")
        h["diskUsedPct"] = round(100.0 * (st.f_blocks - st.f_bfree) / st.f_blocks, 1)
    except OSError:
        pass
    try:
        # Signal strength at the recipient's house — the other thing most
        # likely to explain "it stopped working".
        with open("/proc/net/wireless") as f:
            for line in f:
                if line.strip().startswith("wlan0:"):
                    h["wifiDbm"] = int(float(line.split()[3]))
    except (OSError, IndexError, ValueError):
        pass
    try:
        h["browserProcs"] = int(subprocess.run(["pgrep", "-c", "chromium"],
                                capture_output=True, text=True).stdout.strip() or 0)
    except (OSError, ValueError):
        pass
    return h


def current_url(default):
    try:
        with open(URL_FILE) as f:
            return f.read().strip() or default
    except OSError:
        return default


def set_url(url):
    with open(URL_FILE, "w") as f:
        f.write(url)


def restart_browser():
    subprocess.run(["pkill", "-f", "chromium"], check=False)


def self_wipe():
    """Remote "return it to me for re-imaging" wipe. Erases everything a
    borrowed device knows — cached photos, browser sessions, saved Wi-Fi,
    and this device's hub credentials — then powers off. Ordering matters:
    the config is neutered FIRST so the kiosk respawn loop can only come
    back to about:blank if it races us. Best-effort by design: physical
    possession of the SD card still wins; this handles the "juuust in case"
    tier, not forensics."""
    print("WIPE command received — erasing local data and powering off", flush=True)
    try:
        with open(CONF, "w") as f:
            f.write("# wiped by hub command\nHUB_URL=\nDEVICE_KEY=\nDEFAULT_URL=about:blank\n")
    except OSError as e:
        print(f"wipe: conf rewrite failed: {e}", flush=True)
    try:
        os.remove(URL_FILE)
    except OSError:
        pass
    subprocess.run(["pkill", "-f", "chromium"], check=False)
    # v2 persistent profile + v1 default profile + caches: photos, cookies, SW cache
    subprocess.run(["rm", "-rf", "/var/lib/kiosk/profile",
                    "/home/kiosk/.config/chromium", "/home/kiosk/.cache"], check=False)
    forget_wifi()
    subprocess.run(["sync"], check=False)
    # TRIM the freed flash blocks — deleted data becomes unrecoverable on
    # most cards rather than merely unlinked
    subprocess.run(["fstrim", "-a"], check=False)
    subprocess.run(["systemctl", "poweroff"], check=False)


def forget_wifi():
    """Delete every saved Wi-Fi profile except comitup's own, so the captive
    portal hotspot takes over — used when handing the device to a recipient."""
    out = subprocess.run(["nmcli", "-t", "-f", "NAME,TYPE", "connection", "show"],
                         capture_output=True, text=True).stdout
    for line in out.splitlines():
        cname, _, kind = line.partition(":")
        if "wireless" in kind and not (cname.startswith("comitup-") or cname.startswith("slideshow-")):
            print(f"forgetting wifi: {cname}", flush=True)
            subprocess.run(["nmcli", "connection", "delete", cname], check=False)


def main():
    cfg = load_conf()
    hub = cfg["HUB_URL"].rstrip("/")
    key = cfg["DEVICE_KEY"]
    default = cfg.get("DEFAULT_URL", "about:blank")
    name = cfg.get("DEVICE_NAME", socket.gethostname())
    poll = int(cfg.get("POLL_SECS", "45"))
    # Restart the browser every N hours to shed accumulated memory (0 = off).
    # On a 512MB board a long-running slideshow can thrash itself into swap.
    reload_every = float(cfg.get("RELOAD_EVERY_HOURS", "0"))
    last_reload = time.monotonic()
    did = device_id()
    print(f"kiosk-agent starting as {did} ({name}), hub {hub}", flush=True)

    while True:
        try:
            cur = current_url(default)
            body = json.dumps({
                "deviceId": did,
                "name": name,
                "currentUrl": cur,
                "uptimeSecs": uptime_secs(),
                "health": health(),
            }).encode()
            req = urllib.request.Request(
                hub + "/api/heartbeat",
                data=body,
                headers={"Content-Type": "application/json", "X-Api-Key": key},
            )
            with urllib.request.urlopen(req, timeout=15) as resp:
                data = json.load(resp)

            desired = data.get("desiredUrl")
            if desired and desired != cur:
                print(f"navigate: {cur} -> {desired}", flush=True)
                set_url(desired)
                restart_browser()

            for cmd in data.get("commands", []):
                ctype = cmd.get("type")
                print(f"command: {ctype}", flush=True)
                if ctype == "reload":
                    restart_browser()
                elif ctype == "reboot":
                    subprocess.run(["systemctl", "reboot"], check=False)
                elif ctype == "poweroff":
                    # Flush pending writes first: the point of a graceful
                    # shutdown is that the SD card is quiescent when the
                    # power finally goes.
                    subprocess.run(["sync"], check=False)
                    subprocess.run(["systemctl", "poweroff"], check=False)
                elif ctype == "forget-wifi":
                    forget_wifi()
                elif ctype == "wipe":
                    self_wipe()
        except Exception as e:  # noqa: BLE001 — keep the appliance alive no matter what
            print(f"heartbeat failed: {e}", flush=True)

        if reload_every > 0 and time.monotonic() - last_reload > reload_every * 3600:
            print("periodic reload (RELOAD_EVERY_HOURS)", flush=True)
            restart_browser()
            last_reload = time.monotonic()
        time.sleep(poll)


if __name__ == "__main__":
    main()
