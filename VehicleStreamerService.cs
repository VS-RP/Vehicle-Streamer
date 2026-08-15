using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SampSharp.Entities.SAMP;

namespace SampSharp.VehicleStreamer.Entities;

/// <summary>
/// Default <see cref="IVehicleStreamerService"/> implementation. Maintains a 2-D XY
/// grid of records and, on each <see cref="Tick"/>, spawns/despawns native
/// <see cref="Vehicle"/> instances based on observer proximity.
/// </summary>
public sealed class VehicleStreamerService : IVehicleStreamerService
{
    private readonly IWorldService _world;
    private readonly VehicleStreamerOptions _options;

    private readonly List<StreamedVehicle> _all = new();
    private readonly Dictionary<(int X, int Y), List<StreamedVehicle>> _grid = new();
    private float _maxStreamDistance;
    private long _tick;

    /// <summary>
    /// Initializes a new instance of <see cref="VehicleStreamerService"/>.
    /// </summary>
    /// <param name="world">World service used to create native open.mp vehicles.</param>
    /// <param name="options">Tuning knobs (cell size, hysteresis, grace period).</param>
    public VehicleStreamerService(IWorldService world, VehicleStreamerOptions options)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(options);

        _world = world;
        _options = options;
    }

    /// <inheritdoc />
    public int Count => _all.Count;

    /// <inheritdoc />
    public int LiveCount
    {
        get
        {
            var n = 0;
            // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
            foreach (var v in _all)
                if (v.IsLive) n++;
            return n;
        }
    }

    /// <inheritdoc />
    public StreamedVehicle Register(StreamedVehicleSpawnInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.StreamDistance <= 0)
            throw new ArgumentException("StreamDistance must be > 0.", nameof(info));

        var vehicle = new StreamedVehicle(this, info);
        _all.Add(vehicle);
        Bucket(vehicle);
        if (info.StreamDistance > _maxStreamDistance)
            _maxStreamDistance = info.StreamDistance;
        return vehicle;
    }

    /// <inheritdoc />
    public bool Unregister(StreamedVehicle vehicle)
    {
        if (!_all.Remove(vehicle)) return false;
        Unbucket(vehicle);
        if (vehicle.IsLive) DestroyNative(vehicle);
        return true;
    }

    /// <inheritdoc />
    public IEnumerable<StreamedVehicle> All() => _all;

    /// <inheritdoc />
    public Vehicle? ForceSpawn(StreamedVehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return vehicle.IsLive ? vehicle.Native : SpawnNative(vehicle);
    }

    /// <inheritdoc />
    public bool ForceDespawn(StreamedVehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        if (!vehicle.IsLive) return false;
        DestroyNative(vehicle);
        return true;
    }

    /// <inheritdoc />
    public void Tick(IEnumerable<Player> players)
    {
        _tick++;

        // Drop dangling natives — open.mp may have destroyed them under us
        // (admin /destroy, sudden Vehicle.Respawn, etc.). Re-bucket lives that drove
        // off into a different cell so observer scans pick them up correctly.
        ReconcileLiveRecords();

        var observers = players?.Where(static p => p is { IsComponentAlive: true }).ToArray()
                        ?? [];

        var hysteresis = _options.HysteresisFactor < 1f ? 1f : _options.HysteresisFactor;
        var maxOutDistance = _maxStreamDistance * hysteresis;
        var cellRadius = _options.CellSize <= 0
            ? 1
            : (int)MathF.Ceiling(maxOutDistance / _options.CellSize);

        var touched = new HashSet<StreamedVehicle>();
        var spawnedThisTick = 0;

        foreach (var observer in observers)
        {
            var pos = observer.Position;
            var pvw = observer.VirtualWorld;
            var (cx, cy) = ToCell(pos);

            for (var dx = -cellRadius; dx <= cellRadius; dx++)
            for (var dy = -cellRadius; dy <= cellRadius; dy++)
            {
                if (!_grid.TryGetValue((cx + dx, cy + dy), out var bucket)) continue;

                foreach (var v in bucket)
                {
                    if (v.VirtualWorld >= 0 && v.VirtualWorld != pvw) continue;

                    var inDistance = v.StreamDistance;
                    var outDistance = inDistance * hysteresis;

                    var anchor = v.IsLive ? v.Native!.Position : v.State.Position;
                    var distSq = Vector3.DistanceSquared(pos, anchor);

                    if (distSq <= inDistance * inDistance)
                    {
                        if (!v.IsLive)
                        {
                            // Throttle spawns per tick: entering a dense area would
                            // otherwise fire a burst of reliable CreateVehicle + state
                            // RPCs at once, tripping open.mp's per-client acks_limit
                            // (disconnect + temp-ban) and crashing the client. Records
                            // skipped this tick are picked up on subsequent ticks.
                            if (_options.MaxSpawnsPerTick > 0 && spawnedThisTick >= _options.MaxSpawnsPerTick)
                                continue;
                            SpawnNative(v);
                            spawnedThisTick++;
                        }

                        touched.Add(v);
                        v.EarliestDespawnTick = _tick + _options.DespawnTickGrace;
                    }
                    else if (v.IsLive && distSq <= outDistance * outDistance)
                    {
                        // Hysteresis band: keep the live native, defer despawn.
                        touched.Add(v);
                        v.EarliestDespawnTick = _tick + _options.DespawnTickGrace;
                    }
                }
            }
        }

        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
        foreach (var v in _all)
        {
            if (!v.IsLive) continue;
            if (v.IsPinned) continue;
            if (touched.Contains(v)) continue;
            if (_tick < v.EarliestDespawnTick) continue;

            if (_options.KeepOccupiedVehicles && IsOccupied(v.Native!))
            {
                v.EarliestDespawnTick = _tick + _options.DespawnTickGrace;
                continue;
            }

            DestroyNative(v);
        }
    }

    private void ReconcileLiveRecords()
    {
        foreach (var v in _all)
        {
            if (v.Native is null) continue;

            if (!v.Native.IsComponentAlive)
            {
                // Native disappeared without our involvement — forget it; the next
                // observer scan will respawn at the anchor with our captured state.
                v.Native = null;
                continue;
            }

            // Re-bucket if the live vehicle drove into another cell.
            var freshCell = ToCell(v.Native.Position);
            if (freshCell == v.Cell) continue;
            Unbucket(v);
            v.Cell = freshCell;
            BucketAt(v, freshCell);
        }
    }

    private Vehicle SpawnNative(StreamedVehicle v)
    {
        // Always create at the anchor: open.mp uses the position passed to
        // CreateVehicle as its respawn point. If state.Position differs, teleport
        // immediately after creation — the engine still respawns to the anchor.
        var respawnSeconds = (int)Math.Round(v.RespawnDelay.TotalSeconds);
        if (respawnSeconds < 0) respawnSeconds = -1;

        var native = _world.CreateVehicle(
            v.Model,
            v.Anchor,
            v.AnchorRotation,
            v.State.PrimaryColor,
            v.State.SecondaryColor,
            respawnSeconds,
            v.HasSiren);

        // Apply captured state. Skip the no-op cases on first spawn so we don't
        // waste calls when the record is fresh.
        if (v.Interior > 0)
            native.LinkToInterior(v.Interior);
        if (v.VirtualWorld >= 0)
            native.VirtualWorld = v.VirtualWorld;

        if (v.State.Position != v.Anchor)
            native.Position = v.State.Position;
        if (MathF.Abs(v.State.ZAngle - v.AnchorRotation) > 0.001f)
            native.Angle = v.State.ZAngle;

        if (v.State.Health > 0 && MathF.Abs(v.State.Health - native.Health) > 0.5f)
            native.Health = v.State.Health;

        if (!string.IsNullOrEmpty(v.State.NumberPlate))
            native.SetNumberPlate(v.State.NumberPlate);

        if ((v.State.DamagePanels | v.State.DamageDoors | v.State.DamageLights | v.State.DamageTires) != 0)
            native.UpdateDamageStatus(
                v.State.DamagePanels,
                v.State.DamageDoors,
                v.State.DamageLights,
                v.State.DamageTires);

        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
        foreach (var component in v.State.Components)
            if (component > 0) native.AddComponent(component);

        if (v.State.Engine) native.Engine = true;
        if (v.State.LightsOn) native.Lights = true;
        if (v.State.DoorsLocked) native.Doors = true;
        if (v.State.BootOpen) native.Boot = true;
        if (v.State.BonnetOpen) native.Bonnet = true;

        v.Native = native;

        try { v.OnSpawn?.Invoke(v, native); }
        catch { /* surfaced via consumer logging — don't kill the streamer tick */ }

        // The vehicle may have been driven before destroy; ensure its grid bucket
        // reflects the captured position, not the anchor.
        var freshCell = ToCell(v.State.Position);
        if (freshCell == v.Cell) return native;
        Unbucket(v);
        v.Cell = freshCell;
        BucketAt(v, freshCell);

        return native;
    }

    private void DestroyNative(StreamedVehicle v)
    {
        var native = v.Native;
        if (native is null) return;

        try { v.OnDespawn?.Invoke(v, native); }
        catch
        {
            // ignored
        }

        if (native.IsComponentAlive)
            CaptureState(v, native);

        v.Native = null;

        if (native.IsComponentAlive)
            native.DestroyEntity();

        // Snap the bucket to the captured position so the next observer scan
        // checks the right cell — important if the vehicle drove far away.
        var freshCell = ToCell(v.State.Position);
        if (freshCell == v.Cell) return;
        Unbucket(v);
        v.Cell = freshCell;
        BucketAt(v, freshCell);
    }

    private static void CaptureState(StreamedVehicle v, Vehicle native)
    {
        v.State.Position = native.Position;
        v.State.ZAngle = native.Angle;
        v.State.Health = native.Health;

        native.GetDamageStatus(out var p, out var d, out var l, out var t);
        v.State.DamagePanels = p;
        v.State.DamageDoors = d;
        v.State.DamageLights = l;
        v.State.DamageTires = t;

        var (c1, c2) = native.Colors;
        v.State.PrimaryColor = c1;
        v.State.SecondaryColor = c2;

        var plate = native.NumberPlate;
        if (!string.IsNullOrEmpty(plate))
            v.State.NumberPlate = plate;

        v.State.Engine = native.Engine;
        v.State.LightsOn = native.Lights;
        v.State.DoorsLocked = native.Doors;
        v.State.BootOpen = native.Boot;
        v.State.BonnetOpen = native.Bonnet;

        v.State.Components.Clear();
        // CarModType has 14 documented slots (Spoiler..VentLeft, 0..13). Only
        // non-zero IDs represent installed components.
        for (var slot = 0; slot <= (int)CarModType.VentLeft; slot++)
        {
            int id;
            try { id = native.GetComponentInSlot((CarModType)slot); }
            catch { continue; }
            if (id != 0) v.State.Components.Add(id);
        }
    }

    private static bool IsOccupied(Vehicle v)
    {
        if (!v.IsComponentAlive) return false;
        if (v.Driver is { IsComponentAlive: true }) return true;
        foreach (var p in v.GetPassengers())
            if (p is { IsComponentAlive: true })
                return true;
        return false;
    }

    private (int X, int Y) ToCell(Vector3 p)
    {
        var sz = _options.CellSize;
        if (sz <= 0) sz = 250f;
        return ((int)MathF.Floor(p.X / sz), (int)MathF.Floor(p.Y / sz));
    }

    private void Bucket(StreamedVehicle v)
    {
        var cell = ToCell(v.State.Position);
        v.Cell = cell;
        BucketAt(v, cell);
    }

    private void BucketAt(StreamedVehicle v, (int X, int Y) cell)
    {
        if (!_grid.TryGetValue(cell, out var list))
            _grid[cell] = list = [];
        list.Add(v);
    }

    private void Unbucket(StreamedVehicle v)
    {
        if (!_grid.TryGetValue(v.Cell, out var list)) return;
        list.Remove(v);
        if (list.Count == 0) _grid.Remove(v.Cell);
    }
}
