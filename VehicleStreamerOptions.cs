namespace SampSharp.VehicleStreamer.Entities;

/// <summary>
/// Tuning knobs for <see cref="VehicleStreamerService"/>. Provided to DI as a
/// singleton via <see cref="VehicleStreamerEcsExtensions.AddVehicleStreamer(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Action{VehicleStreamerOptions})"/>.
/// </summary>
public sealed class VehicleStreamerOptions
{
    /// <summary>
    /// XY size of one grid cell, metres. Pick a value comparable to (or larger than)
    /// the typical <see cref="StreamedVehicleSpawnInfo.StreamDistance"/> — the
    /// streamer always inspects ⌈StreamDistance × HysteresisFactor / CellSize⌉ cells
    /// around every observer, so very small cells increase per-tick scan cost.
    /// </summary>
    public float CellSize { get; set; } = 250f;

    /// <summary>
    /// Stream-out distance is <c>StreamDistance × HysteresisFactor</c>. Setting
    /// this above 1.0 prevents thrashing when an observer sits on the boundary
    /// of a record's stream radius.
    /// </summary>
    public float HysteresisFactor { get; set; } = 1.5f;

    /// <summary>
    /// Minimum number of consecutive ticks a record must spend out-of-range
    /// (and unoccupied) before it can be despawned. With a 1 Hz tick a value of
    /// 2 means up to a 2-second grace period.
    /// </summary>
    public int DespawnTickGrace { get; set; } = 2;

    /// <summary>If true, occupied vehicles are exempt from despawning regardless of distance.</summary>
    public bool KeepOccupiedVehicles { get; set; } = true;

    /// <summary>
    /// Maximum number of native vehicles the streamer may spawn in a single
    /// <see cref="VehicleStreamerService.Tick"/>. Caps the burst of reliable
    /// CreateVehicle + state RPCs sent when an observer enters a dense area — an
    /// uncapped burst can exceed open.mp's per-client <c>acks_limit</c> (which
    /// disconnects and temporarily bans the player) and overload the SA-MP client.
    /// Records deferred this tick are spawned on subsequent ticks. <c>0</c>
    /// disables the cap. With the default 1 Hz tick, 30 ≈ 30 vehicles/second.
    /// </summary>
    public int MaxSpawnsPerTick { get; set; } = 30;
}
