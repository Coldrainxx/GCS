using System;
using System.Collections.Generic;

namespace GCS.Core.Mavlink;

/// <summary>
/// Buffers incoming bytes and extracts complete MAVLink v2 frames.
/// Handles fragmented packets that arrive across multiple chunks, signed
/// frames (incompat flag 0x01 adds a 13-byte signature), and resynchronises
/// on the start marker after garbage or a corrupt length.
/// </summary>
public class MavlinkFrameBuffer
{
    // 4096 (max transport read) + headroom for a leftover partial frame.
    private readonly byte[] _buffer = new byte[8192];
    private int _bufferPos = 0;

    private const byte MAVLINK_V2_START = 0xFD;
    private const int MAVLINK_V2_HEADER_LEN = 10;
    private const int MAVLINK_V2_CHECKSUM_LEN = 2;
    private const int MAVLINK_V2_SIGNATURE_LEN = 13;
    private const byte MAVLINK_IFLAG_SIGNED = 0x01;

    /// <summary>
    /// Add incoming data to the buffer and extract any complete frames.
    /// </summary>
    public IEnumerable<ReadOnlyMemory<byte>> AddData(ReadOnlySpan<byte> data)
    {
        Append(data);

        var frames = new List<ReadOnlyMemory<byte>>();
        int searchPos = 0;

        while (searchPos < _bufferPos)
        {
            int startIdx = IndexOfStart(searchPos);
            if (startIdx < 0)
            {
                // No start marker in the remaining bytes - discard them.
                _bufferPos = 0;
                return frames;
            }

            int remaining = _bufferPos - startIdx;
            if (remaining < MAVLINK_V2_HEADER_LEN)
            {
                // Not enough for a header yet - keep the partial, wait for more.
                ShiftBuffer(startIdx);
                return frames;
            }

            byte payloadLen = _buffer[startIdx + 1];
            byte incompatFlags = _buffer[startIdx + 2];
            int signatureLen = (incompatFlags & MAVLINK_IFLAG_SIGNED) != 0
                ? MAVLINK_V2_SIGNATURE_LEN
                : 0;

            int totalFrameLen = MAVLINK_V2_HEADER_LEN
                + payloadLen
                + MAVLINK_V2_CHECKSUM_LEN
                + signatureLen;

            if (remaining < totalFrameLen)
            {
                // Incomplete frame - keep it and wait for the rest.
                ShiftBuffer(startIdx);
                return frames;
            }

            frames.Add(_buffer.AsSpan(startIdx, totalFrameLen).ToArray());
            searchPos = startIdx + totalFrameLen;
        }

        // Everything scanned was consumed.
        _bufferPos = 0;
        return frames;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length >= _buffer.Length)
        {
            // A single chunk larger than the buffer: keep only the newest tail.
            data.Slice(data.Length - _buffer.Length).CopyTo(_buffer);
            _bufferPos = _buffer.Length;
            return;
        }

        if (_bufferPos + data.Length > _buffer.Length)
        {
            // Not enough room: drop the oldest (partial/garbage) bytes to fit.
            int overflow = _bufferPos + data.Length - _buffer.Length;
            ShiftBuffer(overflow);
        }

        data.CopyTo(_buffer.AsSpan(_bufferPos));
        _bufferPos += data.Length;
    }

    private int IndexOfStart(int from)
    {
        for (int i = from; i < _bufferPos; i++)
        {
            if (_buffer[i] == MAVLINK_V2_START)
                return i;
        }
        return -1;
    }

    private void ShiftBuffer(int fromPos)
    {
        if (fromPos <= 0)
            return;

        int remaining = _bufferPos - fromPos;
        if (remaining > 0)
            Array.Copy(_buffer, fromPos, _buffer, 0, remaining);

        _bufferPos = remaining > 0 ? remaining : 0;
    }

    public void Reset()
    {
        _bufferPos = 0;
    }
}
