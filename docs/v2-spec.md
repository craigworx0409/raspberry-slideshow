# v2 spec — wall-mounted frame, local image cache, video

Two driving goals, one driving bill:

1. **Kill the bandwidth bill.** A polling fleet re-downloads the same immutable
   images forever; at four frames this was ~$40/month in blob egress (v1 with
   remote-hosted originals). Images never change after creation, so every byte
   after the first download is waste. v2 caches **indefinitely on-device**.
2. **Wall-frame form factor.** v2 hangs on a wall like a picture: slim panel,
   Pi hidden flush behind it, one skinny cord dropping to an outlet —
   table-lamp profile, not PC.
3. Bigger board headroom: more/larger decoded images in the buffer, and
   **smooth 1080p video** as a supported slide type.

---

## Part 1 — Software

### 1.1 The cache: a Service Worker in the player (not a device daemon)

Two candidate architectures were considered:

| | A. Device-side caching proxy (daemon on the Pi) | B. Service Worker inside the player page |
|---|---|---|
| Auth | ❌ must forward session cookies/tokens to a second HTTP client — ugly and fragile | ✅ runs inside the page's origin; cookies/auth just work |
| Eviction control | ✅ exact (`statvfs`, LRU to a %-free target) | ⚠️ approximate (origin quota ≈ 60% of disk; own LRU on top) |
| Moving parts | ❌ new systemd service + Python HTTP stack + URL rewriting in player | ✅ zero new processes; ~150 lines of web code |
| Works for the private archive fleet too | ❌ auth problem is worse there | ✅ same `sw.js` drops into any player page |
| Observability | ✅ trivially | ⚠️ via `navigator.storage.estimate()` + agent `du` |

**Decision: B — Service Worker.** The auth transparency is decisive: the same
~150-line worker serves this repo's player *and* any private variant, with no
proxy plumbing. The eviction trade-off is acceptable because the budget math is
generous (below).

#### Worker behavior (`server/wwwroot/slideshow/sw.js`)

- **Scope:** intercepts same-origin `GET`s under `/media/` (images and video).
  Cache-first, network-fallback: a cached URL is served from disk **without
  revalidation, forever** — URLs are immutable by construction (content-named
  files; an edited image is a new URL).
