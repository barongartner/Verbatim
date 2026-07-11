using System.Text;
using Verbatim.Core;

namespace Verbatim.Core.Tests;

public class WavWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wavtests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string TempPath() => Path.Combine(_dir, Path.GetRandomFileName() + ".wav");

    private static string Ascii(byte[] bytes, int offset, int count) =>
        Encoding.ASCII.GetString(bytes, offset, count);

    [Fact]
    public void WritesValidHeaderAndData()
    {
        var pcm = new short[] { 0, 1000, -1000, short.MaxValue, short.MinValue, 42 };
        var path = TempPath();
        WavWriter.WriteSlice(path, pcm, 16000, 0, pcm.Length);

        var bytes = File.ReadAllBytes(path);
        int dataBytes = pcm.Length * 2;

        Assert.Equal(44 + dataBytes, bytes.Length);
        Assert.Equal("RIFF", Ascii(bytes, 0, 4));
        Assert.Equal(36 + dataBytes, BitConverter.ToInt32(bytes, 4));
        Assert.Equal("WAVE", Ascii(bytes, 8, 4));
        Assert.Equal("fmt ", Ascii(bytes, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(bytes, 16));          // fmt chunk size
        Assert.Equal(1, BitConverter.ToInt16(bytes, 20));           // PCM
        Assert.Equal(1, BitConverter.ToInt16(bytes, 22));           // mono
        Assert.Equal(16000, BitConverter.ToInt32(bytes, 24));       // sample rate
        Assert.Equal(32000, BitConverter.ToInt32(bytes, 28));       // byte rate
        Assert.Equal(2, BitConverter.ToInt16(bytes, 32));           // block align
        Assert.Equal(16, BitConverter.ToInt16(bytes, 34));          // bits per sample
        Assert.Equal("data", Ascii(bytes, 36, 4));
        Assert.Equal(dataBytes, BitConverter.ToInt32(bytes, 40));

        for (int i = 0; i < pcm.Length; i++)
            Assert.Equal(pcm[i], BitConverter.ToInt16(bytes, 44 + i * 2));
    }

    [Fact]
    public void SliceWritesOnlyRequestedRange()
    {
        var pcm = new short[] { 10, 20, 30, 40, 50 };
        var path = TempPath();
        WavWriter.WriteSlice(path, pcm, 16000, 1, 4); // samples 20, 30, 40

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(44 + 6, bytes.Length);
        Assert.Equal(6, BitConverter.ToInt32(bytes, 40));
        Assert.Equal(20, BitConverter.ToInt16(bytes, 44));
        Assert.Equal(30, BitConverter.ToInt16(bytes, 46));
        Assert.Equal(40, BitConverter.ToInt16(bytes, 48));
    }

    [Fact]
    public void ClampsRangeToPcmBounds()
    {
        var pcm = new short[] { 1, 2, 3 };
        var path = TempPath();
        WavWriter.WriteSlice(path, pcm, 16000, -5, 99);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(44 + 6, bytes.Length);
        Assert.Equal(6, BitConverter.ToInt32(bytes, 40));
        Assert.Equal(1, BitConverter.ToInt16(bytes, 44));
        Assert.Equal(3, BitConverter.ToInt16(bytes, 48));
    }

    [Fact]
    public void StartPastEndProducesEmptyData()
    {
        var pcm = new short[] { 1, 2, 3 };
        var path = TempPath();
        WavWriter.WriteSlice(path, pcm, 16000, 2, 1); // end < start → clamped to empty

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(44, bytes.Length);
        Assert.Equal(0, BitConverter.ToInt32(bytes, 40));
        Assert.Equal(36, BitConverter.ToInt32(bytes, 4));
    }

    [Fact]
    public void HonorsCustomSampleRate()
    {
        var pcm = new short[] { 7 };
        var path = TempPath();
        WavWriter.WriteSlice(path, pcm, 44100, 0, 1);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(44100, BitConverter.ToInt32(bytes, 24));
        Assert.Equal(88200, BitConverter.ToInt32(bytes, 28));
    }
}
