# Jellyfin Channels

Builds 24/7 virtual channels from your own Jellyfin library. A channel runs on
the wall clock whether or not anyone is watching, so tuning in joins whatever is
airing — you miss what already went out, and leaving and coming back later puts
you on the *then-current* program rather than resuming where you stopped.

## Status: Phase 1

Channels can be defined in the Jellyfin dashboard and queried over HTTP. No
client support yet — nothing plays them.

### API

| | |
|---|---|
| `GET /Channels` | every configured channel |
| `GET /Channels/{id}/NowPlaying` | the airing item, the offset to start at, when it ends, and what follows |

`NowPlaying` answers `204 No Content` when the channel is off air — a gap in the
schedule is a normal state, not an error. `StartPositionSeconds` is a
join-in-progress offset and **must not** be written back as playback progress.

| Phase | Scope | State |
|-------|-------|-------|
| 0 | Schedule resolver, dayparting, deterministic ordering, with tests | **done** |
| 1 | Snapshot builder, library-change listener, REST API, config page | **done** |
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

## Dayparting

A channel can be one schedule all day, or split into dayparts — cartoons in the
morning, drama at night, optionally only on certain days.

Each daypart owns an independent running order on its own continuous timeline.
That is what keeps it cheap: selecting a daypart chooses *which* timeline you
are watching, and the per-daypart arithmetic is the same resolver, untouched. It
is also consistent with the premise — a timeline advances whether or not anyone
is tuned in, and a daypart is simply a channel visible for part of the day.

- **Timezone is explicit per channel**, never inherited from the host. A server's
  own zone is easy to get wrong, and a schedule that quietly shifts by an hour is
  worse than one that refuses to guess.
- **A program overrunning its daypart is cut off at the boundary**, the way a
  broadcast schedule cuts away regardless of what is on. Letting it overrun makes
  dayparts drift out of their slots; refusing to start anything that will not fit
  leaves dead air.
- **A program ending exactly on the boundary still hands over** to the incoming
  daypart. Treating only the overrun case as a handover reports the wrong next
  program whenever a runtime divides the daypart evenly — which is common.
- **A gap between dayparts is off air**, reported honestly rather than as an error.
- **Blocks spanning midnight are rejected**; express them as two dayparts
  (22:00-24:00 and 00:00-02:00) so which day an airing belongs to is never
  ambiguous at exactly the boundaries the schedule must get right.

## Commercials and interstitials

Already accommodated, and needing no change here: a program is only
`(ItemId, RuntimeTicks)`, so the resolver has no idea whether an entry is an
episode, a bumper or an ad — only how long it lasts. Inserting interstitials is
a snapshot-builder concern.

Breaks *between* programs work as-is. Breaks *inside* a program need
`ProgramSlot` to carry an end offset as well as a start, so playback can stop
part-way, cut away, and resume.

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

The SDK is installed per-user at `~/.dotnet` (no sudo required, and it does not
disturb the system install at `/usr/local/share/dotnet`). SDK 10 compiles both
target frameworks, and the .NET 9 *runtime* is installed alongside it because
running the net9 test assembly needs it.

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"

dotnet build
dotnet test
```

Do not re-sign the installed binaries. If `dotnet` exits 137 (SIGKILL) on macOS,
ad-hoc signing the host makes it *look* fixed while leaving it inconsistent with
the Microsoft-signed libraries the MSBuild worker processes load, which then die
with "Child node exited prematurely". Reinstall into a clean directory instead.

## Licence

MIT.