- **Metadata:** IndexedDB `cachemeta` store: `url → { size, lastAccess }`.
  `lastAccess` updates are throttled to once per day per URL (the value only
  matters at eviction granularity; don't hammer IndexedDB on every slide).
- **Persistence:** on first run the player calls
  `navigator.storage.persist()` — on a kiosk profile this is granted, which
  exempts the origin from Chromium's automatic pressure eviction. Our own
  janitor is then the only evictor.
- **Budget & eviction (the "leave 20% free" rule):** on player start and every
  6 h, the worker runs a janitor:
  - `{usage, quota} = await navigator.storage.estimate()`
  - budget = `min(0.8 × quota, CACHE_MAX_GB param if provided)`
    (Chromium's per-origin quota is ~60% of the disk, so 0.8 × quota ≈ half
    the card — and staying inside quota means Chromium never force-evicts us.)
  - while `usage > budget`: delete lowest-`lastAccess` entries (cache entry +
    metadata row), re-estimate.
- **Size guard:** responses over 200 MB are streamed through, not cached
  (protects the budget from a stray huge video).
- **Failure posture:** any worker error falls through to plain network — the
  slideshow must never be *worse* than v1 because the cache hiccuped.
- **Durability posture (err on keeping data):** cached bytes cost real egress
  money to re-acquire, so nothing evicts them except the janitor's budget
  math: `persist()` blocks browser pressure-eviction, cache names are never
  versioned-and-dropped on player updates, the `clear-cache` command is a
  deliberate admin act (and `wipe` a more deliberate one), and the janitor
  only runs when usage actually exceeds budget — there is no time-based
  expiry at all. A frame that sits offline for a year boots with its photos.

#### Player changes (`server/wwwroot/slideshow/`)

- Register `sw.js`; add `?cache=0` param to bypass (debugging).
- New optional param `cachegb=N` → passed to the worker as `CACHE_MAX_GB`.
- Report cache stats on the hidden status overlay (usage / quota / hit count
  since load) — invaluable when eyeballing a frame's behavior remotely.

#### Server changes (`server/Program.cs`)

- `/media/*` responses get `Cache-Control: public, max-age=31536000, immutable`
  — lets the *HTTP* cache help too, and is honest: media URLs are immutable.
- Optional `HEAD` support on media (the worker uses it to size-check before
  deciding whether to cache).

#### Device changes (`device/`)

- **Drop `--incognito`** — incognito forbids Service Workers and persistent
  caches; it was there to suppress crash-restore bars, which is handled by
  `--hide-crash-restore-bubble` + `--noerrdialogs` + the existing
  singleton-lock cleanup instead.
- Chromium gets a **persistent profile** on the card
  (`--user-data-dir=/var/lib/kiosk/profile`); its *HTTP* disk cache stays
  small (`--disk-cache-size=64MB`, on disk now, not tmpfs — it stores hot
  metadata; the SW cache holds the bulk).
- Agent telemetry: add `cacheMb` (a `du -sm` of the profile's Cache Storage)
  to the heartbeat health payload, so the hub shows per-frame cache size.
- Agent command: `clear-cache` — stop browser, delete the profile's
  `Service Worker/CacheStorage` dirs, relaunch. The remote fix for cache
  corruption you can't SSH to.
- SD-wear note: cache writes are one-time-per-image by design; the v1
  write-minimization stack (journald→RAM, no apt timers, `commit=60`) stays.

**Expected effect on the bill:** each unique image is downloaded once per
frame, ever. Egress becomes `(new images) × (frames)` instead of
`(all images) × (frames) × (every rotation)` — back to single-digit dollars.

### 1.2 Self-delete ("wipe") — implemented

Threat model: handing someone a frame is handing them keys to the family
photos. Recipients are trusted; the wipe is the **"juuust in case"** tier —
a relationship changes, a device is lost in a move, a house is sold with
things left on walls. It is explicitly *not* forensic-grade: physical
possession of the SD card always wins. Recovery from a wipe = the device
comes back to you and gets re-imaged from the master.

Mechanics (`wipe` command, hub → agent, at-most-once like all commands):

1. **Neuter first:** `/etc/kiosk.conf` is overwritten (hub URL, device key,
   default URL → `about:blank`) *before* anything else, so if the kiosk
   respawn loop races the wipe, the browser can only come back to a blank
   page with no credentials.
2. Saved URL deleted; browser killed; **both** profile generations deleted
   (v2 `/var/lib/kiosk/profile` and v1 `~/.config/chromium` + `~/.cache`) —
   that's the SW photo cache, cookies, and sessions.
3. Wi-Fi profiles deleted (reuses `forget-wifi` — the recipient's Wi-Fi
   password is their PII).
4. `sync`, then **`fstrim -a`** — TRIMmed flash blocks are unrecoverable on
   most cards, upgrading "unlinked" to "gone" against casual recovery.
5. `systemctl poweroff`. The device never heartbeats again (its key is gone),
   which is itself the wipe-succeeded signal: it drops to permanently
   offline on the admin page.

Admin UI: red **Wipe** button; confirmation requires *typing the device
name* (the only command gated that hard). The queued command executes on the
device's next check-in, so it works on any online frame anywhere.

### 1.3 Video slides

- Feed items gain `type: "image" | "video"` (server infers from extension /
  content type at upload or URL registration).
- Player: `type=video` renders a muted `<video autoplay playsinline>`; the
  slide advances on `ended` (or after `delay` ms, whichever is longer;
  `loop=1` param available for ambient loops). Crossfade works identically —
  the video element fades like an `<img>`.
- Preload: the next slide (image or video) is fetched while the current one
  shows; the SW caches video bytes the same as images (≤ 200 MB guard).
- **Format guidance (documented, not enforced):** 1080p H.264 ≤ 10 Mbps is the
  sweet spot — hardware-decoded on every candidate board. HEVC also fine on
  Pi 5. Server stores uploads as-is in v2.0; an optional ffmpeg transcode
  job is v2.1 territory.

### 1.4 Board bring-up deltas (assuming Pi 5-class board)

- **Video out is micro-HDMI** (two ports; use HDMI0, nearest USB-C).
- **Power:** official spec is 5.1 V/5 A, but a kiosk with zero USB peripherals
  runs reliably from a quality 5 V/3 A USB-C PD source with
  `usb_max_current_enable=1` in `config.txt` (documented Raspberry Pi
  fallback; the 5 A ask is for downstream USB loads we don't have).
- **Graphics:** the v1 `WLR_RENDERER=pixman` workaround targeted the Pi 3-era
  V3D 2.1 scanout bug. Pi 5 (V3D 7.1) does not exhibit it — v2 defaults to
  **GL rendering** (smoother fades, required for fluid video), with pixman
  retained as a documented fallback flag. Validate on first bring-up:
  internal screenshot vs physical panel, exactly like the v1 debugging story.
- Everything else carries over unchanged: cage + Wayland, kiosk-session
  respawn loop, splash, comitup onboarding, agent, hardening script.
- **Storage:** 64 GB card baseline (A2-class). Cache budget lands ≈ 30 GB.
- RAM: 2 GB handles large image buffers + 1080p video; 4 GB is cheap
  insurance if slots are pushed high with full-quality originals.

### 1.5 v1 fleet back-port

Everything in 1.1–1.2 except the GL default applies to the deployed 3A+
frames as-is (their 32 GB cards yield ≈ 15 GB budgets — still a total-egress
kill for a family library). Ship the player/server changes first; the fleet
inherits the SW cache on its next reload with **no device-side deploy**;
the `--incognito` removal + profile flag is one small device update after.

---

## Part 2 — Hardware (wall-frame build)

> All prices/stock **verified live August 13, 2026** (every item's product page
> was fetched during research — the 2026 memory-driven Pi price rises are
> reflected).

### Recommended build (per frame)

| # | Part | Pick | Price | Why / notes |
|---|---|---|---|---|
| 1 | Board | **Raspberry Pi 5 — 2 GB** ([PiShop](https://www.pishop.us/product/raspberry-pi-5-2gb/) or [CanaKit](https://www.canakit.com/raspberry-pi-5-2gb.html), both in stock) | $65.00 | Post-price-rise sweet spot: A76 cores decode/render big images ~2-3× faster than Pi 4; HEVC 4Kp60 hardware decode, H.264 1080p smooth in software. 4 GB is $110 for RAM a kiosk won't use; the $45 Pi 5 1 GB risks OOM with big preload buffers. No Pi 6 exists or is dated. |
| 2 | Display | **Arzopa Z1FC 16.1″ 1080p** ([Amazon](https://www.amazon.com/dp/B0CH9WTW56)) | $129.98 ($109.99 w/ Prime) | Best photo panel of the field-tested crop: 98% *measured* sRGB, matte, 9.3 mm thin, 1.7 lb, dims to a measured **3.5 W**, runs from 5 V/3 A USB-C. Mini-HDMI input. Flat back, no VESA → printed/adhesive mount (see #7). |
| 2b | *VESA alt.* | UPERFECT 15.6″ VESA (M156T03-W, [direct](https://uperfect.com/products/uperfect-15-6-vesa-portable-monitor)) | $129.99 | The only mainstream panel with a real **VESA 75** mount — bolt-on wall hardware, but only 72% sRGB (muted photos). Amazon ASIN currently unavailable; buy direct. |
| 3 | Power | **MINIX 70W flat GaN, dual USB-C** ([Amazon](https://www.amazon.com/dp/B0FZK7Q8ST)) | $32.90 ($29.35 Prime) | 12.8 mm thin, hugs the wall. **Verified simultaneous split 45 W + 20 W** (also 35+35): monitor on one port, Pi on the other at 5 V/3 A. Gray only. (The prettier Nomad Slim 65W is sold out everywhere as of Aug 2026.) |
| 4 | Video cable | **Seadream micro-HDMI → mini-HDMI, 1 ft** ([Amazon](https://www.amazon.com/dp/B08BFZFT7R)) | $6.66 | Pi 5 is micro-HDMI; the Arzopa is mini-HDMI — this is the single-cable, zero-adapter link. (UPERFECT path instead: Seadream 8″ 90° micro→full-HDMI, B07DVJGF7C, $6.99 — low stock, 2-packs available.) |
| 5 | Power hops | SUNGUY 6″ right-angle USB-C, 2-pack ([Amazon](https://www.amazon.com/dp/B0DCG1SVCG)) | $6.99 | Flush right-angle power at the frame end for both devices; 60 W/3 A rating is correct for 5 V/3 A (Pi) and 45 W@15 V (monitor). |
| 6 | Wall run | 2× 6 ft white round USB-C 60 W (~$9 ea) in **D-Line Micro+ half-round paintable raceway** ([kit](https://www.amazon.com/D-Line-Cable-Raceway-Kit-Self-Adhesive/dp/B07C43K1MQ) $29.79; single 39″ ~$12) | ~$30–48 | 0.39″ off the wall, hinged lid, holds both cables, paint it wall-color. Honest finding: flat *white* USB-C cables don't exist at US retail (Tripp Lite's flat one is black) — inside a raceway, roundness is moot. |
| 7 | Mount | **TheTimmyBoy Pi 5 panel-sandwich case** (free STL, [MakerWorld 1527533](https://makerworld.com/en/models/1527533-raspberry-pi-5-case-mount-for-amazon-15-6-monitor)) + **Mount-It! ultra-low-profile VESA 75 wall plate** ([Amazon](https://www.amazon.com/dp/B004DEJAZU)) | $16.99 + filament | Printed case clips the Pi 5 flat to the back of MNN/KYY-family panels (verify Arzopa shell fit; STEP sources available for remixing) and includes a printable VESA-75 bracket; the steel plate holds it 0.55″ off the wall. Zero-visible-hardware alternates: French cleat pair $6.99, or adhesive magnet + steel-plate route ~$44. |
| 8 | Cooling | Pi rides the printed case **bare + low-profile passive heatsink**, or **GeeekPi armor case** ([Amazon](https://www.amazon.com/dp/B0CM9KRNXZ), $12.99, 24 mm) if mounted beside the panel | $0–17 | Slideshow duty cycle idles ~3 W — passive is fine. FLIRC Pi 5 ($16.95) is the premium fanless box when depth allows. **Avoid** Argon THRML (it's an active blower despite looks) and the official case (needs its fan). |
| 9 | Storage | SanDisk-class 64 GB A2 microSD | ~$12 | ≈30 GB SW cache budget after OS. |

**Per-frame total: ≈ $265–285** (Arzopa path, incl. raceway & mount; the
$110 4 GB board would add $45 for headroom v2 doesn't need).

### Power architecture (validated)

One outlet → MINIX flat charger → two thin cables up one painted raceway →
right-angle hops behind the panel. Pi 5 runs at 5 V/3 A with
`usb_max_current_enable=1` (the 5 A official ask only funds downstream USB
peripherals we don't have). **Single-cable variants were researched and
rejected with reasons:** PD splitter cables fix their legs at profiles a Pi
can't drink (9 V-centric secondary legs; Pi PMIC requires 5 V/3 A), and any
renegotiation on a shared cable can brown-out the Pi mid-show. Monitor
PD-passthrough daisy-chaining exists (e.g. Plugable USBC-PDMON) but adds a
laptop-profile-oriented failure point. Two cables, one raceway, no drama.

### Total power budget

~3 W Pi idle + 3.5–9 W panel = **7–12 W total** — table-lamp class, as
specified. (v1 3A+ frames: nearly identical.)

---

## Part 3 — Open questions for v2.1

- Optional server-side ffmpeg transcode of video uploads to a known-good
  1080p H.264 profile.
- PoE HAT variant (single Ethernet cable instead of power cord) for
  installations near a switch — bulkier, probably v3.
- E-ink or auto-dimming ambient-light sensor for night hours (the player
  already supports a `dim` param via CSS filter; a $2 photoresistor on GPIO
  could drive it).
