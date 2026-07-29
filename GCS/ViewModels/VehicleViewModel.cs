using System;
using System.Threading;
using GCS.Core.Domain;
using GCS.Core.Mavlink;
using GCS.Core.State;

namespace GCS.ViewModels;

// Single definition lives in Core alongside the rules that produce it.
using VehicleAlertLevel = GCS.Core.Swarm.VehicleAlertLevel;

/// <summary>
/// One drone in the swarm. Owns a <see cref="VehicleStateStore"/> filtered to its
/// own system id, so telemetry from other vehicles on the shared link is ignored.
/// </summary>
public sealed class VehicleViewModel : ViewModelBase, IDisposable
{
    private readonly VehicleStateStore _store;
    private bool _disposed;

    public byte SystemId { get; }
    public string Name => $"UAV {SystemId}";

    public VehicleViewModel(IMavlinkBackend backend, byte systemId, SynchronizationContext? context)
    {
        SystemId = systemId;
        _store = new VehicleStateStore(backend, context, systemId);
        _store.StateChanged += OnStateChanged;
    }

    /// <summary>Latest merged state for this vehicle.</summary>
    public VehicleState State { get; private set; } = new(null, null, null, null, null, null, null);

    private bool _isLeader;
    /// <summary>Marks the formation leader (followers hold an offset from it).</summary>
    public bool IsLeader
    {
        get => _isLeader;
        set
        {
            if (!SetProperty(ref _isLeader, value)) return;
            OnPropertyChanged(nameof(RoleText));
            OnPropertyChanged(nameof(StationText));
        }
    }

    public string RoleText => IsLeader ? "LEADER" : "FOLLOWER";

    private GCS.Core.Swarm.FormationOffset? _station;
    /// <summary>Assigned formation station, in the leader's body frame.</summary>
    public GCS.Core.Swarm.FormationOffset? Station
    {
        get => _station;
        set { if (SetProperty(ref _station, value)) OnPropertyChanged(nameof(StationText)); }
    }

    /// <summary>Human-readable station, e.g. "35 aft · 35 right · 5 below".</summary>
    public string StationText
    {
        get
        {
            if (IsLeader) return "—";
            if (_station is not { } s) return "";
            var parts = new System.Collections.Generic.List<string>();
            if (Math.Abs(s.Forward) >= 0.5)
                parts.Add($"{Math.Abs(s.Forward):F0} {(s.Forward < 0 ? "aft" : "fwd")}");
            if (Math.Abs(s.Right) >= 0.5)
                parts.Add($"{Math.Abs(s.Right):F0} {(s.Right < 0 ? "left" : "right")}");
            if (Math.Abs(s.Down) >= 0.5)
                parts.Add($"{Math.Abs(s.Down):F0} {(s.Down > 0 ? "below" : "above")}");
            return parts.Count == 0 ? "on leader" : string.Join(" · ", parts);
        }
    }

    private bool _isActive;
    /// <summary>True for the vehicle the single-vehicle screens are bound to.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public DateTime LastUpdateUtc { get; private set; } = DateTime.UtcNow;

    // ── Per-vehicle health ───────────────────────────────────────────
    // The alert engine only ever watched the active vehicle, so a follower in
    // trouble raised nothing. Every vehicle now assesses itself, using the
    // rules in GCS.Core.Swarm.VehicleHealthEvaluator (which are unit-tested).
    private VehicleAlertLevel _alertLevel;
    public VehicleAlertLevel AlertLevel
    {
        get => _alertLevel;
        private set
        {
            if (!SetProperty(ref _alertLevel, value)) return;
            OnPropertyChanged(nameof(HasAlert));
            OnPropertyChanged(nameof(IsCritical));
        }
    }

    private string _alertText = "";
    public string AlertText
    {
        get => _alertText;
        private set => SetProperty(ref _alertText, value);
    }

    public bool HasAlert => _alertLevel != VehicleAlertLevel.None;
    public bool IsCritical => _alertLevel == VehicleAlertLevel.Critical;

    /// <summary>
    /// Re-assess this vehicle's health. Called on a fleet-wide tick so staleness
    /// is noticed even when a vehicle has stopped sending anything.
    /// </summary>
    public void EvaluateHealth(DateTime nowUtc)
    {
        var result = GCS.Core.Swarm.VehicleHealthEvaluator.Evaluate(
            secondsSinceUpdate: (nowUtc - LastUpdateUtc).TotalSeconds,
            batteryPercent: BatteryPercent,
            hasBattery: State.Battery != null,
            hasGps: State.Gps != null,
            hasGpsFix: State.Gps?.HasFix ?? false,
            isArmed: IsArmed);

        AlertLevel = result.Level;
        AlertText = result.Text;
    }

    // ── Bindable telemetry ───────────────────────────────────────────
    public double Latitude => State.Position?.LatitudeDeg ?? 0;
    public double Longitude => State.Position?.LongitudeDeg ?? 0;
    public double AltitudeRel => State.Position?.AltitudeRelMeters ?? 0;
    public double Heading => State.Position?.HeadingDeg ?? State.VfrHud?.HeadingDeg ?? 0;
    public double GroundSpeed => State.VfrHud?.GroundspeedMps ?? 0;
    public double AirSpeed => State.VfrHud?.AirspeedMps ?? 0;

    public double RollDeg => (State.Attitude?.RollRad ?? 0) * 180.0 / Math.PI;
    public double PitchDeg => (State.Attitude?.PitchRad ?? 0) * 180.0 / Math.PI;
    public double YawDeg => (State.Attitude?.YawRad ?? 0) * 180.0 / Math.PI;

    public double Voltage => State.Battery?.VoltageVolts ?? 0;
    public int BatteryPercent => State.Battery?.RemainingPercent ?? 0;

    public bool IsArmed => State.IsArmed;
    public string ArmedText => IsArmed ? "ARMED" : "DISARMED";
    public string FlightMode => State.FlightMode?.ToString().ToUpperInvariant() ?? "UNKNOWN";

    public string GpsFix => State.Gps?.FixTypeString ?? "NO GPS";
    public int Satellites => State.Gps?.SatellitesVisible ?? 0;
    public bool HasPosition => State.Position != null;

    private void OnStateChanged(VehicleState state)
    {
        State = state;
        LastUpdateUtc = DateTime.UtcNow;

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Latitude));
        OnPropertyChanged(nameof(Longitude));
        OnPropertyChanged(nameof(AltitudeRel));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(GroundSpeed));
        OnPropertyChanged(nameof(AirSpeed));
        OnPropertyChanged(nameof(RollDeg));
        OnPropertyChanged(nameof(PitchDeg));
        OnPropertyChanged(nameof(YawDeg));
        OnPropertyChanged(nameof(Voltage));
        OnPropertyChanged(nameof(BatteryPercent));
        OnPropertyChanged(nameof(IsArmed));
        OnPropertyChanged(nameof(ArmedText));
        OnPropertyChanged(nameof(FlightMode));
        OnPropertyChanged(nameof(GpsFix));
        OnPropertyChanged(nameof(Satellites));
        OnPropertyChanged(nameof(HasPosition));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.StateChanged -= OnStateChanged;
        _store.Dispose();
    }
}
