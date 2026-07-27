#!/bin/bash
# Run on the Pi from inside the device/ directory (after copying it over):
#   scp -r device kiosk@slideshow.local:~/ && ssh kiosk@slideshow.local "cd device && bash install.sh"
set -e

sudo install -m 755 usr/local/bin/kiosk.sh /usr/local/bin/kiosk.sh
sudo install -m 755 usr/local/bin/kiosk-session.sh /usr/local/bin/kiosk-session.sh
sudo install -m 755 usr/local/bin/kiosk-agent.py /usr/local/bin/kiosk-agent.py
sudo install -m 644 etc/systemd/system/kiosk-agent.service /etc/systemd/system/kiosk-agent.service

# Don't clobber an existing config (it holds this device's name/keys)
if [ ! -f /etc/kiosk.conf ]; then
    sudo install -m 600 etc/kiosk.conf /etc/kiosk.conf
    echo ">>> Edit /etc/kiosk.conf with your hub URL, device key, and device name!"
fi

sudo mkdir -p /var/lib/kiosk
sudo chmod 755 /var/lib/kiosk

sudo systemctl daemon-reload
sudo systemctl enable --now kiosk-agent

echo "Installed. Check the agent with: sudo journalctl -u kiosk-agent -f"
