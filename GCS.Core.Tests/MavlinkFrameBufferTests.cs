using GCS.Core.Mavlink;

namespace GCS.Core.Tests;

public class MavlinkFrameBufferTests
{
    // Builds a syntactically-sized MAVLink v2 frame. Contents are arbitrary
    // (the buffer frames by length and never validates the CRC - that is the
    // serializer's job downstream), but no byte besides the STX is 0xFD so the
    // resync tests behave deterministically.
    private static byte[] BuildFrame(byte payloadLen, bool signed)
    {
        int signatureLen = signed ? 13 : 0;
        int total = 10 + payloadLen + 2 + signatureLen;
        var frame = new byte[total];
        frame[0] = 0xFD;                       // STX
        frame[1] = payloadLen;                 // LEN
        frame[2] = (byte)(signed ? 0x01 : 0);  // incompat_flags (0x01 = signed)
        for (int i = 3; i < total; i++)
            frame[i] = 0xAB;
        return frame;
    }

    [Fact]
    public void SingleFrame_IsExtractedWhole()
    {
        var buf = new MavlinkFrameBuffer();
        var frame = BuildFrame(5, signed: false);

        var frames = buf.AddData(frame).ToList();

        Assert.Single(frames);
        Assert.Equal(frame.Length, frames[0].Length);
    }

    [Fact]
    public void FragmentedFrame_IsReassembledAcrossChunks()
    {
        var buf = new MavlinkFrameBuffer();
        var frame = BuildFrame(10, signed: false);

        var first = buf.AddData(frame.AsSpan(0, 6).ToArray()).ToList();   // partial header
        var second = buf.AddData(frame.AsSpan(6).ToArray()).ToList();     // remainder

        Assert.Empty(first);
        Assert.Single(second);
        Assert.Equal(frame.Length, second[0].Length);
    }

    [Fact]
    public void TwoFramesInOneChunk_AreBothExtracted()
    {
        var buf = new MavlinkFrameBuffer();
        var a = BuildFrame(4, signed: false);
        var b = BuildFrame(7, signed: false);

        var frames = buf.AddData(a.Concat(b).ToArray()).ToList();

        Assert.Equal(2, frames.Count);
        Assert.Equal(a.Length, frames[0].Length);
        Assert.Equal(b.Length, frames[1].Length);
    }

    [Fact]
    public void SignedFrame_ConsumesSignatureBytes_AndFollowingFrameIsAligned()
    {
        // Regression guard: a signed frame adds 13 signature bytes. If the
        // length math ignores incompat_flags, the next frame is mis-aligned.
        var buf = new MavlinkFrameBuffer();
        var signed = BuildFrame(6, signed: true);   // 10 + 6 + 2 + 13 = 31
        var next = BuildFrame(3, signed: false);

        var frames = buf.AddData(signed.Concat(next).ToArray()).ToList();

        Assert.Equal(2, frames.Count);
        Assert.Equal(signed.Length, frames[0].Length);
        Assert.Equal(next.Length, frames[1].Length);
    }

    [Fact]
    public void GarbageBeforeStartMarker_IsSkipped()
    {
        var buf = new MavlinkFrameBuffer();
        var junk = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var frame = BuildFrame(5, signed: false);

        var frames = buf.AddData(junk.Concat(frame).ToArray()).ToList();

        Assert.Single(frames);
        Assert.Equal(frame.Length, frames[0].Length);
    }

    [Fact]
    public void NoStartMarker_YieldsNothing()
    {
        var buf = new MavlinkFrameBuffer();
        var frames = buf.AddData(new byte[] { 1, 2, 3, 4, 5 }).ToList();
        Assert.Empty(frames);
    }
}
