using GCS.Core.Domain;
using GCS.Core.Mission;

namespace GCS.Core.Tests;

public class MissionServiceTests
{
    public MissionServiceTests() => MavlinkInit.EnsureInitialized();

    private static MissionItem Wp(int seq, double lat, double lon, float alt)
        => new(seq, MavCmd.Waypoint, lat, lon, alt);

    [Fact]
    public async Task Download_ReceivesAllItems_CompletesWithFullList()
    {
        var svc = new MissionService(new FakeSender(), new FakeBackend(1, 1));
        var states = new List<MissionState>();
        svc.MissionStateChanged += states.Add;

        var downloadTask = svc.DownloadAsync(CancellationToken.None);

        await svc.OnMissionCount(2, CancellationToken.None);
        await svc.OnMissionItem(Wp(0, 10, 20, 100), CancellationToken.None);
        await svc.OnMissionItem(Wp(1, 11, 21, 110), CancellationToken.None);

        var result = await downloadTask;

        Assert.Equal(2, result.Count);
        Assert.Equal(MissionTransferState.Completed, states[^1].State);
    }

    [Fact]
    public async Task Download_EmptyMission_CompletesEmpty()
    {
        var svc = new MissionService(new FakeSender(), new FakeBackend(1, 1));

        var downloadTask = svc.DownloadAsync(CancellationToken.None);
        await svc.OnMissionCount(0, CancellationToken.None);

        var result = await downloadTask;
        Assert.Empty(result);
    }

    [Fact]
    public async Task Upload_AllRequestsServedThenAck_Completes()
    {
        var sender = new FakeSender();
        var svc = new MissionService(sender, new FakeBackend(1, 1));
        var states = new List<MissionState>();
        svc.MissionStateChanged += states.Add;

        var items = new[] { Wp(0, 10, 20, 100), Wp(1, 11, 21, 110) };

        await svc.UploadAsync(items, CancellationToken.None);   // clear + count
        await svc.OnMissionRequest(0, CancellationToken.None);  // item 0
        await svc.OnMissionRequest(1, CancellationToken.None);  // item 1
        svc.OnMissionAck(0);                                    // accepted

        Assert.Equal(MissionTransferState.Completed, states[^1].State);
        Assert.True(sender.Sent.Count >= 4); // clear, count, item0, item1
    }

    [Fact]
    public async Task Upload_ErrorAck_RaisesFailed()
    {
        var svc = new MissionService(new FakeSender(), new FakeBackend(1, 1));
        var states = new List<MissionState>();
        svc.MissionStateChanged += states.Add;

        await svc.UploadAsync(new[] { Wp(0, 10, 20, 100) }, CancellationToken.None);
        svc.OnMissionAck(3); // MAV_MISSION_NO_SPACE / item index too large

        Assert.Equal(MissionTransferState.Failed, states[^1].State);
        Assert.NotNull(states[^1].ErrorMessage);
    }

    [Fact]
    public async Task MissionItem_WhenNotDownloading_IsIgnored()
    {
        var sender = new FakeSender();
        var svc = new MissionService(sender, new FakeBackend(1, 1));

        // No active download - this must be a no-op, not throw.
        await svc.OnMissionItem(Wp(0, 10, 20, 100), CancellationToken.None);

        Assert.Empty(sender.Sent);
    }
}
