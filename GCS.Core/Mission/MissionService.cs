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

    // Guards the whole transfer state machine. Upload/Download are driven from
    // the UI thread while the On* callbacks fire on the transport thread, so
    // every field below is touched from multiple threads. Packets are built
    // under the lock (which also bumps _seq) but always sent outside it.
    private readonly object _gate = new();

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
        var sys = _backend.SystemId;
        var comp = _backend.ComponentId;

        byte[] clearPacket, countPacket;
        TaskCompletionSource<IReadOnlyList<MissionItem>>? cancelledDownload;

        lock (_gate)
        {
            // Cancel any ongoing download.
            _isDownloading = false;
            cancelledDownload = _downloadTcs;
            _downloadTcs = null;

            _uploadItems = items;
            _isUploading = true;
            _lastUploadedSeq = -1;
            _seq = 0;

            clearPacket = MissionClearAllCommand.Create(sys, comp, ref _seq);
            countPacket = MissionCountCommand.Create(sys, comp, (ushort)items.Count, ref _seq);
        }

        cancelledDownload?.TrySetCanceled();

        Debug.WriteLine($"[MissionService] Starting upload of {items.Count} items to {sys}:{comp}");

        MissionStateChanged?.Invoke(
            new MissionState(MissionTransferState.Uploading, 0, items.Count, null));

        await _sender.SendAsync(clearPacket, ct);
        await Task.Delay(200, ct);
        await _sender.SendAsync(countPacket, ct);

        Debug.WriteLine($"[MissionService] Sent MISSION_COUNT={items.Count}");
    }

    public async Task OnMissionRequest(ushort seq, CancellationToken ct)
    {
        var sys = _backend.SystemId;
        var comp = _backend.ComponentId;

        byte[] packet;
        int totalCount;

        lock (_gate)
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

            var item = _uploadItems[seq];
            packet = MissionItemIntCommand.Create(item, sys, comp, ref _seq);
            _lastUploadedSeq = seq;
            totalCount = _uploadItems.Count;
        }

        Debug.WriteLine($"[MissionService] Sending item {seq}/{totalCount}");
        await _sender.SendAsync(packet, ct);

        MissionStateChanged?.Invoke(
            new MissionState(MissionTransferState.Uploading, seq + 1, totalCount, null));
    }

    public async Task<IReadOnlyList<MissionItem>> DownloadAsync(CancellationToken ct)
    {
        var sys = _backend.SystemId;
        var comp = _backend.ComponentId;

        TaskCompletionSource<IReadOnlyList<MissionItem>> tcs;
        byte[] packet;

        lock (_gate)
        {
            // Cancel any ongoing upload.
            _isUploading = false;
            _uploadItems = null;

            // Reset download state.
            _downloaded = new List<MissionItem>();
            _isDownloading = true;
            _expectedCount = 0;

            _downloadTcs = new TaskCompletionSource<IReadOnlyList<MissionItem>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = _downloadTcs;

            packet = MissionRequestListCommand.Create(sys, comp, ref _seq);
        }

        Debug.WriteLine($"[MissionService] Starting download from {sys}:{comp}");

        MissionStateChanged?.Invoke(
            new MissionState(MissionTransferState.Downloading, 0, 0, null));

        await _sender.SendAsync(packet, ct);

        var timeoutTask = Task.Delay(10000, ct);
        var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

        if (completedTask == timeoutTask)
        {
            lock (_gate) _isDownloading = false;
            Debug.WriteLine($"[MissionService] Download timeout!");
            MissionStateChanged?.Invoke(
                new MissionState(MissionTransferState.Failed, 0, 0, "Download timeout"));
            return new List<MissionItem>();
        }

        return await tcs.Task;
    }

    public async Task OnMissionCount(ushort count, CancellationToken ct)
    {
        var sys = _backend.SystemId;
        var comp = _backend.ComponentId;

        byte[]? requestPacket = null;
        bool emptyMission = false;
        TaskCompletionSource<IReadOnlyList<MissionItem>>? emptyTcs = null;

        lock (_gate)
        {
            if (!_isDownloading)
            {
                Debug.WriteLine($"[MissionService] Ignoring MISSION_COUNT - not downloading");
                return;
            }

            _expectedCount = count;

            if (count == 0)
            {
                _isDownloading = false;
                emptyMission = true;
                emptyTcs = _downloadTcs;
            }
            else
            {
                requestPacket = MissionRequestIntCommand.Create(0, sys, comp, ref _seq);
            }
        }

        MissionStateChanged?.Invoke(
            new MissionState(MissionTransferState.Downloading, 0, count, null));

        if (emptyMission)
        {
            Debug.WriteLine($"[MissionService] No mission items on vehicle");
            MissionStateChanged?.Invoke(
                new MissionState(MissionTransferState.Completed, 0, 0, null));
            emptyTcs?.TrySetResult(new List<MissionItem>());
            return;
        }

        Debug.WriteLine($"[MissionService] Requesting item 0");
        await _sender.SendAsync(requestPacket!, ct);
    }

    public async Task OnMissionItem(MissionItem item, CancellationToken ct)
    {
        var sys = _backend.SystemId;
        var comp = _backend.ComponentId;

        byte[] packet;
        int receivedCount, expected;
        bool complete = false;
        List<MissionItem>? result = null;
        TaskCompletionSource<IReadOnlyList<MissionItem>>? completeTcs = null;

        lock (_gate)
        {
            if (!_isDownloading)
            {
                Debug.WriteLine($"[MissionService] Ignoring MISSION_ITEM - not downloading");
                return;
            }

            _downloaded!.Add(item);
            receivedCount = _downloaded.Count;
            expected = _expectedCount;

            if (receivedCount < expected)
            {
                packet = MissionRequestIntCommand.Create((ushort)receivedCount, sys, comp, ref _seq);
            }
            else
            {
                packet = MissionAckCommand.Create(sys, comp, 0, ref _seq);
                _isDownloading = false;
                complete = true;
                result = _downloaded;
                completeTcs = _downloadTcs;
            }
        }

        Debug.WriteLine($"[MissionService] Received item {receivedCount}/{expected}");

        MissionStateChanged?.Invoke(
            new MissionState(MissionTransferState.Downloading, receivedCount, expected, null));

        await _sender.SendAsync(packet, ct);

        if (complete)
        {
            Debug.WriteLine($"[MissionService] Download COMPLETE! {expected} items");
            MissionStateChanged?.Invoke(
                new MissionState(MissionTransferState.Completed, expected, expected, null));
            completeTcs?.TrySetResult(result!);
        }
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        var sys = _backend.SystemId;
        var comp = _backend.ComponentId;

        byte[] packet;
        lock (_gate)
        {
            packet = MissionClearAllCommand.Create(sys, comp, ref _seq);
        }

        await _sender.SendAsync(packet, ct);
        Debug.WriteLine("[MissionService] Sent MISSION_CLEAR_ALL");
    }

    public void OnMissionAck(byte result)
    {
        int uploadedCount = 0;
        bool uploadComplete = false;
        bool failed = false;
        string? errorMsg = null;
        TaskCompletionSource<IReadOnlyList<MissionItem>>? failedTcs = null;

        lock (_gate)
        {
            if (result == 0)
            {
                if (_isUploading && _uploadItems != null && _lastUploadedSeq == _uploadItems.Count - 1)
                {
                    uploadedCount = _uploadItems.Count;
                    _isUploading = false;
                    _uploadItems = null;
                    _lastUploadedSeq = -1;
                    uploadComplete = true;
                }
            }
            else
            {
                _isUploading = false;
                _isDownloading = false;
                _uploadItems = null;
                failed = true;
                errorMsg = MapError(result);
                failedTcs = _downloadTcs;
            }
        }

        Debug.WriteLine($"[MissionService] ACK: {result}");

        if (uploadComplete)
        {
            Debug.WriteLine($"[MissionService] Upload COMPLETE!");
            MissionStateChanged?.Invoke(
                new MissionState(MissionTransferState.Completed, uploadedCount, uploadedCount, null));
        }

        if (failed)
        {
            Debug.WriteLine($"[MissionService] Error: {errorMsg}");
            MissionStateChanged?.Invoke(
                new MissionState(MissionTransferState.Failed, 0, 0, errorMsg));
            failedTcs?.TrySetException(new Exception(errorMsg));
        }
    }

    private static string MapError(byte result) => result switch
    {
        1 => "Generic error",
        2 => "Coordinates out of range",
        3 => "Item index too large",
        4 => "Not enough space",
        5 => "Denied by MAV",
        15 => "Timeout",
        _ => $"Error {result}"
    };
}
