# raspberry-slideshow

![Slideshow demo](docs/hero.gif)

**A ~$50 Raspberry Pi turns any HDMI screen into a remote-controlled photo
frame. A ~$5–20/month Linux VM runs the website that controls it.**

Give one to your parents. Point it at a collection of family photos. When you
add new pictures, every frame updates itself. When something goes wrong at
their house, you diagnose it from yours.

**▶ Live demo:** [myslideshow.craig0409.com/slideshow/?delay=4000&fade=800](https://myslideshow.craig0409.com/slideshow/?delay=4000&fade=800)
— exactly what a frame displays (press F11 for the full effect). The player
is steered entirely by URL parameters, so "configuring" a frame just means
choosing its URL:

| Try it | What it shows |
|---|---|
| [`/slideshow/`](https://myslideshow.craig0409.com/slideshow/) | every image, shuffled, calm default pace (12 s) |
| [`?group=Family Moments`](https://myslideshow.craig0409.com/slideshow/?group=Family%20Moments) | a single named collection |
| [`?group=Scenery,Family Moments`](https://myslideshow.craig0409.com/slideshow/?group=Scenery,Family%20Moments) | two collections blended |
| [`?delay=4000&fade=800`](https://myslideshow.craig0409.com/slideshow/?delay=4000&fade=800) | brisk demo pacing (4 s slides, 0.8 s crossfade) |
| [`?group=Sunsets & Water&delay=20000&fade=3000`](https://myslideshow.craig0409.com/slideshow/?group=Sunsets%20%26%20Water&delay=20000&fade=3000) | gallery mode: long holds, slow dissolves |
| [`?img=thumb&shuffle=0`](https://myslideshow.craig0409.com/slideshow/?img=thumb&shuffle=0) | bandwidth-light thumbnails, fixed order |

```
┌──────────────┐  heartbeat every 45s    ┌───────────────────────────┐
│ Raspberry Pi │ ──────────────────────► │   your Linux VM           │
│  (Chromium   │ ◄────────────────────── │   ASP.NET Core + SQLite   │
│   kiosk)     │  URL + commands         │   behind Caddy (HTTPS)    │
└──────┬───────┘                         └────────────┬──────────────┘
       │ HDMI                                         │
   any TV or                              web UI: image library,
    monitor                               device admin, user view
```

- The Pi boots straight into a fullscreen browser showing a slideshow.
- The server hosts the images (uploaded and auto-resized to 1080p, or
  referenced by URL), grouped into named **collections**.
- Each frame can show any collection — switched remotely from a web page,
  including by non-technical "owners" you assign per device.
- Devices report health (memory, temperature, Wi-Fi signal, power quality)
  with every heartbeat, so you can support a frame you can't SSH into.
- Wi-Fi onboarding is a phone-friendly captive portal: the frame broadcasts
  its own hotspot when it can't find a known network. No keyboard needed, ever.

This repo is both the **source code** and a **step-by-step guide** aimed at a
developer who is comfortable on Windows but has never operated a Linux server.
Every Linux command you need is written out; nothing assumes prior experience.

## The path

Read and do these in order. Each step ends with something working.

| Step | Guide | You end up with |
|---|---|---|
| 1 | [Buy the hardware](docs/01-hardware.md) | ~$50 of parts on the way |
| 2 | [Rent and learn your Linux VM](docs/02-linux-vm.md) | a server you can SSH into, and enough Linux to be dangerous |
| 3 | [Deploy the server app](docs/03-server-deploy.md) | `https://slideshow.your-domain.com` live, with HTTPS |
| 4 | [Build the first frame](docs/04-build-the-frame.md) | a Pi that boots into your slideshow |
| 5 | [Clone more frames](docs/05-clone-more-frames.md) | as many frames as you have Pis |
| 6 | [Operate the fleet](docs/06-operate.md) | day-2 skills: health, updates, troubleshooting |
| 7 | [Reuse the VM for your next idea](docs/07-new-app-runbook.md) | a repeatable recipe for hosting any .NET app |

## Costs

| Thing | One-time | Monthly |
|---|---|---|
| Raspberry Pi 3 Model A+, PSU, SD card, HDMI cable | ~$50/frame | — |
| Hetzner Cloud VM (CX22/CPX11 class) | — | ~$5–8 |
| Domain name (any registrar; DNS at Cloudflare, free plan) | — | ~$1 amortized |

The VM is wildly oversized for this app alone — the whole point of step 7 is
that the same box hosts your next several ideas at no extra cost.

## Repo layout

| Path | What |
|---|---|
| `server/` | ASP.NET Core app: image library + device hub + slideshow player |
| `device/` | Everything installed on a Pi (scripts, agent, systemd unit, installers) |
| `deploy/deploy-vm.ps1` | One-command deploy from your Windows machine to the VM |
| `docs/` | The guide |

## Hard-won engineering notes

This design was extracted from a working four-frame deployment. The
non-obvious decisions, all learned the hard way, are documented inline where
they live, and summarized in [docs/06-operate.md](docs/06-operate.md):

- **Wayland (`cage`) with CPU rendering, not X11** — on Raspberry Pi OS
  Trixie, the Pi 3-era GPU scans out black frames for tiled GPU buffers while
  everything *looks* healthy internally.
- **`--no-memcheck` before the URL** — Pi OS's Chromium wrapper otherwise
  blocks forever on an invisible "low RAM" dialog on 512 MB boards.
- **Chromium singleton lock cleared each launch** — a cloned SD card
  otherwise refuses to start the browser ("profile in use on another
  computer"), and so does one that lost power at the wrong moment.
- **Server-side 1080p variants** — a 512 MB board decodes 1080p images
  happily and full-resolution photos not at all; periodic browser reloads
  shed the memory creep that image-heavy pages accumulate.
- **Write-minimized SD card** — logs to RAM, browser cache to RAM, batched
  filesystem commits, no background apt: people *will* pull the plug.

## License

MIT — see [LICENSE](LICENSE).

---

## ☕ Tip jar

If you appreciate this project, send me a tip! Click a QR (or scan it from
your phone):

| PayPal | Cash App | Venmo |
|:---:|:---:|:---:|
| <a href="https://paypal.me/Craig0409"><img src="docs/paypal.jpg" width="160" alt="PayPal QR"></a> | <a href="https://cash.app/$Craig0409"><img src="docs/cashapp.jpg" width="160" alt="Cash App QR"></a> | <a href="https://venmo.com/Craig0409vt"><img src="docs/venmo.jpg" width="160" alt="Venmo QR"></a> |
| [paypal.me/Craig0409](https://paypal.me/Craig0409) | [cash.app/$Craig0409](https://cash.app/$Craig0409) | [venmo.com/Craig0409vt](https://venmo.com/Craig0409vt) |
