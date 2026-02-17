using GCS.Core.Domain;
using GCS.Core.Mavlink;
using GCS.Core.Mavlink.Tx;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GCS.Core.Mission;

public sealed class MissionService : IMissionService
{
    private readonly IMavlinkSender _sender;
    private readonly IMavlinkBackend _backend;

    // Download state
    private List<MissionItem>? _downloaded;
    private ushort _expectedCount;
    private TaskCompletionSource<IReadOnlyList<MissionItem>>? _downloadTcs;
    private bool _isDownloading = false;

    // Upload state
    private IReadOnlyList<MissionItem>? _uploadItems;
    private bool _isUploading = false;
    private int _lastUploadedSeq = -1;
    private byte _seq;

    public event Action<MissionState>? MissionStateChanged;

    public MissionService(IMavlinkSender sender, IMavlinkBackend backend)
    {
        _sender = sender;
        _backend = backend;
    }

    public async Task UploadAsync(IReadOnlyList<MissionItem> items, CancellationToken ct)
    {
        // Cancel any ongoing download
        _isDownloading = false;
        _downloadTcs?.TrySetCanceled();
        _downloadTcs = null;

        var sys = _backend.SystemId;
        var comp = _backend.ComponentId;

        Debug.WriteLine($"[MissionService] Starting upload of {items.Count} items to {sys}:{comp}");

        _uploadItems = items;
        _isUploading = true;
        _lastUploadedSeq = -1;
        _seq = 0;

        MissionStateChanged?.Invoke(
            new MissionState(MissionTransferState.Uploading, 0, items.Count, null));

        var clearPacket = MissionClearAllCommand.Create(sys, comp, ref _seq);
        await _sender.SendAsync(clearPacket, ct);

        await Task.Delay(200, ct);

        var countPacket = MissionCountCommand.Create(sys, comp, (ushort)items.Count, ref _seq);
        await _sender.SendAsync(countPacket, ct);

        Debug.WriteLine($"[MissionService] Sent MISSION_COUNT={items.Count}");
    }

    public async Task OnMissionRequest(ushort seq, CancellationToken ct)
    {
        if (!_isUploading || _uploadItems == null)
        {
            Debug.WriteLine($"[MissionService] Ignoring request seq={seq} - not uploading");
            return;
        }

        if (seq >= _uploadItems.Count)
        {
            Debug.WriteLine($"[MissionService] ERROR: seq {seq} >= count {_uploadItems.Count}");
            return;
        }

        var sys = _backend.SystemId;
        var comp = _backend.ComponentId;
        var item = _uploadItems[seq];

        Debug.WriteLine($"[MissionService] Sending item {seq}/{_uploadItems.Count}: cmd={item.Command}");

        var packet = MissionItemIntCommand.Create(item, sys, comp, ref _seq);
        await _sender.SendAsync(packet, ct);

        _lastUploadedSeq = seq;

        MissionStateChanged?.Invoke(
            new MissionState(MissionTransferState.Uploading, seq + 1, _uploadItems.Count, null));
    }

    public async Task<IReadOnlyList<MissionItem>> DownloadAsync(CancellationToken ct)
    {
        // Cancel any ongoing upload
        _isUploading = false;
        _uploadItems = null;

        // Reset download state
        _downloaded = new List<MissionItem>();
        _isDownloading = true;
        _expectedCount = 0;

        // Create new TCS
        _downloadTcs = new TaskCompletionSource<IReadOnlyList<MissionItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var sys = _backend.SystemId;
        var comp = _backend.ComponentId;

        Debug.WriteLine($"[MissionService] Starting download from {sys}:{comp}");

        MissionStateChanged?.Invoke(
            new MissionState(MissionTransferState.Downloading, 0, 0, null));

        var packet = MissionRequestListCommand.Create(sys, comp, ref _seq);
        Debug.WriteLine($"[MissionService] Sent MISSION_REQUEST_LIST");
        await _sender.SendAsync(packet, ct);

        // Add timeout
        var timeoutTask = Task.Delay(10000, ct);
        var completedTask = await Task.WhenAny(_downloadTcs.Task, timeoutTask);

        if (completedTask == timeoutTask)
        {
            _isDownloading = false;
            Debug.WriteLine($"[MissionService] Download timeout!");
            MissionStateChanged?.Invoke(
                new MissionState(MissionTransferState.Failed, 0, 0, "Download timeout"));
            return new List<MissionItem>();
        }

        return await _downloadTcs.Task;
    }

