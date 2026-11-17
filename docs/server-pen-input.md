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
Eraser identity and eraser contact are separate: native `PEN_FLAG_INVERTED`
(0x2) identifies the eraser end while hovering or touching; `PEN_FLAG_ERASER`
(0x4) is set only while the eraser is in contact. Hover, UP and LEAVE never
advertise eraser-pressed state. Tool changes release the previous endpoint before
entering the new one. The UDP eraser bit remains an endpoint identity, not a
contact flag, so the client and wire format do not need to change.

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
retries the same frame up to three times. Other native errors are handled by the
device owner: Win32 errors, including invalid parameter (87), are logged as
`[Error]`, the old device is destroyed, and creation/injection is retried every
250 ms on the same dedicated thread. This covers enter, down, contact updates,
up, leave, release, cleanup and device creation, without a hover-only condition
or retry limit. Reconnection does not prove the cause of the original failure.
Input older than one second is discarded rather than replaying a stale down;
the next packet/heartbeat synchronizes current state. Stop/Exit cancels recovery
before waiting for the injection thread. The snapshot lock is never held during
injection/recovery, so Debug and Stop remain responsive. Recoverable listener or
socket failures also automatically restart listening every 500 ms; stopping
listening cancels pending restarts.

Both applications prefix every debug line with `[Info]`, `[Warning]`, `[Error]`
or `[Fatal]`, followed by the timestamp, and retain the latest 1000 lines even
before Debug is opened. Warnings/errors are nonmodal and do not terminate the
application. Only Fatal (unsupported platform/native API, or an unexpected
unrecoverable exception) displays an error dialog and shuts down with exit code 1.

Run the checks without generating desktop input:
`dotnet run --project tests/PointerBridge.ServerChecks`.
These checks cover severity/history, every injection action, repeated creation
failure beyond the previous retry limit, cleanup failure, stale input, thread
affinity, cancellation, listener responsiveness/port release, and sender shutdown.
