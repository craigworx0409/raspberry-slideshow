#!/bin/bash
# Runs INSIDE cage (WAYLAND_DISPLAY is set) and loops the browser, so
# navigation/reload restarts (the agent pkills chromium) never tear down the
# compositor — the screen never drops to console text. Each launch first
# paints a friendly splash page, which hops to the real URL once ready.

CONF=/etc/kiosk.conf
# Persistent (survives reboot) — the agent updates it on navigate
URL_FILE=/var/lib/kiosk/url
SPLASH=/tmp/kiosk-splash.html
DEFAULT_URL=$(sed -n 's/^DEFAULT_URL=//p' "$CONF")
HUB_URL=$(sed -n 's/^HUB_URL=//p' "$CONF")
# Trixie ships the binary as "chromium"; older releases as "chromium-browser"
BROWSER=$(command -v chromium-browser || command -v chromium)

# Give Wi-Fi a moment at boot so the first page load doesn't race the network
for _ in $(seq 1 10); do
    curl -s --max-time 2 "$HUB_URL/health" >/dev/null 2>&1 && break
    sleep 2
done

while true; do
    URL=$(cat "$URL_FILE" 2>/dev/null)
    [ -n "$URL" ] || URL="$DEFAULT_URL"

    # Chromium stamps the owning hostname into these; a cloned card (or an
    # unclean shutdown) leaves a stale lock that makes it refuse to start
    # ("profile appears to be in use ... on another computer").
    rm -f ~/.config/chromium/Singleton{Lock,Cookie,Socket}

    # Interstitial shown while the real page loads (& must be entity-escaped
    # inside the meta refresh attribute)
    cat > "$SPLASH" <<EOF
<!doctype html><meta charset="utf-8">
<meta http-equiv="refresh" content="1;url=${URL//&/&amp;}">
<style>
  body{margin:0;height:100vh;display:grid;place-items:center;
       background:#101418;color:#dfe7ef;font-family:system-ui,sans-serif}
  .w{text-align:center}.tv{font-size:72px}
  p{font-size:30px;opacity:.85;animation:pulse 1.6s ease-in-out infinite}
  @keyframes pulse{50%{opacity:.3}}
</style>
<div class="w"><div class="tv">&#128250;</div><p>Loading your pictures&hellip;</p></div>
EOF

    # Park the cursor bottom-right once the page has had time to load
    ( sleep 12; command -v wlrctl >/dev/null 2>&1 && wlrctl pointer move 10000 10000 ) &

    # --no-memcheck must come before the URL: the Pi OS wrapper stops
    # parsing its own options at the first non-flag argument
    # Browser cache lives in tmpfs (RAM): it is the bulk of the device's disk
    # churn, and nothing in it is worth surviving a reboot — so a yanked power
    # cord has far less write traffic in flight to corrupt.
    "$BROWSER" \
        --no-memcheck \
        --disk-cache-dir=/tmp/chromium-cache \
        --disk-cache-size=33554432 \
        --ozone-platform=wayland \
        --noerrdialogs --disable-infobars --incognito \
        --autoplay-policy=no-user-gesture-required \
        --check-for-update-interval=31536000 \
        --kiosk "file://$SPLASH"
    # chromium exited (crash, or agent killed it to navigate/reload) — loop
    sleep 1
done
