using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GCS.Core.Firmware;

/// <summary>
/// Downloads and parses the ArduPilot firmware manifest, and fetches .apj files,
/// from firmware.ardupilot.org.
/// </summary>
public sealed class ArduPilotManifestClient
{
    private const string ManifestUrl = "https://firmware.ardupilot.org/manifest.json.gz";

    private readonly HttpClient _http;

    public ArduPilotManifestClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>Fetch and parse the manifest, returning every .apj firmware build.</summary>
    public async Task<IReadOnlyList<ArduPilotFirmwareEntry>> GetFirmwareAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(ManifestUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var raw = await resp.Content.ReadAsStreamAsync(ct);
        // The manifest is ~67 MB decompressed - stream it one entry at a time on a
        // background thread instead of materialising the whole JSON tree.
        return await Task.Run(() => ParseFirmware(raw, ct), ct);
    }

    private static List<ArduPilotFirmwareEntry> ParseFirmware(Stream raw, CancellationToken ct)
    {
        using var gz = new GZipStream(raw, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        using var jr = new JsonTextReader(reader);

        var list = new List<ArduPilotFirmwareEntry>();

        while (jr.Read())
        {
            if (jr.TokenType != JsonToken.PropertyName || (string?)jr.Value != "firmware")
                continue;

            if (!jr.Read() || jr.TokenType != JsonToken.StartArray)
                break;

            while (jr.Read() && jr.TokenType != JsonToken.EndArray)
            {
                ct.ThrowIfCancellationRequested();
                if (jr.TokenType != JsonToken.StartObject) continue;

                var fw = JObject.Load(jr);
                if (!string.Equals((string?)fw["format"], "apj", StringComparison.OrdinalIgnoreCase))
                    continue;

                var url = (string?)fw["url"];
                if (string.IsNullOrEmpty(url)) continue;

                list.Add(new ArduPilotFirmwareEntry(
                    VehicleType: (string?)fw["vehicletype"] ?? "",
                    Platform: (string?)fw["platform"] ?? "",
                    Version: (string?)fw["mav-firmware-version"] ?? "",
                    ReleaseType: (string?)fw["mav-firmware-version-type"] ?? "",
                    Url: url,
                    BoardId: (int?)fw["board_id"] ?? 0,
                    Latest: ((int?)fw["latest"] ?? 0) == 1));
            }
            break;
        }

        return list;
    }

    /// <summary>Download the raw .apj (JSON) text for a firmware entry.</summary>
    public async Task<string> DownloadApjTextAsync(string url, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }
}
