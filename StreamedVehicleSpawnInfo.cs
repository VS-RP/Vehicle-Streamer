using System;
using System.Numerics;
using SampSharp.Entities.SAMP;

namespace SampSharp.VehicleStreamer.Entities;

/// <summary>
/// Description of a vehicle to register with <see cref="IVehicleStreamerService"/>.
/// Mirrors the <see cref="VehicleSpawnInfo"/> shape for native open.mp vehicles
/// plus a few streamer-specific knobs (<see cref="StreamDistance"/>, <see cref="Tag"/>,
/// <see cref="OnSpawn"/>, <see cref="OnDespawn"/>).
/// </summary>
public sealed class StreamedVehicleSpawnInfo
{
    /// <summary>SA-MP vehicle model.</summary>
    public required VehicleModelType Model { get; init; }

    /// <summary>World anchor — used as the open.mp respawn point of every spawn cycle.</summary>
    public required Vector3 Position { get; init; }

    /// <summary>Anchor Z rotation in degrees.</summary>
    public required float ZRotation { get; init; }

    /// <summary>Primary colour ID, or -1 for random.</summary>
    public int PrimaryColor { get; init; } = -1;

    /// <summary>Secondary colour ID, or -1 for random.</summary>
    public int SecondaryColor { get; init; } = -1;

    /// <summary>Native respawn delay (the open.mp timer that returns the vehicle to its anchor).</summary>
    public TimeSpan RespawnDelay { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Whether to attach a siren on creation.</summary>
    public bool HasSiren { get; init; }

    /// <summary>Optional initial interior. Leave at 0 for the default world.</summary>
    public int Interior { get; init; }

    /// <summary>Virtual world. Use -1 to mean "any" — the streamer will then ignore observer VW filtering.</summary>
    public int VirtualWorld { get; init; }

    /// <summary>Optional initial number plate.</summary>
    public string? NumberPlate { get; init; }

    /// <summary>
    /// Stream-in distance in metres. Stream-out happens at
    /// <c>StreamDistance × <see cref="VehicleStreamerOptions.HysteresisFactor"/></c>.
    /// </summary>
    public float StreamDistance { get; init; } = 250f;

    /// <summary>
    /// Caller-controlled payload travelling with the record across spawn cycles.
    /// The library never reads this — use it to stash gamemode-side state
    /// (e.g. an analogue of <c>VsVehicle</c> data — fuel, owner, tuning ids).
    /// </summary>
    public object? Tag { get; set; }

    /// <summary>
    /// Fired immediately after the native vehicle is created. Use it to attach
    /// gamemode ECS components, restore consumer-side state, set a label, etc.
    /// The native is positioned/rotated/coloured/healed by the streamer itself
    /// before this callback runs — only consumer-side state needs handling here.
    /// </summary>
    public Action<StreamedVehicle, Vehicle>? OnSpawn { get; set; }

    /// <summary>
    /// Fired right before the native vehicle is destroyed. Use it to capture
    /// consumer-side state (e.g. fuel) into <see cref="StreamedVehicle.Tag"/>.
    /// Open.mp-native state (position, rotation, health, damage, tuning, plate,
    /// colours, parameters) is captured automatically — no need to copy it here.
    /// </summary>
    public Action<StreamedVehicle, Vehicle>? OnDespawn { get; set; }
}
