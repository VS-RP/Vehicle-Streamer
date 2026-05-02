# SampSharp.OpenMp.VehicleStreamer

Pure-managed (.NET 10) vehicle streamer for the SampSharp open.mp x64 host. Spawns
and despawns native open.mp vehicles on demand based on player proximity, so a
gamemode can keep more "logical" vehicles than the engine's hard limit
(typically ~2000 across the whole server).

Unlike [Incognito's streamer plugin](https://github.com/samp-incognito/samp-streamer-plugin)
this library has **no native component** and **no plugin dependency** — the grid
and tick loop run entirely in C# on top of `IWorldService`.

## Architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│  C# gamemode                                                         │
│     uses IVehicleStreamerService.Register / Tick / ForceSpawn        │
└──────────────────────────────────────────────────────────────────────┘
                               │   in-process (managed only)
                               ▼
┌──────────────────────────────────────────────────────────────────────┐
│  SampSharp.OpenMp.VehicleStreamer  (this assembly)                   │
│     XY grid · hysteresis · per-tick proximity scan                   │
│     state capture (pos / rot / health / damage / tuning / params)    │
└──────────────────────────────────────────────────────────────────────┘
                               │   IWorldService (CreateVehicle / etc)
                               ▼
┌──────────────────────────────────────────────────────────────────────┐
│  SampSharp.OpenMp.Entities                                           │
└──────────────────────────────────────────────────────────────────────┘
```

## Surface

- `IVehicleStreamerService.Register(StreamedVehicleSpawnInfo)` → `StreamedVehicle`
  - returns a handle that becomes "live" (with `Native` populated) once an
    observer enters the configured stream distance
- `Tick(IEnumerable<Player>)` — caller-driven; recommended cadence 1 Hz
- `ForceSpawn` / `ForceDespawn` — manual overrides for teleporting players
  straight into a streamed car or for explicit cleanup
- `StreamedVehicle.IsPinned` — opt out of despawning while keeping the record

`StreamedVehicle.Tag` is a free-form `object?` for carrying gamemode-side state
(fuel, owner, faction, etc.) across spawn cycles. The library never reads it.

`StreamedVehicleSpawnInfo` exposes `OnSpawn`/`OnDespawn` callbacks so the
consumer can attach ECS components, restore custom state, write/read its `Tag`
without the library knowing anything about gamemode types.

## Anchor semantics

`Register` records the position you pass in as the **anchor**. Every time the
streamer creates the native counterpart it does so via `CreateVehicle` at the
anchor — that becomes open.mp's respawn point. Captured `State.Position` is
restored immediately after by teleporting the freshly created native, so the
vehicle visually appears where you last left it but, when the engine respawns
it (driver leaves, /respawn etc.), it returns to the original anchor.

## Usage

```csharp
// in Startup.ConfigureServices
services.AddVehicleStreamer(opts =>
{
    opts.CellSize = 300f;
    opts.HysteresisFactor = 1.5f;
});

// create a record
var handle = streamer.Register(new StreamedVehicleSpawnInfo
{
    Model = VehicleModelType.Cheetah,
    Position = new Vector3(1665f, -2115f, 13.4f),
    ZRotation = 268f,
    PrimaryColor = 93,
    SecondaryColor = 0,
    StreamDistance = 250f,
    OnSpawn = (sv, vehicle) =>
    {
        // attach gamemode components, apply per-record fuel etc.
    },
});

// drive the streamer from your tick loop
foreach (var player in livePlayers) /* whatever */;
streamer.Tick(livePlayers);
```

## Dependencies

| Project                            | Reference type   |
|------------------------------------|------------------|
| `SampSharp.OpenMp.Core`            | ProjectReference |
| `SampSharp.OpenMp.Entities`        | ProjectReference |

No native dependency.

## License

Apache-2.0.
