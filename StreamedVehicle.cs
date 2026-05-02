using System;
using System.Numerics;
using SampSharp.Entities.SAMP;

namespace SampSharp.VehicleStreamer.Entities;

/// <summary>
/// Handle for a vehicle managed by <see cref="IVehicleStreamerService"/>. Lifetime
/// is independent of the underlying native: dormant when no observer is in range,
/// spawned (with <see cref="Native"/> populated) while at least one observer is.
/// </summary>
public sealed class StreamedVehicle
{
    private readonly IVehicleStreamerService _service;

    internal StreamedVehicle(IVehicleStreamerService service, StreamedVehicleSpawnInfo info)
    {
        _service = service;

        Model = info.Model;
        Anchor = info.Position;
        AnchorRotation = info.ZRotation;
        StreamDistance = info.StreamDistance;
        VirtualWorld = info.VirtualWorld;
        Interior = info.Interior;
        RespawnDelay = info.RespawnDelay;
        HasSiren = info.HasSiren;

        OnSpawn = info.OnSpawn;
        OnDespawn = info.OnDespawn;
        Tag = info.Tag;

        State = new VehicleStreamState
        {
            Position = info.Position,
            ZAngle = info.ZRotation,
            PrimaryColor = info.PrimaryColor,
            SecondaryColor = info.SecondaryColor,
            NumberPlate = info.NumberPlate,
        };
    }

    /// <summary>SA-MP vehicle model the record was created with.</summary>
    public VehicleModelType Model { get; }

    /// <summary>Original spawn position. Always passed to <c>CreateVehicle</c> so the
    /// open.mp engine respawns the vehicle here, regardless of where it was driven.</summary>
    public Vector3 Anchor { get; }

    /// <summary>Z rotation that pairs with <see cref="Anchor"/>.</summary>
    public float AnchorRotation { get; }

    /// <summary>Stream-in radius in metres. Mutable for runtime tweaks.</summary>
    public float StreamDistance { get; set; }

    /// <summary>Virtual world filter. -1 disables the filter (visible in any VW).</summary>
    public int VirtualWorld { get; set; }

    /// <summary>Interior to link to on every spawn. Zero for default.</summary>
    public int Interior { get; set; }

    /// <summary>Native respawn delay reapplied on every spawn.</summary>
    public TimeSpan RespawnDelay { get; set; }

    /// <summary>Whether to attach a siren when (re)spawning the native.</summary>
    public bool HasSiren { get; set; }

    /// <summary>If set, the streamer never despawns this record (still spawns it on demand).</summary>
    public bool IsPinned { get; set; }

    /// <summary>Captured open.mp-native state, restored on every spawn.</summary>
    public VehicleStreamState State { get; }

    /// <summary>Live native counterpart, or null while dormant.</summary>
    public Vehicle? Native { get; internal set; }

    /// <summary>True when a live native is present and alive.</summary>
    public bool IsLive => Native is { IsComponentAlive: true };

    /// <summary>Free-form payload for the consumer. Library never reads it.</summary>
    public object? Tag { get; set; }

    /// <summary>See <see cref="StreamedVehicleSpawnInfo.OnSpawn"/>.</summary>
    public Action<StreamedVehicle, Vehicle>? OnSpawn { get; set; }

    /// <summary>See <see cref="StreamedVehicleSpawnInfo.OnDespawn"/>.</summary>
    public Action<StreamedVehicle, Vehicle>? OnDespawn { get; set; }

    /// <summary>Cell coordinates the record is currently bucketed into.</summary>
    internal (int X, int Y) Cell { get; set; }

    /// <summary>
    /// Earliest tick at which the streamer is allowed to despawn this record.
    /// Bumped every time an observer touches it; used to implement grace periods.
    /// </summary>
    internal long EarliestDespawnTick { get; set; }

    /// <summary>Convenience: equivalent to <c>service.Unregister(this)</c>.</summary>
    public bool Unregister() => _service.Unregister(this);

    /// <inheritdoc />
    public override string ToString() => $"StreamedVehicle(Model={Model}, Anchor={Anchor}, Live={IsLive})";
}
