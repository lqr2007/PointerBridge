# Pen UDP protocol v1

Each UDP datagram contains exactly **16 bytes** of binary payload. Integers use
little-endian byte order. No strings, field names, debug messages or device IDs
are transmitted. UDP/IP headers are additional to these 16 bytes.

| Offset | Bytes | Value |
| --- | --- | --- |
| 0 | 1 | Version, currently 1 |
| 1 | 1 | State flags, see below |
| 2 | 4 | Random nonzero session ID, changes when creating a sender |
| 6 | 4 | Unsigned sequence number, increments for every datagram, wraps |
| 10 | 2 | Normalized X, 0 = left, 65535 = right |
| 12 | 2 | Normalized Y, 0 = top, 65535 = bottom |
| 14 | 2 | Normalized pressure, 0..65535 |

Flags: bit 0 = in range, bit 1 = contact, bit 2 = eraser,
bit 3 = barrel button down, bit 4 = barrel state known.
Bits 5..7 are reserved and zero. If bit 4 is zero, do not interpret bit 3 as
a hardware-confirmed button state. Leaving the capture region clears range,
contact, barrel-down and pressure. Eraser/known flags may remain set.

Divide X/Y/pressure by 65535.0 to recover normalized values. Coordinates use
the white capture area inside the fullscreen black window. The white area uses
the user's selected target aspect ratio and is maximized/centered inside the
capture monitor. Black bars send a released/out-of-range state, not clamped edge
contact. No capture dimensions or DPI are needed to decode positions.

Movement is coalesced to the latest quantized sample at approximately 120 Hz
maximum. Flag transitions bypass movement throttling and remain ordered in a
bounded queue. Intermediate points within a WPF event are not transmitted;
this is a low-bandwidth latest-state stream, not lossless stroke recording.
On queue overflow, pending transitions are discarded and release/current states
are scheduled to resynchronize. Errors are reported to the debug window.

Unchanged full state is repeated every approximately 250 ms, including when the
pen is stationary or absent. Closing/changing the sender attempts three release
datagrams before disposing the socket. Final sends on process exit are best
effort and may not finish before process termination.

Receiver requirements (server parsing/state tracking and synthetic pen injection
are now implemented; see `server-pen-input.md`):

- Check version, payload length and valid flag combinations before injection.
- Track sequence numbers per session/source, discarding old/duplicate packets.
  With unsigned wrap, a is newer than b when unchecked((int)(a - b)) > 0.
- Reset injection state on a legitimate new session; do not let late packets
  from a retired session switch the receiver back. Track/expire retired sessions.
- Treat each packet as full state, not just an event. Reconstruct contact/tool
  transitions, releasing the previous tool before switching tools.
- After about 1 second without a valid packet, release contact/buttons and
  leave range. Never rely on receiving the last release datagram.
- UDP loss can still erase a short press/release occurring between received
  states. This protocol provides no acknowledgment/retransmission guarantee.
- Packets are neither authenticated nor encrypted. Restrict the receiver to
  trusted peers/networks; do not expose pointer injection directly to the Internet.

Run sender loopback checks with:

    dotnet run --project tests/PointerBridge.TransportChecks
