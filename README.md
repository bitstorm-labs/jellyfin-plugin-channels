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
| `GET /VirtualChannels` | every configured channel |
| `GET /VirtualChannels/{id}/NowPlaying` | the airing item, the offset to start at, when it ends, and what follows |

`NowPlaying` answers `204 No Content` when the channel is off air — a gap in the
schedule is a normal state, not an error. `StartPositionSeconds` is a
join-in-progress offset and **must not** be written back as playback progress.

| Phase | Scope | State |
|-------|-------|-------|
| 0 | Schedule resolver, dayparting, deterministic ordering, with tests | **done** |
| 1 | Snapshot builder, change listener, REST API, config page, seasons, exclusions | **done** |
| 2 | tvOS/iOS channel list and channel playback mode | not started |
| 3 | Roku and Android | not started |

### Management API (0.11)

Administrators only (`RequiresElevation`), reads included. Clients use it for
"Add to Channel" and a Manage Channels screen; dayparts, seasons and filter
rules stay in the dashboard.

| | |
|---|---|
| `GET /VirtualChannels/Manage` | every channel with its dayparts, added titles and rule summary |
| `GET /VirtualChannels/Manage/Membership?itemId=` | `Added` / `ViaRule` / `None` for every channel and daypart |
| `POST /VirtualChannels/Manage` | new all-day channel `{ Name, Logo?, SeedItemId? }` → 201 |
| `PATCH /VirtualChannels/Manage/{id}` | `{ Name?, Logo? }`; `Logo: ""` removes the logo |
| `DELETE /VirtualChannels/Manage/{id}` | the channel and the collection the plugin made for it |
| `POST /VirtualChannels/Manage/{id}/Items` | `{ ItemId, DaypartIndex? }`, idempotent |
| `DELETE /VirtualChannels/Manage/{id}/Items/{itemId}?daypartIndex=` | added titles only; rule-sourced ones are 404 |
| `GET /VirtualChannels/Logos/{key}` | a shipped logo by key (`?style=mono`), for a picker |

A series (or an episode's or season's series) joins the daypart's first Series
block. A film joins the channel's own BoxSet, "SashimiTV · {Name}", created on
first use and recorded as `ManagedCollectionId`; a Collection block for it is
added to the daypart. Every edit runs under one lock on a copy of the
configuration and is saved through `UpdateConfiguration`, the dashboard's own
path, so it invalidates the running orders the same way.

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

## Two traps this had to be built around

Both were found by deploying to a real server, not by reading docs:

- **The route cannot be `/Channels`.** Jellyfin's own `ChannelsController` owns
  it, and registering a second one does not shadow it — every request to it then
  fails with `AmbiguousMatchException`. Hence `/VirtualChannels`.
- **A collection's members are linked children, not physical ones.** Asking for
  them through `InternalItemsQuery` returns an empty set *with no error*, which
  surfaces only as a channel quietly missing its movies. Walk the collection with
  `GetRecursiveChildren(includeLinkedChildren: true)` instead.

Configuration is also deliberately made of primitives — minutes past midnight and
a day bitmask rather than `TimeSpan` and a `[Flags]` enum. Plugin config
round-trips through Jellyfin's JSON API and persists via `XmlSerializer`, and
both are hostile to richer types: Jellyfin's `JsonFlagEnumConverter` implements
`Write` but throws `NotImplementedException` on `Read`, so a flags enum cannot be
posted back at all.

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
- **A Collection block is its membership, live.** Adding a film to the
  collection rebuilds the channel like any library change — the change
  listener treats a `BoxSet` update as it treats a `Video`, because that is
  the one folder whose contents a channel airs.
