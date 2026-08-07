# Sync protocol

Status: **design stub** — implementation starts at M7. This file records the
decisions already frozen at kickoff so earlier milestones don't preclude them.

## Strategy

Ship an OpenSubsonic-compatible HTTP server first (`Mirelia.Sync`, ASP.NET
Core minimal APIs). Mature third-party clients on both mobile platforms give
immediate usability with offline caching solved, and validate the protocol
layer before any first-party mobile code is written.

First-party mobile apps come after, as a separate stack (Kotlin/Compose,
Swift/SwiftUI). Nothing is shared but the wire protocol.

## Constraints designed around from day one

- **Smart playlists are materialized before transfer.** The desktop evaluates
  the query, snapshots it, and sends a static list plus files. Mobile never
  needs a query engine.
- **iOS cannot be sync-only.** No API writes into the system Music library;
  an iOS companion is necessarily a full player. Either budget for that or
  lean on OpenSubsonic clients and skip a first-party iOS app.

## Delivery policy

All transcoding decisions live in one place so new formats never touch sync
code:

```csharp
DeliveryDecision Decide(Track t, DeviceProfile d);
// => AsIs | Transcode(codec, bitrate) | Skip
```

Inputs: target device's supported codecs, the user's "convert lossless to AAC
for mobile" setting, and whether the track came from a plugin decoder
(always transcode those).

## Discovery and delta

- Discovery: mDNS (`mirelia._tcp`), plus manual host entry.
- Pairing: short code → long-lived token.
- Delta: monotonic change journal (`change_log(seq, entity, id, op)`) plus
  `content_hash` for conflict detection. Client sends its last seen `seq`.
