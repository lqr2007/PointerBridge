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

Verification: automated checks use a fake injection sink and clock to test state
transitions, burst timing, retries and native structure sizes without injecting real input.
Actual Windows pen delivery, right-click behavior, pressure and eraser support
must be tested in the intended drawing application (Windows Ink mode).

The injection boundary spaces native calls by at least 1 ms, including consecutive
hover/down and up/leave frames and packets drained from a UDP backlog. Every frame
uses an actual QPC timestamp with `dwTime` left at zero. `ERROR_NOT_READY` (21)
retries the same frame up to three times; other errors, including invalid parameter
(87), propagate without retrying contact input. The device owner can recover error
87 during pure hover by destroying and recreating the device, then applying the
hover packet. This is limited to three recoveries per rolling minute. A failed
contact or contact-to-hover transition is never replayed. A successful recovery
keeps the listener running and is reported in Debug; it does not establish the
cause of the original error. Diagnostics include QPC, native
call spacing, button changes and retry count. Timing is a possible contributor to
intermittent failures, not a confirmed explanation for error 87.

Run the checks without generating desktop input:
`dotnet run --project tests/PointerBridge.ServerChecks`.
These checks also cover hover recovery, its rate limit, eraser preservation,
contact failure propagation and device thread affinity.

Manual test: start receiver listening, open a blank document in the target drawing
app, connect the client, and test hover, light/heavy strokes, side button and
eraser. Disconnect the network while touching; contact should be released in
about 1.0–1.1 seconds. Stop listening to immediately disable remote pen input.
