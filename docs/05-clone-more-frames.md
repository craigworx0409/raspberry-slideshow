# Step 5 — Clone the golden master

*One verified frame (Step 4) becomes N frames by copying its SD card.
~25 minutes per card, almost all unattended.*

## 5.1 Image the master card

1. Shut the master frame down cleanly:
   `ssh kiosk@frame1.local "sudo poweroff"` — then pull its SD card and put
   it in your PC's reader.
2. Raspberry Pi Imager can only *write* images, not read them. Use
   **Rufus** (`winget install Rufus.Rufus` — portable, no Start-menu entry,
   launch with `rufus` from a new terminal):
   - Device: the SD card (**only ever pick the ~32 GB entry** — triple-check
     you're not selecting a real hard drive)
   - Click the **save icon (💾)** next to the Device dropdown → save as
     `master-frame.vhd` (~30 GB, 10–20 min).
3. Put the master card back in the master Pi.

## 5.2 Write and personalize each clone

For each blank card: Rufus → Device = the card → **SELECT** →
`master-frame.vhd` → **START**.

Boot each clone **one at a time** (they all wake up believing they're
`frame1`, so don't run two un-renamed clones at once). Each clone:

1. It appears on `/admin/` as a **new device** automatically — device
   identity comes from each board's own Wi-Fi MAC address, so clones can't
   collide. (Its *name* still says frame1; fix that next.)
2. Personalize over SSH (your key works — it was cloned too). Since the
   master is also on the network, prefer the IP from your router's client
   list over `frame1.local`, or verify you're on the right board first:
   `cat /sys/class/net/wlan0/address` and match the MAC shown on `/admin/`.
   ```bash
   sudo raspi-config nonint do_hostname frame2
   sudo sed -i 's/^DEVICE_NAME=.*/DEVICE_NAME=frame2/' /etc/kiosk.conf
   sudo rm -f /var/lib/kiosk/url
   sudo reboot
   ```
3. It reboots as `frame2`, shows the default slideshow, and reports healthy
   on the admin page. Done — next card.

> **Why doesn't the browser break on a cloned card?** Chromium stamps its
> profile with a lock naming the machine that owns it; on a clone that lock
> says "another computer" and Chromium refuses to start — a black screen
> with no errors anywhere. `kiosk-session.sh` deletes the stale lock before
> every launch, which also self-heals the same condition after a power cut.

## 5.3 Hand a frame to its person

Per frame, on `/admin/`:

1. **Assign an owner** — their email + an initial password. That creates
   their login for the site's front page, where they see *their* frame,
   pick any collection for it, or point it at any URL, and change their
   password. They never see other people's frames or your admin pages.
2. Pick its starting collection (set its URL from admin, or let them).
3. **Click "Forget Wi-Fi"** as the very last step before boxing it up. The
   frame drops offline and will broadcast its `slideshow-<nn>` setup hotspot
   when it powers up at their house.

What the recipient does at home — worth printing on a card in the box:

> 1. Plug the frame into your TV (HDMI) and power.
> 2. On your phone, join the Wi-Fi network called **slideshow-XX**.
> 3. A setup page appears (if not, browse to `10.41.0.1`). Choose your home
>    Wi-Fi, type its password, done.
> 4. The frame connects and starts your slideshow. If your Wi-Fi ever
>    changes, the slideshow-XX network reappears — repeat from step 2.

Next: [Step 6 — operating the fleet](06-operate.md).