    public async Task OnMissionCount(ushort count, CancellationToken ct)
    {
        Debug.WriteLine($"[MissionService] OnMissionCount: {count}, _isDownloading={_isDownloading}");

        if (!_isDownloading)
        {
            Debug.WriteLine($"[MissionService] Ignoring MISSION_COUNT - not downloading");
            return;
        }

        _expectedCount = count;

        MissionStateChanged?.Invoke(
            new MissionState(MissionTransferState.Downloading, 0, count, null));

        if (count == 0)
        {
            Debug.WriteLine($"[MissionService] No mission items on vehicle");
            _isDownloading = false;
            MissionStateChanged?.Invoke(
                new MissionState(MissionTransferState.Completed, 0, 0, null));
            _downloadTcs?.TrySetResult(new List<MissionItem>());
            return;
        }

        var sys = _backend.SystemId;
        var comp = _backend.ComponentId;

        Debug.WriteLine($"[MissionService] Requesting item 0");
        var packet = MissionRequestIntCommand.Create(0, sys, comp, ref _seq);
        await _sender.SendAsync(packet, ct);
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        var packet = MissionClearAllCommand.Create(
            _backend.SystemId,
            _backend.ComponentId,
            ref _seq);

        await _sender.SendAsync(packet, ct);

        System.Diagnostics.Debug.WriteLine("[MissionService] Sent MISSION_CLEAR_ALL");
    }
    public async Task OnMissionItem(MissionItem item, CancellationToken ct)
    {
        if (!_isDownloading)
        {
            Debug.WriteLine($"[MissionService] Ignoring MISSION_ITEM - not downloading");
            return;
        }

        _downloaded!.Add(item);

        Debug.WriteLine($"[MissionService] Received item {_downloaded.Count}/{_expectedCount}");

        MissionStateChanged?.Invoke(
            new MissionState(MissionTransferState.Downloading, _downloaded.Count, _expectedCount, null));

        if (_downloaded.Count < _expectedCount)
        {
            var sys = _backend.SystemId;
            var comp = _backend.ComponentId;
            var packet = MissionRequestIntCommand.Create((ushort)_downloaded.Count, sys, comp, ref _seq);
            await _sender.SendAsync(packet, ct);
        }
        else
        {
            // Send ACK
            var sys = _backend.SystemId;
            var comp = _backend.ComponentId;
            var packet = MissionAckCommand.Create(sys, comp, 0, ref _seq);
            await _sender.SendAsync(packet, ct);

            Debug.WriteLine($"[MissionService] Download COMPLETE! {_expectedCount} items");
            _isDownloading = false;

            MissionStateChanged?.Invoke(
                new MissionState(MissionTransferState.Completed, _expectedCount, _expectedCount, null));

            _downloadTcs!.TrySetResult(_downloaded);
        }
    }

    public void OnMissionAck(byte result)
    {
        Debug.WriteLine($"[MissionService] ACK: {result}, uploading={_isUploading}, lastSeq={_lastUploadedSeq}");

        if (result == 0)
        {
            if (_isUploading && _uploadItems != null && _lastUploadedSeq == _uploadItems.Count - 1)
            {
                Debug.WriteLine($"[MissionService] Upload COMPLETE!");

                int count = _uploadItems.Count;
                _isUploading = false;
                _uploadItems = null;
                _lastUploadedSeq = -1;

                MissionStateChanged?.Invoke(
                    new MissionState(MissionTransferState.Completed, count, count, null));
            }
            return;
        }

        // Error
        _isUploading = false;
        _isDownloading = false;

        string errorMsg = result switch
        {
            1 => "Generic error",
            2 => "Coordinates out of range",
            3 => "Item index too large",
            4 => "Not enough space",
            5 => "Denied by MAV",
            15 => "Timeout",
            _ => $"Error {result}"
        };

        Debug.WriteLine($"[MissionService] Error: {errorMsg}");
        MissionStateChanged?.Invoke(
            new MissionState(MissionTransferState.Failed, 0, 0, errorMsg));
        _uploadItems = null;
        _downloadTcs?.TrySetException(new Exception(errorMsg));
    }
}