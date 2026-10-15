using System.Buffers.Binary;
using FluentAssertions;
using Xunit;

namespace KusPus.Parakeet.Tests;

public class WavReaderTests
{
    internal static byte[] Pcm16Wav(int sampleRate, int channels, short[] interleaved, bool extensible = false, bool oddChunkBefore = false)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = interleaved.Length * 2;
        w.Write("RIFF"u8);
        w.Write(0); // patched below
        w.Write("WAVE"u8);
        if (oddChunkBefore)
        {
            w.Write("LIST"u8);
            w.Write(3);
            w.Write(new byte[] { 1, 2, 3, 0 }); // 3-byte body + pad
        }
        w.Write("fmt "u8);
        w.Write(extensible ? 40 : 16);
        w.Write((ushort)(extensible ? 0xFFFE : 1));
        w.Write((ushort)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 2);
        w.Write((ushort)(channels * 2));
        w.Write((ushort)16);
        if (extensible)
        {
            w.Write((ushort)22);    // cbSize
            w.Write((ushort)16);    // valid bits
            w.Write(0);             // channel mask
            w.Write((ushort)1);     // sub-format tag = PCM
            w.Write(new byte[14]);  // rest of the GUID
        }
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var s in interleaved)
        {
            w.Write(s);
        }
        w.Flush();
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        return bytes;
    }

    [Fact]
    public void Parses_16k_mono_pcm_and_scales_to_unit_range()
    {
        var wav = Pcm16Wav(16000, 1, [0, 16384, -32768, 32767]);

        var r = WavReader.Parse(wav);

        r.Success.Should().BeTrue();
        r.Value!.SampleRate.Should().Be(16000);
        r.Value.Samples.Should().Equal(0f, 0.5f, -1f, 32767f / 32768f);
    }

    [Fact]
    public void Downmixes_stereo_by_averaging()
    {
        var wav = Pcm16Wav(48000, 2, [1000, 3000, -2000, 2000]);

        var r = WavReader.Parse(wav);

        r.Success.Should().BeTrue();
        r.Value!.SampleRate.Should().Be(48000);
        r.Value.Samples.Should().Equal(2000f / 32768f, 0f);
    }

    [Fact]
    public void Accepts_wave_format_extensible_and_skips_unknown_chunks()
    {
        var wav = Pcm16Wav(16000, 1, [123], extensible: true, oddChunkBefore: true);

        var r = WavReader.Parse(wav);

        r.Success.Should().BeTrue();
        r.Value!.Samples.Should().Equal(123f / 32768f);
    }

    [Fact]
    public void Rejects_non_riff_and_non_pcm()
    {
        WavReader.Parse(new byte[] { 1, 2, 3 }).Success.Should().BeFalse();

        var wav = Pcm16Wav(16000, 1, [1]);
        wav[20] = 3; // format tag → IEEE float
        var r = WavReader.Parse(wav);
        r.Success.Should().BeFalse();
        r.Error.Should().Contain("Unsupported");
    }

    [Fact]
    public void Read_returns_Fail_for_missing_file()
    {
        var r = WavReader.Read(Path.Combine(Path.GetTempPath(), "KusPus_nope_" + Guid.NewGuid().ToString("N") + ".wav"));
        r.Success.Should().BeFalse();
    }

    [Fact]
    public void Streaming_data_size_0xFFFFFFFF_reads_to_end_of_file()
    {
        var wav = Pcm16Wav(16000, 1, [0, 16384]);
        // data chunk size field sits right before the samples.
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(wav.Length - 4 - 4), 0xFFFFFFFF);

        var r = WavReader.Parse(wav);

        r.Success.Should().BeTrue();
        r.Value!.Samples.Should().Equal(0f, 0.5f);
    }
}
