#!/bin/bash
# Wi-Fi onboarding via comitup (https://davesteele.github.io/comitup/).
# If the Pi can't reach any known Wi-Fi network, it broadcasts its own hotspot
# ("slideshow-<nnn>"); the gift recipient connects from a phone and picks their
# home network from the captive-portal page. Credentials are stored as
# NetworkManager profiles, so the Imager-baked home/test network keeps working.
#
# Run on the Pi after install.sh:  bash install-comitup.sh
set -e

echo "==> Adding comitup apt source..."
wget -q https://davesteele.github.io/comitup/deb/davesteele-comitup-apt-source_1.3_all.deb -O /tmp/comitup-apt-source.deb
sudo dpkg -i --force-all /tmp/comitup-apt-source.deb
rm /tmp/comitup-apt-source.deb
sudo apt-get update

echo "==> Installing comitup..."
sudo apt-get install -y comitup

echo "==> Removing configs that would fight NetworkManager/comitup..."
sudo rm -f /etc/wpa_supplicant/wpa_supplicant.conf
# /etc/network/interfaces must not reference wifi interfaces (usually empty on
# Raspberry Pi OS Bookworm; delete only wlan lines if the file exists)
[ -f /etc/network/interfaces ] && sudo sed -i '/wlan/d' /etc/network/interfaces

echo "==> Masking services that conflict with comitup's hotspot DNS..."
# NOTE: do NOT mask wpa_supplicant — NetworkManager drives Wi-Fi through it
# on Bookworm/Trixie; masking it strands a Wi-Fi-only board like the 3A+.
sudo systemctl mask dnsmasq.service 2>/dev/null || true
sudo systemctl mask systemd-resolved.service 2>/dev/null || true
sudo systemctl enable NetworkManager.service

echo "==> Writing /etc/comitup.conf..."
sudo tee /etc/comitup.conf >/dev/null <<'EOF'
# Hotspot SSID the recipient will see when the Pi can't find a known network.
# <nnn> is a persistent per-device number, so multiple devices stay distinct.
ap_name: slideshow-<nnn>
EOF

echo "==> Done. Reboot to activate. To test: move out of Wi-Fi range (or"
echo "    'sudo nmcli connection delete <home-network>') and look for the"
echo "    'slideshow-<nnn>' hotspot on a phone."
