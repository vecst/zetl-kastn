# Zetl Linux Input Safety Spike

This executable is the B1 hardware proof for evdev/uinput. It is deliberately
separate from the production app and requires an explicit `--arm` flag before
it will grab a keyboard.

Run read-only diagnostics first:

```bash
./Zetl.Linux.Spike diagnose
./Zetl.Linux.Spike observe --seconds=10 \
  --device=/dev/input/by-id/usb-Keychron_Keychron_C2_Pro-event-kbd
./Zetl.Linux.Spike uinput-test
```

Run the time-bounded forwarder over SSH:

```bash
./Zetl.Linux.Spike forward --arm --seconds=15 \
  --device=/dev/input/by-id/usb-Keychron_Keychron_C2_Pro-event-kbd
```

Add `--trace-events` when diagnosing key transitions. While grabbed, hold both
Shift keys and press Escape to release immediately. Killing the process also
closes the evdev file descriptor, which makes the kernel release `EVIOCGRAB`.
The forwarder refuses to grab until all physical keys have remained released,
then tracks and explicitly releases every key pressed on its virtual device
before removing the grab.

The spike was validated on AerynOS 2026.05 under Wayland on June 7, 2026. The
target user had read access to the selected evdev device and write access to
`/dev/uinput`; no root process was required.
