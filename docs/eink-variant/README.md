# E-ink frame variant — parked ("maybe later")

Status: **researched, deliberately not pursued yet.** The v2 wall frame
(Pi 5 + 16″ 1080p LCD, video-capable) is its own goal. This folder preserves
the e-ink investigation so a future variant starts warm.

Decision already made: if this happens, it is **Spectra 6 only** — E Ink's
true-color-ink generation with saturated, print-like output. Kaleido-class
color-filter panels (muted colors) and grayscale HDMI e-ink monitors
(Dasung/Boox, $700+) are rejected for photo display.

## What e-ink changes about the product

| | LCD wall frame (v2) | Spectra 6 frame |
|---|---|---|
| Motion | crossfades, video | **none** — a refresh is a visible ~15–30 s particle "page-turn" |
| Cadence | new image every ~12 s | new image every **15 min – hours** (a gallery rotating a print) |
| Look | photographic, backlit | dithered 6-color palette; reads as a **print**, stylized and beautiful |
| Light/glare | emissive; matte helps | zero glare, zero light at night — bedroom/hallway perfect |
| Display power | 3.5–9 W continuous | ~0 W static; power only during refresh |
| Cost per inch | cheap | expensive (see below) |

Refresh reality check (2026 panels): Spectra 6 full refresh ≈ 12–30 s;
Pimoroni notes 7.3″ panels manufactured from April 2026 use an AC waveform
with a ~28 s core refresh. There is no partial-refresh trickery worth using
for photos, and no animation of any kind.

## Candidate hardware (prices seen Aug 2026)

| Panel | Price | Notes |
|---|---|---|
| [Waveshare 13.3″ Spectra 6 HAT+](https://www.amazon.com/Waveshare-13-3inch-Compatible-Raspberry-HAT/dp/B0DPBW2R25) | ~$300–330 | 1600×1200 @ 200 ppi, HAT+ standard, [wiki/manual](https://www.waveshare.com/wiki/13.3inch_e-Paper_HAT+_(E)_Manual). Same panel family as $400–2000 "InkPoster" art frames. |
| [Pimoroni Inky Impression 7.3″](https://shop.pimoroni.com/en-us/products/inky-impression) | ~$90 | 800×480, superb Python library, the classic hobby photo-frame HAT. |
| [Waveshare ESP32-S3 13.3″ E6 kit](https://www.waveshare.com/esp32-s3-epaper-13.3e6.htm) | ~$330–380 | The battery play: e-ink's zero static draw + ESP32 deep sleep → **weeks on battery, no cord at all**; wakes hourly, fetches one image, sleeps. Requires firmware work (the trade the main project rejected — justified here and only here). |

Cost surprise worth remembering: a 13.3″ e-ink build (~$330 panel + $17
Pi Zero 2 W + frame) runs **$370–400 — more than the 16″ 1080p LCD build** —
for a quarter of the display area. The economical tier is the 7.3″ at
~$120–140 all-in: a desk/shelf object, not a wall statement.

## Integration sketch (surprisingly small)

These are **SPI HATs, not HDMI** — no browser, so the Chromium kiosk stack
is replaced by one small daemon, and everything server-side is reused as-is:

- `eink-agent.py` (systemd, Python): polls the **same hub feed API** (same
  collections, same auth model, same admin page control), downloads the next
  image, **Pillow-dithers to the 6-color Spectra palette**, pushes over SPI
  via the vendor library, sleeps until the next rotation slot.
- Caching: a plain local directory with the same immutable-URL rule — the
  service worker isn't needed because there is no browser.
- Board: **Pi Zero 2 W ($17)** — with no Chromium to feed, the cheapest
  board in the family is ample. (ESP32-S3 kit instead if battery operation
  is the goal.)
- Hub: gains a `cadence` per-device setting (minutes between refreshes);
  everything else — heartbeats, health, wipe, collections — carries over,
  minus browser-specific bits.
- Server: serve a pre-scaled exact-resolution variant (1600×1200 or
  800×480) so the device never decodes/scales big JPEGs.

## Positioning if built

Sell it as **"a living art print"**, not "a photo screen": zero glare, no
glow, changes a few times a day, print-like rendering. Sits beside the v2
wall frame as a third tier, not a replacement.
