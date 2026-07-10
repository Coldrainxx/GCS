using System.Buffers.Binary;

namespace GCS.Core.Logging;

/// <summary>
/// Writes a Mission-Planner-compatible telemetry log (.tlog): every MAVLink
/// packet prefixed with an 8-byte big-endian timestamp in microseconds since
/// the Unix epoch. Thread-safe; a write failure disables further logging
/// instead of throwing into the telemetry path.
/// </summary>
public sealed class TelemetryLogger : IDisposable
{
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly object _lock = new();
    private FileStream? _stream;
    private int _writesSinceFlush;
    private bool _failed;

    public string FilePath { get; }
    public long PacketsWritten { get; private set; }

    public TelemetryLogger(string filePath)
    {
        FilePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
    }

    /// <summary>Append one complete MAVLink packet with the current timestamp.</summary>
    public void Write(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty) return;

        Span<byte> timestamp = stackalloc byte[8];
        ulong micros = (ulong)((DateTime.UtcNow - UnixEpoch).Ticks / 10);
        BinaryPrimitives.WriteUInt64BigEndian(timestamp, micros);

        lock (_lock)
        {
            if (_stream == null || _failed) return;
            try
            {
                _stream.Write(timestamp);
                _stream.Write(packet);
                PacketsWritten++;

                // Bound data loss on a crash without paying a flush per packet.
                if (++_writesSinceFlush >= 128)
                {
                    _writesSinceFlush = 0;
                    _stream.Flush();
                }
            }
            catch
            {
                _failed = true; // disk full / file gone — stop logging, keep flying
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _stream?.Flush(); _stream?.Dispose(); }
            catch { /* best effort */ }
            _stream = null;
        }
    }
}
