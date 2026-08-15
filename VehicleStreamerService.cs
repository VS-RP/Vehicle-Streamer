using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<VehicleStreamerService>? _logger;

    private readonly List<StreamedVehicle> _all = new();
    private readonly Dictionary<(int X, int Y), List<StreamedVehicle>> _grid = new();

    // Per-tick scratch, kept as fields so a 1 Hz tick doesn't allocate three
    // collections every time. Cleared at the point of use, never read across ticks.
    private readonly HashSet<StreamedVehicle> _touched = new();
    private readonly Dictionary<StreamedVehicle, float> _pending = new();
    private readonly List<KeyValuePair<StreamedVehicle, float>> _spawnOrder = new();

    private float _maxStreamDistance;
    private long _tick;

    /// <summary>
    /// Initializes a new instance of <see cref="VehicleStreamerService"/>.
    /// </summary>
    /// <param name="world">World service used to create native open.mp vehicles.</param>
    /// <param name="options">Tuning knobs (cell size, hysteresis, grace period).</param>
    /// <param name="logger">
    /// Optional. Receives the exceptions the streamer has to swallow to keep ticking:
    /// throwing consumer callbacks and records that fail to spawn or despawn. Without
    /// it those failures are invisible. Resolved from DI when logging is registered.
    /// </param>
    public VehicleStreamerService(IWorldService world, VehicleStreamerOptions options,
        ILogger<VehicleStreamerService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(options);

        _world = world;
        _options = options;
        _logger = logger;
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

        // _maxStreamDistance is not touched here — ReconcileRecords recomputes it at
        // the top of every tick, which is the only place that can also see it shrink.
        var vehicle = new StreamedVehicle(this, info);
        _all.Add(vehicle);
        Bucket(vehicle);
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
    public IEnumerable<StreamedVehicle> All() => _all.ToArray();

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
        // off into a different cell so observer scans pick them up correctly, and
        // refresh the scan radius.
        ReconcileRecords();

        var observers = players?.Where(static p => p is { IsComponentAlive: true }).ToArray()
                        ?? [];

        var hysteresis = _options.HysteresisFactor < 1f ? 1f : _options.HysteresisFactor;
        var maxOutDistance = _maxStreamDistance * hysteresis;
        var cellRadius = _options.CellSize <= 0
            ? 1
            : (int)MathF.Ceiling(maxOutDistance / _options.CellSize);

        _touched.Clear();
        _pending.Clear();

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
                        if (v.IsLive)
                        {
                            _touched.Add(v);
                            v.EarliestDespawnTick = _tick + _options.DespawnTickGrace;
                        }
                        else
                        {
                            // Collect rather than spawn here: SpawnNative may re-bucket
                            // the record, and that mutates the very grid list this loop
                            // is enumerating. Remember the closest observer's distance
                            // so a capped tick can spend its budget on the nearest.
                            if (!_pending.TryGetValue(v, out var best) || distSq < best)
                                _pending[v] = distSq;
                        }
                    }
                    else if (v.IsLive && distSq <= outDistance * outDistance)
                    {
                        // Hysteresis band: keep the live native, defer despawn.
                        _touched.Add(v);
                        v.EarliestDespawnTick = _tick + _options.DespawnTickGrace;
                    }
                }
            }
        }

        SpawnPending();

        var despawnBudget = _options.MaxDespawnsPerTick;
        var despawned = 0;

        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
        foreach (var v in _all)
        {
            if (!v.IsLive) continue;
            if (v.IsPinned) continue;
            if (_touched.Contains(v)) continue;
            if (_tick < v.EarliestDespawnTick) continue;
            if (despawnBudget > 0 && despawned >= despawnBudget) break;

            try
            {
                if (_options.KeepOccupiedVehicles && IsOccupied(v.Native!))
                {
                    v.EarliestDespawnTick = _tick + _options.DespawnTickGrace;
                    continue;
                }

                DestroyNative(v);
                despawned++;
            }
            catch (Exception e)
            {
                // One unusable record must not cost the rest of the tick. Leave the
                // record live; the next tick will try again.
                _logger?.LogError(e, "Vehicle streamer failed to despawn {Record}", v);
            }
        }
    }

    /// <summary>
    /// Phase two of <see cref="Tick"/>: create the natives the grid scan asked for.
    /// Deliberately runs after the scan — <see cref="SpawnNative"/> can re-bucket a
    /// record, which mutates a grid list and would break the scan's enumeration.
    /// </summary>
    private void SpawnPending()
    {
        if (_pending.Count == 0) return;

        _spawnOrder.Clear();
        foreach (var pending in _pending)
            _spawnOrder.Add(pending);

        var budget = _options.MaxSpawnsPerTick;
        if (budget > 0 && _spawnOrder.Count > budget)
        {
            // Over budget: spawn the nearest first, since those are what observers
            // are most likely to be looking at. The rest are retried next tick.
            _spawnOrder.Sort(static (a, b) => a.Value.CompareTo(b.Value));
            _spawnOrder.RemoveRange(budget, _spawnOrder.Count - budget);
        }

        foreach (var (v, _) in _spawnOrder)
        {
            // A consumer's OnSpawn may have force-spawned this record already.
            if (v.IsLive) continue;

            try
            {
                SpawnNative(v);
            }
            catch (Exception e)
            {
                // One unusable record must not cost the rest of the tick.
                _logger?.LogError(e, "Vehicle streamer failed to spawn {Record}", v);
                continue;
            }

            _touched.Add(v);
            v.EarliestDespawnTick = _tick + _options.DespawnTickGrace;
        }
    }

    /// <summary>
    /// Start-of-tick pass over every record. Drops natives open.mp destroyed behind
    /// our back, keeps grid buckets in step with where vehicles actually are, and
    /// recomputes <c>_maxStreamDistance</c>, which sets the scan radius: deriving it
    /// incrementally would only ever let it grow, since both <see cref="Unregister"/>
    /// and a runtime <see cref="StreamedVehicle.StreamDistance"/> change can lower it,
    /// and a single stale long-range record inflates the scan by (2r+1)² cells.
    /// </summary>
    private void ReconcileRecords()
    {
        _maxStreamDistance = 0f;

        foreach (var v in _all)
        {
            if (v.StreamDistance > _maxStreamDistance)
                _maxStreamDistance = v.StreamDistance;

            if (v.Native is null) continue;

            if (!v.Native.IsComponentAlive)
            {
                // Native disappeared without our involvement — forget it; the next
                // observer scan will respawn at the anchor with our captured state.
                // Snap the bucket back onto that captured state: the record may have
                // been re-bucketed to wherever it was last driven, and leaving Cell
                // out of sync with State.Position both hides it from scans near its
                // real position and desyncs the grid.
                v.Native = null;
                Rebucket(v, ToCell(v.State.Position));
                continue;
            }

            // Re-bucket if the live vehicle drove into another cell.
            Rebucket(v, ToCell(v.Native.Position));
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
            native.Interior = v.Interior;
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
        catch (Exception e)
        {
            // A throwing consumer callback must not kill the tick, but swallowing it
            // silently made gamemode bugs invisible — the native is already live and
            // the streamer carries on regardless of what the callback did.
            _logger?.LogError(e, "OnSpawn callback threw for {Record}", v);
        }

        // The vehicle may have been driven before destroy; ensure its grid bucket
        // reflects the captured position, not the anchor.
        Rebucket(v, ToCell(v.State.Position));

        return native;
    }

    private void DestroyNative(StreamedVehicle v)
    {
        var native = v.Native;
        if (native is null) return;

        try { v.OnDespawn?.Invoke(v, native); }
        catch (Exception e)
        {
            // Same as OnSpawn: the despawn proceeds either way, but the consumer's
            // failure to capture its own state is worth knowing about.
            _logger?.LogError(e, "OnDespawn callback threw for {Record}", v);
        }

        if (native.IsComponentAlive)
            CaptureState(v, native);

        v.Native = null;

        if (native.IsComponentAlive)
            native.DestroyEntity();

        // Snap the bucket to the captured position so the next observer scan
        // checks the right cell — important if the vehicle drove far away.
        Rebucket(v, ToCell(v.State.Position));
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

    /// <summary>
    /// Moves a record into <paramref name="cell"/>, no-op when it is already there.
    /// Mutates <c>_grid</c>, so it must never run while a grid bucket is being
    /// enumerated — see <see cref="SpawnPending"/>.
    /// </summary>
    private void Rebucket(StreamedVehicle v, (int X, int Y) cell)
    {
        if (cell == v.Cell) return;
        Unbucket(v);
        v.Cell = cell;
        BucketAt(v, cell);
    }
}
