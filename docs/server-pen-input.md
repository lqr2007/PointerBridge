# Receiver synthetic pen input

Starting the server listener now creates a Windows synthetic PT_PEN device.
Stopping the listener releases the pen and destroys the device. Closing Debug
does not stop injection. This is Windows pointer injection, not a virtual HID
driver or a WinTab implementation.

Requirements: Windows 10 version 1809/build 17763 or newer, an interactive desktop,
and an application that accepts Windows pen/Ink input. Elevated windows, secure
desktops and applications requiring hardware-specific/WinTab input may not accept
the injected pen. Errors include the Win32 error code and stop the listener.
No automatic privilege elevation is performed.

Normalized coordinates map to the entire **primary monitor** in physical pixels,
including the taskbar. Set the client's capture aspect ratio to match the target
monitor to avoid distortion. The server's per-monitor DPI manifest takes effect
after a full restart. `SyntheticPenInput.Inject` contains the monitor selection
and coordinate mapping for future display-selection work.

The receiver injects each accepted packet, independently of the one-second log
timer. The state machine creates hover before contact, converts contact changes
to down/up, preserves pressure, barrel switch and eraser, and releases the old
tool on pen/eraser or session/source changes. Pressure is mapped to the Windows
pen range 0..1024; tilt/rotation are not marked valid because v1 does not send them.

After one second without a valid newer packet, a background watchdog releases
the pen (checked every 100 ms), even when Debug is closed. Invalid/duplicate/old
packets do not refresh the timeout. UDP loss can still erase short transitions;
this is not a guaranteed-delivery drawing protocol.

Security: v1 UDP is unauthenticated and unencrypted. Starting the listener allows
accepted network packets to control desktop pen input. Use only on a trusted LAN,
restrict firewall rules to the sender IP, and do not forward/expose the port to
the Internet. The first valid source holds the receiver until its one-second
timeout; this is source selection, NOT authentication.

Verification: automated tests use a fake injection sink and test state transitions,
native structure sizes and UDP timeout/stop behavior without injecting real input.
Actual Windows pen delivery, right-click behavior, pressure and eraser support
must be tested in the intended drawing application (Windows Ink mode).

Manual test: start receiver listening, open a blank document in the target drawing
app, connect the client, and test hover, light/heavy strokes, side button and
eraser. Disconnect the network while touching; contact should be released in
about 1.0–1.1 seconds. Stop listening to immediately disable remote pen input.
