using GCS.Core.Domain;

namespace GCS.Core.Mission;

public interface IMissionService
{
    /// <summary>
    /// Fired when mission transfer state changes.
    /// </summary>
    event Action<MissionState>? MissionStateChanged;

    /// <summary>
    /// Upload mission items to the vehicle.
    /// </summary>
    Task UploadAsync(IReadOnlyList<MissionItem> items, CancellationToken ct);

    /// <summary>
    /// Download mission items from the vehicle.
    /// </summary>
    Task<IReadOnlyList<MissionItem>> DownloadAsync(CancellationToken ct);

    /// <summary>
    /// Called by handler when MISSION_REQUEST_INT is received (during upload).
    /// </summary>
    Task OnMissionRequest(ushort seq, CancellationToken ct);

    /// <summary>
    /// Called by handler when MISSION_COUNT is received (during download).
    /// </summary>
    Task OnMissionCount(ushort count, CancellationToken ct);

    /// <summary>
    /// Called by handler when MISSION_ITEM_INT is received (during download).
    /// </summary>
    Task OnMissionItem(MissionItem item, CancellationToken ct);
    Task ClearAsync(CancellationToken ct);
    /// <summary>
    /// Called by handler when MISSION_ACK is received.
    /// </summary>
    void OnMissionAck(byte result);
}