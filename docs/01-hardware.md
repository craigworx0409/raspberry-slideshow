# Step 1 — Buy the hardware

Per frame, about $50:

| Part | ~Price | Notes |
|---|---|---|
| **Raspberry Pi 3 Model A+** | $25 | The sweet spot: quad-core 1.4 GHz, dual-band Wi-Fi, full-size HDMI, $25. Buy from an [official reseller](https://www.raspberrypi.com/products/raspberry-pi-3-model-a-plus/) (Adafruit, PiShop, CanaKit in the US). Skip the pre-soldered-header version — this project uses no GPIO. |
| Official Raspberry Pi 12.5 W Micro-USB power supply | $9 | Don't substitute a random phone charger: under-voltage is the #1 cause of SD-card corruption, and the health telemetry in this project will name and shame a weak supply. |
| 32 GB microSD card (SanDisk Ultra or similar, Class 10/UHS-I) | $9 | 16 GB works; 32 GB is the price sweet spot. Buy one per frame **plus one spare** — re-flashing is the repair procedure for everything. |
| HDMI cable | $5 | Full-size HDMI on the 3A+ — any cable you have lying around works. |
| *(optional)* Case | $5–15 | The board happily velcros to the back of a TV bare. The Flirc aluminum case doubles as a heatsink if you want one. |

You also need, one-time (not per frame):

- A **USB SD-card reader** for your PC (many laptops have a slot already).
- A TV or monitor with HDMI for testing.

## Why this board?

Any Pi with HDMI + Wi-Fi can work, but the constraints in this repo are tuned
for the 3A+'s 512 MB of RAM (the images are pre-sized to 1080p, the browser
runs with CPU rendering, memory is actively managed). Notes if you substitute:

- **Pi Zero 2 W** ($15) — same software stack, mini-HDMI, frequently
  out of stock and often limited to one per order. Works identically.
- **Pi 4 / Pi 5** — more RAM makes everything easier, and the display bug
  workarounds in this repo (see step 4) may be unnecessary — but at $45–80+
  they defeat the price point, and they want more power and cooling.
- **Pi 1/2 and original Zero** — no. Single-core/ARMv6 boards cannot run a
  modern browser acceptably.

## While you wait for delivery

Do [Step 2 — the Linux VM](02-linux-vm.md). It needs nothing from this box
of parts, and it's the longest step if Linux is new to you.
