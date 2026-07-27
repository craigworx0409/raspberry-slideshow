#!/bin/bash
# Minimise SD-card writes so an unplanned power cut has as little in flight as
# possible. Recipients WILL yank the cord; this is the damage-limitation pass.
# Idempotent — safe to re-run. Run on the Pi:  bash harden.sh
set -e

echo "==> Logs to RAM (journald volatile)..."
sudo sed -i 's/^#\?Storage=.*/Storage=volatile/' /etc/systemd/journald.conf
sudo systemctl restart systemd-journald

echo "==> zram swap stays, but no spilling cold pages to the card..."
sudo systemctl mask --now rpi-zram-writeback.timer rpi-zram-writeback.service 2>/dev/null || true

echo "==> Stop background maintenance that writes (and could install updates)..."
# apt-daily-upgrade in particular defeats the whole point of a frozen
# appliance image: no surprise package changes behind our back.
sudo systemctl mask --now apt-daily.timer apt-daily-upgrade.timer \
    man-db.timer logrotate.timer 2>/dev/null || true

echo "==> Batch ext4 journal commits (60s instead of 5s)..."
if ! grep -q "commit=60" /etc/fstab; then
    sudo sed -i 's|\(\s/\s\+ext4\s\+defaults,noatime\)|\1,commit=60|' /etc/fstab
fi

echo "==> Nightly self-heal reboot at 4am..."
echo "0 4 * * * /sbin/reboot" | sudo crontab -

echo "==> Verify auto-repair on boot (must be fsck.repair=yes)..."
if grep -q "fsck.repair=yes" /boot/firmware/cmdline.txt; then
    echo "    ok — a dirty filesystem self-repairs instead of hanging at a prompt"
else
    sudo sed -i "1s|\$| fsck.repair=yes|" /boot/firmware/cmdline.txt
    echo "    added"
fi

echo
echo "Done. Verify after reboot:"
echo "  findmnt -no OPTIONS /            # expect noatime,commit=60"
echo "  vcgencmd get_throttled           # 0x0 ideal; bit 0/16 set = bad PSU"
