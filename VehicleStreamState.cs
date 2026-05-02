using System.Collections.Generic;
using System.Numerics;

namespace SampSharp.VehicleStreamer.Entities;

/// <summary>
/// Snapshot of open.mp-native vehicle state preserved across stream cycles.
/// On every despawn the streamer copies the live vehicle's values into this object;
/// on every (re)spawn the streamer applies the values back to the new native.
///
/// Consumers may mutate this between despawn and the next spawn (e.g. to adjust
/// number plate or repair the vehicle) — the change takes effect on the next stream-in.
/// </summary>
public sealed class VehicleStreamState
{
    /// <summary>World position the native should be teleported to right after creation.</summary>
    public Vector3 Position { get; set; }

    /// <summary>Z rotation in degrees applied right after creation.</summary>
    public float ZAngle { get; set; }

    /// <summary>Current health. Defaults to 1000 (full) on first registration.</summary>
    public float Health { get; set; } = 1000f;

    /// <summary>Bitfield of panel damage (open.mp damage status). Re-applied via UpdateDamageStatus.</summary>
    public int DamagePanels { get; set; }

    /// <summary>Bitfield of door damage (open.mp damage status). Re-applied via UpdateDamageStatus.</summary>
    public int DamageDoors { get; set; }

    /// <summary>Bitfield of light damage (open.mp damage status). Re-applied via UpdateDamageStatus.</summary>
    public int DamageLights { get; set; }

    /// <summary>Bitfield of tire damage (open.mp damage status). Re-applied via UpdateDamageStatus.</summary>
    public int DamageTires { get; set; }

    /// <summary>Primary colour. -1 means "leave the value chosen at create-time alone".</summary>
    public int PrimaryColor { get; set; } = -1;

    /// <summary>Secondary colour. -1 means "leave the value chosen at create-time alone".</summary>
    public int SecondaryColor { get; set; } = -1;

    /// <summary>Custom number plate or null to keep the engine default.</summary>
    public string? NumberPlate { get; set; }

    /// <summary>Engine on/off, restored after spawn.</summary>
    public bool Engine { get; set; }

    /// <summary>Headlights on/off, restored after spawn.</summary>
    public bool LightsOn { get; set; }

    /// <summary>Whether the doors are locked (Vehicle.Doors), restored after spawn.</summary>
    public bool DoorsLocked { get; set; }

    /// <summary>Whether the boot/trunk is open, restored after spawn.</summary>
    public bool BootOpen { get; set; }

    /// <summary>Whether the bonnet/hood is open, restored after spawn.</summary>
    public bool BonnetOpen { get; set; }

    /// <summary>Vehicle component (mod) IDs currently fitted. Re-applied via AddComponent on spawn.</summary>
    public List<int> Components { get; } = [];
}
