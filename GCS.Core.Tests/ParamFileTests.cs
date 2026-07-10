using GCS.Core.Parameters;
using Xunit;

namespace GCS.Core.Tests;

public class ParamFileTests
{
    private static string TempFile()
    {
        return Path.Combine(Path.GetTempPath(), $"gcs_test_{Guid.NewGuid():N}.param");
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = TempFile();
        try
        {
            ParamFile.Save(path, new Dictionary<string, float>
            {
                ["WP_RADIUS"] = 90f,
                ["Q_A_RAT_PIT_P"] = 0.135f,
                ["THR_MIN"] = -20f,
            });

            var (loaded, skipped) = ParamFile.Load(path);

            Assert.Equal(0, skipped);
            Assert.Equal(3, loaded.Count);
            Assert.Contains(loaded, p => p.Key == "WP_RADIUS" && p.Value == 90f);
            Assert.Contains(loaded, p => p.Key == "Q_A_RAT_PIT_P" && Math.Abs(p.Value - 0.135f) < 1e-6);
            Assert.Contains(loaded, p => p.Key == "THR_MIN" && p.Value == -20f);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_AcceptsMissionPlannerVariants_AndSkipsGarbage()
    {
        var path = TempFile();
        try
        {
            File.WriteAllLines(path, new[]
            {
                "# comment line",
                "",
                "AIRSPEED_MIN,12",
                "AIRSPEED_MAX\t30",
                "TRIM_THROTTLE 45",
                "not a parameter line at all !!!",
                "NAME_TOO_LONG_FOR_MAVLINK_PARAM,5",
            });

            var (loaded, skipped) = ParamFile.Load(path);

            Assert.Equal(3, loaded.Count);
            Assert.Equal(2, skipped);
            Assert.Contains(loaded, p => p.Key == "AIRSPEED_MIN" && p.Value == 12f);
            Assert.Contains(loaded, p => p.Key == "AIRSPEED_MAX" && p.Value == 30f);
            Assert.Contains(loaded, p => p.Key == "TRIM_THROTTLE" && p.Value == 45f);
        }
        finally { File.Delete(path); }
    }
}
