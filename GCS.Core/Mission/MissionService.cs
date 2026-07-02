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
    // under the lock but always sent outside it.
    private readonly object _gate = new();

    // Download state
    private List<MissionItem>? _downloaded;
    private ushort _expectedCount;
    private TaskCompletionSource<IReadOnlyList<MissionItem>>? _downloadTcs;
    private bool _isDownloading;
    private DateTime _lastRxUtc;
    private int _downloadRetries;

    // Upload state
    private IReadOnlyList<MissionItem>? _uploadItems;
    private bool _isUploading;
    private int _lastUploadedSeq = -1;
    private TaskCompletionSource<bool>? _uploadTcs;
    private DateTime _lastUploadActivityUtc;

    private const double IdleTimeoutSec = 8.0;      // give up if the vehicle goes silent
    private const int PollMs = 1000;                // watchdog tick
    private const double ItemRetryAfterSec = 1.5;   // re-request a missing download item
    private const int MaxRetries = 6;               // consecutive retries before failing

    public event Action<MissionState>? MissionStateChanged;

    public MissionService(IMavlinkSender sender, IMavlinkBackend backend)
    {
        _sender = sender;
        _backend = backend;
    }

    // ── Upload ───────────────────────────────────────────────────────

    public async Task UploadAsync(IReadOnlyList<MissionItem> items, CancellationToken ct)
    {
        var sys = _backend.SystemId;
        var comp = _backend.ComponentId;

        byte[] clearPacket, countPacket;
        TaskCompletionSource<IReadOnlyList<MissionItem>>? cancelledDownload;
        TaskCompletionSource<bool> uploadTcs;

        lock (_gate)
        {
            _isDownloading = false;
            cancelledDownload = _downloadTcs;
            _downloadTcs = null;

            _uploadItems = items;
            _isUploading = true;
            _lastUploadedSeq = -1;
            _lastUploadActivityUtc = DateTime.UtcNow;
            _uploadTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            uploadTcs = _uploadTcs;

            clearPacket = MissionClearAllCommand.Create(sys, comp);
            countPacket = MissionCountCommand.Create(sys, comp, (ushort)items.Count);
        }

        cancelledDownload?.TrySetCanceled();

        Debug.WriteLine($"[MissionService] Starting upload of {items.Count} items to {sys}:{comp}");
        MissionStateChanged?.Invoke(new MissionState(MissionTransferState.Uploading, 0, items.Count, null));

        await _sender.SendAsync(clearPacket, ct);
        await Task.Delay(200, ct);
        await _sender.SendAsync(countPacket, ct);
        Debug.WriteLine($"[MissionService] Sent MISSION_COUNT={items.Count}");

        // Idle watchdog: the FC drives the transfer by requesting items; if it
        // goes silent mid-upload, fail instead of leaving the UI stuck.
        while (true)
        {
            var completed = await Task.WhenAny(uploadTcs.Task, Task.Delay(PollMs, ct));
            if (completed == uploadTcs.Task)
            {
                await uploadTcs.Task; // success (throws if it faulted)
                return;
            }

            bool timedOut;
            lock (_gate)
            {
                if (!_isUploading) return; // resolved by an ack already
                timedOut = (DateTime.UtcNow - _lastUploadActivityUtc).TotalSeconds > IdleTimeoutSec;
                if (timedOut)
                {
                    _isUploading = false;
                    _uploadItems = null;
                }
            }

            if (timedOut)
            {
                Debug.WriteLine("[MissionService] Upload idle timeout");
                MissionStateChanged?.Invoke(
                    new MissionState(MissionTransferState.Failed, 0, 0, "Upload timed out - no response from vehicle"));
                uploadTcs.TrySetResult(false);
                return;
            }
        }
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

            packet = MissionItemIntCommand.Create(_uploadItems[seq], sys, comp);
            _lastUploadedSeq = seq;
            _lastUploadActivityUtc = DateTime.UtcNow;
            totalCount = _uploadItems.Count;
        }

        Debug.WriteLine($"[MissionService] Sending item {seq}/{totalCount}");
        await _sender.SendAsync(packet, ct);

        MissionStateChanged?.Invoke(
            new MissionState(MissionTransferState.Uploading, seq + 1, totalCount, null));
    }

    // ── Download ─────────────────────────────────────────────────────

    public async Task<IReadOnlyList<MissionItem>> DownloadAsync(CancellationToken ct)
    {
        var sys = _backend.SystemId;
        var comp = _backend.ComponentId;

        TaskCompletionSource<IReadOnlyList<MissionItem>> tcs;
        byte[] packet;

        lock (_gate)
        {
            _isUploading = false;
            _uploadItems = null;

            _downloaded = new List<MissionItem>();
            _isDownloading = true;
            _expectedCount = 0;
            _downloadRetries = 0;
            _lastRxUtc = DateTime.UtcNow;

            _downloadTcs = new TaskCompletionSource<IReadOnlyList<MissionItem>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = _downloadTcs;

            packet = MissionRequestListCommand.Create(sys, comp);
        }

        Debug.WriteLine($"[MissionService] Starting download from {sys}:{comp}");
        MissionStateChanged?.Invoke(new MissionState(MissionTransferState.Downloading, 0, 0, null));
        await _sender.SendAsync(packet, ct);

        // Watchdog: re-request the item (or the list) we're waiting on if the
        // vehicle goes quiet, so a single dropped packet doesn't fail the download.
        while (true)
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(PollMs, ct));
            if (completed == tcs.Task)
                return await tcs.Task;

            byte[]? retry = null;
            bool giveUp = false;
            lock (_gate)
            {
                if (!_isDownloading) continue; // will resolve on next loop
                if ((DateTime.UtcNow - _lastRxUtc).TotalSeconds >= ItemRetryAfterSec)
                {
                    if (++_downloadRetries > MaxRetries)
                    {
                        giveUp = true;
                        _isDownloading = false;
                    }
                    else
                    {
                        retry = _expectedCount == 0
                            ? MissionRequestListCommand.Create(sys, comp)
                            : MissionRequestIntCommand.Create((ushort)_downloaded!.Count, sys, comp);
                    }
                }
            }

            if (giveUp)
            {
                Debug.WriteLine("[MissionService] Download timeout");
                MissionStateChanged?.Invoke(
                    new MissionState(MissionTransferState.Failed, 0, 0, "Download timed out - no response from vehicle"));
                tcs.TrySetResult(new List<MissionItem>());
                return new List<MissionItem>();
            }

            if (retry != null)
            {
                Debug.WriteLine("[MissionService] Re-requesting missing mission data");
                await _sender.SendAsync(retry, ct);
            }
        }
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
            _lastRxUtc = DateTime.UtcNow;
            _downloadRetries = 0;

            if (count == 0)
            {
                _isDownloading = false;
                emptyMission = true;
                emptyTcs = _downloadTcs;
            }
            else
            {
                requestPacket = MissionRequestIntCommand.Create(0, sys, comp);
            }
        }

        MissionStateChanged?.Invoke(new MissionState(MissionTransferState.Downloading, 0, count, null));

        if (emptyMission)
        {
            Debug.WriteLine($"[MissionService] No mission items on vehicle");
            MissionStateChanged?.Invoke(new MissionState(MissionTransferState.Completed, 0, 0, null));
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

            // Only accept the next expected item; ignore duplicates/out-of-order
            // (which can happen after a retry) but treat them as link activity.
            if (item.Sequence != _downloaded!.Count)
            {
                Debug.WriteLine($"[MissionService] Ignoring item seq={item.Sequence}, expected {_downloaded.Count}");
                _lastRxUtc = DateTime.UtcNow;
                return;
            }

            _downloaded.Add(item);
            _lastRxUtc = DateTime.UtcNow;
            _downloadRetries = 0;
            receivedCount = _downloaded.Count;
            expected = _expectedCount;

            if (receivedCount < expected)
            {
                packet = MissionRequestIntCommand.Create((ushort)receivedCount, sys, comp);
            }
            else
            {
                packet = MissionAckCommand.Create(sys, comp, 0);
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
            MissionStateChanged?.Invoke(new MissionState(MissionTransferState.Completed, expected, expected, null));
            completeTcs?.TrySetResult(result!);
        }
    }

    // ── Other commands ───────────────────────────────────────────────

    public async Task ClearAsync(CancellationToken ct)
    {
        var packet = MissionClearAllCommand.Create(_backend.SystemId, _backend.ComponentId);
        await _sender.SendAsync(packet, ct);
        Debug.WriteLine("[MissionService] Sent MISSION_CLEAR_ALL");
    }

    public async Task SetCurrentAsync(ushort seq, CancellationToken ct)
    {
        var packet = MissionSetCurrentCommand.Create(_backend.SystemId, _backend.ComponentId, seq);
        await _sender.SendAsync(packet, ct);
        Debug.WriteLine($"[MissionService] Sent MISSION_SET_CURRENT seq={seq}");
    }

    public void OnMissionAck(byte result)
    {
        int uploadedCount = 0;
        bool uploadComplete = false;
        bool failed = false;
        string? errorMsg = null;
        TaskCompletionSource<IReadOnlyList<MissionItem>>? failedDownload = null;
        TaskCompletionSource<bool>? uploadTcs = null;

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
                    uploadTcs = _uploadTcs;
                }
            }
            else
            {
                _isUploading = false;
                _isDownloading = false;
                _uploadItems = null;
                failed = true;
                errorMsg = MapError(result);
                failedDownload = _downloadTcs;
                uploadTcs = _uploadTcs;
            }
        }

        Debug.WriteLine($"[MissionService] ACK: {result}");

        if (uploadComplete)
        {
            Debug.WriteLine($"[MissionService] Upload COMPLETE!");
            MissionStateChanged?.Invoke(
                new MissionState(MissionTransferState.Completed, uploadedCount, uploadedCount, null));
            uploadTcs?.TrySetResult(true);
        }

        if (failed)
        {
            Debug.WriteLine($"[MissionService] Error: {errorMsg}");
            MissionStateChanged?.Invoke(new MissionState(MissionTransferState.Failed, 0, 0, errorMsg));
            failedDownload?.TrySetException(new Exception(errorMsg));
            uploadTcs?.TrySetResult(false);
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
