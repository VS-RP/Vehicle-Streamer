using System.Collections.Generic;
using SampSharp.Entities.SAMP;

namespace SampSharp.VehicleStreamer.Entities;

/// <summary>
/// Pure-managed vehicle streamer. Tracks dormant spawn records and creates/destroys
/// native open.mp vehicles based on player proximity. Independent of the
/// streamer plugin — runs its own XY grid + per-tick scan in C#.
///
/// Consumers drive the lifecycle by calling <see cref="Tick"/> on a periodic schedule
/// (1–5 seconds typical). The library does not start its own timer — that decision
/// lives with the host gamemode so it can synchronise with the open.mp main thread.
/// </summary>
public interface IVehicleStreamerService
{
    /// <summary>Total registered records (live + dormant).</summary>
    int Count { get; }

    /// <summary>Number of records with a live native counterpart.</summary>
    int LiveCount { get; }

    /// <summary>
    /// Register a new dormant record. The native <see cref="Vehicle"/> is created
    /// the first time a player enters the configured stream distance.
    /// </summary>
    StreamedVehicle Register(StreamedVehicleSpawnInfo info);

    /// <summary>
    /// Remove a record. Destroys the live <see cref="Vehicle"/> if present.
    /// Returns false if the record was not tracked.
    /// </summary>
    bool Unregister(StreamedVehicle vehicle);

    /// <summary>
    /// Enumerate every registered record, live or dormant. The result is a snapshot,
    /// so calling <see cref="Unregister"/> while iterating it is safe.
    /// </summary>
    IEnumerable<StreamedVehicle> All();

    /// <summary>
    /// Drive the streamer: rebucket live records that drifted, spawn records within
    /// stream distance of any observer, despawn untouched records past their grace.
    /// </summary>
    /// <param name="players">Observers — typically every connected, non-NPC player.</param>
    void Tick(IEnumerable<Player> players);

    /// <summary>
    /// Force-spawn a dormant record (useful when a player is about to be teleported
    /// straight into the vehicle and you cannot wait for the next tick).
    /// </summary>
    Vehicle? ForceSpawn(StreamedVehicle vehicle);

    /// <summary>Force-despawn a live record without unregistering it. No-op if dormant.</summary>
    bool ForceDespawn(StreamedVehicle vehicle);
}
