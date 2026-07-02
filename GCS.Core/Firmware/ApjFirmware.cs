using System;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json.Linq;

namespace GCS.Core.Firmware;

/// <summary>
/// A parsed ArduPilot .apj firmware file. The .apj is JSON containing the board
/// id and the flash image as base64 of a zlib-compressed binary.
/// </summary>
public sealed class ApjFirmware
{
    public int BoardId { get; }
    public byte[] Image { get; }             // decompressed flash binary
    public int DeclaredImageSize { get; }
    public string? Description { get; }

    private ApjFirmware(int boardId, byte[] image, int declaredImageSize, string? description)
    {
        BoardId = boardId;
        Image = image;
        DeclaredImageSize = declaredImageSize;
        Description = description;
    }

    public static ApjFirmware Parse(string json)
    {
        var o = JObject.Parse(json);

        int boardId = (int?)o["board_id"] ?? 0;
        int declaredSize = (int?)o["image_size"] ?? 0;
        string? b64 = (string?)o["image"];
        if (string.IsNullOrEmpty(b64))
            throw new InvalidDataException("APJ file has no 'image' field.");

        byte[] compressed = Convert.FromBase64String(b64);
        byte[] image;
        using (var src = new MemoryStream(compressed))
        using (var zlib = new ZLibStream(src, CompressionMode.Decompress))
        using (var dst = new MemoryStream())
        {
            zlib.CopyTo(dst);
            image = dst.ToArray();
        }

        if (declaredSize > 0 && image.Length != declaredSize)
            throw new InvalidDataException(
                $"APJ image size mismatch: header says {declaredSize}, decompressed {image.Length}.");

        return new ApjFirmware(boardId, image, declaredSize, (string?)o["description"]);
    }

    public static ApjFirmware ParseFile(string path) => Parse(File.ReadAllText(path));
}
