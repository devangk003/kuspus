using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Xunit;

namespace KusPus.Mac.Platform.Tests;

public class PcmWavWriterTests
{
    [Fact]
    public void Writes_a_valid_16kHz_mono_pcm16_header()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kuspus-wavtest-{Guid.NewGuid():N}.wav");
        try
        {
            var samples = new byte[3200]; // 100 ms of 16 kHz int16
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (byte)i;
            }

            using (var w = new PcmWavWriter(path, 16_000, 1))
            {
                w.Write(samples.AsSpan(0, 1000));
                w.Write(samples.AsSpan(1000));
                w.BytesWritten.Should().Be(3200);
            }

            var bytes = File.ReadAllBytes(path);
            bytes.Length.Should().Be(44 + 3200);
            Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("RIFF");
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)).Should().Be(36 + 3200);
            Encoding.ASCII.GetString(bytes, 8, 4).Should().Be("WAVE");
            Encoding.ASCII.GetString(bytes, 12, 4).Should().Be("fmt ");
            BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(20)).Should().Be(1);        // PCM
            BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22)).Should().Be(1);        // mono
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)).Should().Be(16_000);
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(28)).Should().Be(32_000);   // byte rate
            BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(32)).Should().Be(2);        // block align
            BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34)).Should().Be(16);       // bits
            Encoding.ASCII.GetString(bytes, 36, 4).Should().Be("data");
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)).Should().Be(3200);
            bytes.AsSpan(44).ToArray().Should().Equal(samples);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
