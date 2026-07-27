#!/bin/bash
# Compositor guard loop — launched from .bash_profile on tty1
# (exec /usr/local/bin/kiosk.sh). cage stays up across browser restarts
# (kiosk-session.sh loops chromium inside it); this outer loop only matters
# if cage itself dies.
#
# X11 was abandoned after a Trixie regression left Xorg scanout black on the
# Pi 3's V3D 2.1 GPU while the kernel KMS path (which cage uses) works fine.

# CPU rendering into plain linear buffers: the vc4 driver on V3D 2.1 (Pi 3
# era) mis-scans GPU-rendered tiled buffers as black on Trixie
export WLR_RENDERER=pixman

while true; do
    cage -- /usr/local/bin/kiosk-session.sh
    sleep 2
done
