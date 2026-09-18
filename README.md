# Jellyfin Channels

Builds 24/7 virtual channels from your own Jellyfin library. A channel runs on
the wall clock whether or not anyone is watching, so tuning in joins whatever is
airing — you miss what already went out, and leaving and coming back later puts
you on the *then-current* program rather than resuming where you stopped.

## Status: Phase 0

The scheduling core and its tests. No server endpoints, no configuration UI, and
no client support yet.

| Phase | Scope | State |
|-------|-------|-------|
| 0 | Schedule resolver + deterministic ordering, with tests | **done** |
| 1 | Snapshot builder, library-change listener, REST API, config page | not started |
| 2 | tvOS/iOS channel list and channel playback mode | not started |
| 3 | Roku and Android | not started |

## Why the schedule lives on the server

Each client could compute its own schedule, but then the resolver is written
once per platform and the versions drift — three implementations of the same
arithmetic, disagreeing about what is on. Keeping it here means one
implementation, and clients stay thin: list the channels, ask what is airing,
play that item at that offset.

It also puts three problems where they can actually be solved:

- **Progress and watched state.** Channel viewing must not report progress or
  mark anything watched, or a few minutes of background TV smears bogus resume
  positions across every device. The server decides; clients just obey.
- **A library that moves.** Media gets added and deleted underneath a channel.
  The server sees those events and rebuilds, rather than every client
  discovering the gap on its own.
- **One clock.** Wall-clock scheduling against per-device clocks is a bug
  waiting to happen.

## Layout

```
src/Jellyfin.Plugin.Channels.Core    scheduling logic — no Jellyfin dependency
src/Jellyfin.Plugin.Channels         plugin shell (Phase 1+)
tests/…Core.Tests                    xunit
```

`Core` deliberately references nothing from Jellyfin. The schedule is pure
arithmetic, and isolating it means it builds on any SDK, tests in milliseconds,
and cannot quietly acquire a server dependency.

## The resolver

`ScheduleResolver.Resolve(snapshot, anchorUtc, nowUtc)` returns the airing item,
how far into it to start, when it began and ends, and what follows.

The schedule is never materialised. Writing a row per airing per day would mean
storing and invalidating an unbounded table; instead a running order plus an
anchor is enough to compute any instant, so a channel costs one small record no
matter how far ahead or behind it is queried.

Details that are load-bearing rather than incidental:

- **Runtimes are ticks, not seconds.** Channels get queried years past their
  anchor; rounding each program to whole seconds lets the error accumulate.
- **Intervals are half-open.** An instant landing exactly on a boundary belongs
  to the program starting there. Treated as closed, a viewer tuning in on the
  boundary joins the previous program at an offset equal to its whole runtime
  and sees only its final frame.
- **Before the anchor, the channel holds at its first program** rather than
  extrapolating backwards into airings that never happened.
- **Zero-runtime items are dropped on construction.** Left in the order they
  either never air or divide the cycle by zero.
- **Ordering does not use `Random`.** Its sequence for a given seed is not
  stable across .NET versions, so an upgrade would silently reshuffle every
  existing channel. `DeterministicShuffle` spells out splitmix64 + Fisher-Yates,
  and a pinned vector in the tests fails if that ever changes.

## Toolchain

`Core` and its tests build on .NET 9. The plugin shell targets **net10.0**,
because `Jellyfin.Controller` 12.1.0 ships net10.0 only — a Jellyfin 12 plugin
has to follow. Building the shell needs the .NET 10 SDK; Phase 0 does not.

```bash
dotnet test
```

## Licence

MIT.