- **A daypart can be seasonal.** `SeasonStartMonthDay` and `SeasonEndMonthDay`
  are MMDD (1001 is 1 October), both inclusive, and carry no year because the
  window recurs. An end before the start wraps the year end, so 1215-0105 is
  Christmas rather than an error. Outside its window a daypart is simply not on
  air — the same state as a gap — so one channel can carry a different theme per
  season and needs no separate notion of a dormant channel. Both ends or
  neither: a half-set window is rejected rather than silently meaning "always",
  which is how a Christmas channel ends up airing in July with nothing in the
  log to say why.

## Logos

A channel can wear one of the logos the plugin ships (`GET /VirtualChannels/Logos`
lists them): `GET /VirtualChannels/{id}/Logo` returns the PNG, `?style=mono` the
white version a player lays over the picture. The endpoint is anonymous, like
Jellyfin's own images, so a client can hand the URL straight to an image view.
The channel list and the guide carry the key in effect today as `Logo`; clients
put it in the URL, which makes a day of caching safe.

A seasonal daypart may carry its own `Logo`, which replaces the channel's for
the whole of its season — by date, not time of day, so a December snowflake
shows at breakfast too. The SVG sources sit beside the PNGs in `Logos/`.

## Filters

A `Filter` block matches on genres, tags and release year, intersected. That
cannot say "Reality but not Food", and that is exactly what comes up when two
channels are built from overlapping genres and each wants a clean identity.
`ExcludeGenres` disqualifies an item that carries any listed genre. For
episodes the genre is read from the series, because that is where metadata
providers put it — a "Science Fiction" channel filtering episodes directly found
8 of them in a library holding 74 sci-fi series.

## Running order stability

The shuffle is decided per item — a hash of the daypart's seed and the item's
id — never by position. Fisher-Yates would draw its swaps from the list's
*length*, so one new download changes every draw after it and reshuffles the
whole channel. On a server where a downloader adds episodes continuously that is
the normal case, not the edge, and it defeats the reason the seed is persisted.
Sorting by a per-item key inserts a new episode in its own place and leaves
every other item where it was.

## Commercials and interstitials

Already accommodated, and needing no change here: a program is only
`(ItemId, RuntimeTicks)`, so the resolver has no idea whether an entry is an
episode, a bumper or an ad — only how long it lasts. Inserting interstitials is
a snapshot-builder concern.

Breaks *between* programs work as-is. Breaks *inside* a program need
`ProgramSlot` to carry an end offset as well as a start, so playback can stop
part-way, cut away, and resume.

## The planner

`DayPlanner` lays each local day out in order, the way a broadcaster builds a
schedule, and `ChannelScheduleResolver` answers "what is on" and the guide
from those plans. Every programme starts from its beginning and plays to its
end:

- **Each block opens with a programme at its start.** A daypart keeps its
  place in its running order from one block to the next, so it never repeats
  or skips — a continuous timeline could not promise this, and joined films
  partway through whenever a daypart opened.
- **A programme only starts if it ends before its block does.** Otherwise one
  of the next few that fits airs (the skipped one goes first next time), else
  a break. An all-day daypart carrying on past midnight is one timeline, so
  its last film may run on.
- **Half-hour alignment** (per channel, on by default): each slot ends on the
  clock's next half hour; short programmes that fit are packed in (never the
  show just aired) and the rest is a break, which clients fill with an "up
  next" card.
- **Regular slots** air whole episodes on the half hour, then the channel's
  own schedule resumes with a fresh programme.
- **Shows are spread through the running order** and play in series order —
  first episode, through to the last, and again.

Plans are memoised per built channel, so a week of guide is a few dictionary
lookups once the days exist. Details that stay load-bearing:

- **Runtimes are ticks, not seconds**, so nothing accumulates rounding error.
- **Intervals are half-open**: an instant on a boundary belongs to the
  programme starting there.
- **Before the channel's anchor nothing airs**; its first day begins at the
  anchor.
- **Zero-runtime items are dropped** when the snapshot is built.
- **Ordering does not use `Random`**, whose sequence is not stable across .NET
  versions; `DeterministicShuffle` spells out splitmix64, with a pinned vector
  in the tests.

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
