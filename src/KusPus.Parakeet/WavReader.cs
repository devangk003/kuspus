using System.Buffers.Binary;
using KusPus.Core;

namespace KusPus.Parakeet;

/// <summary>Decoded PCM audio: mono float samples in −1..1 plus the source rate.</summary>
public sealed record PcmAudio(int SampleRate, float[] Samples);

/// <summary>
/// Minimal RIFF/WAVE reader for what KusPus' own recorders write (16-bit PCM, mono,
/// 16 kHz — see <c>AudioRecorder</c> / <c>PcmWavWriter</c>). Walks the chunk list rather
/// than assuming a 44-byte header, accepts WAVE_FORMAT_EXTENSIBLE, and down-mixes extra
/// channels by averaging so a foreign stereo file still transcribes.
/// </summary>
public static class WavReader
{
    private const int PcmFormat = 1;
    private const int ExtensibleFormat = 0xFFFE;

    public static Result<PcmAudio> Read(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            return Result.Fail<PcmAudio>($"Could not read audio file {path}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result.Fail<PcmAudio>($"Could not read audio file {path}: {ex.Message}", ex);
        }
        return Parse(bytes);
    }

    public static Result<PcmAudio> Parse(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
        {
            return Result.Fail<PcmAudio>("Not a RIFF/WAVE file.");
        }

        int channels = 0, sampleRate = 0, bitsPerSample = 0, format = 0;
        ReadOnlySpan<byte> data = default;
        bool haveFmt = false, haveData = false;

        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.Slice(pos, 4);
            // uint on disk: 0xFFFFFFFF (streaming writers) or ≥2 GB must not wrap negative.
            long size = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(pos + 4, 4));
            int bodyStart = pos + 8;
            int available = (int)Math.Max(0, Math.Min(size, wav.Length - bodyStart));
            var body = wav.Slice(bodyStart, available);

            if (id.SequenceEqual("fmt "u8) && body.Length >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(body[..2]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..4]);
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..8]);
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(body[14..16]);
                if (format == ExtensibleFormat && body.Length >= 26)
                {
                    // Sub-format GUID's first two bytes carry the real format tag.
                    format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..26]);
                }
                haveFmt = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = body; // a streaming writer may leave size as 0xFFFFFFFF → clamped above
                haveData = true;
            }

            // Chunks are word-aligned.
            pos = bodyStart + available + (size % 2 == 1 && available == size ? 1 : 0);
        }

        if (!haveFmt || !haveData)
        {
            return Result.Fail<PcmAudio>("WAVE file is missing its fmt or data chunk.");
        }
        if (format != PcmFormat || bitsPerSample != 16)
        {
            return Result.Fail<PcmAudio>($"Unsupported WAVE encoding (format {format}, {bitsPerSample}-bit); expected 16-bit PCM.");
        }
        if (channels < 1 || sampleRate <= 0)
        {
            return Result.Fail<PcmAudio>($"Invalid WAVE header (channels {channels}, rate {sampleRate}).");
        }

        int frameBytes = channels * 2;
        int frames = data.Length / frameBytes;
        var samples = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            int acc = 0;
            for (int c = 0; c < channels; c++)
            {
                acc += BinaryPrimitives.ReadInt16LittleEndian(data.Slice((i * frameBytes) + (c * 2), 2));
            }
            samples[i] = acc / (float)channels / 32768f;
        }
        return Result.Ok(new PcmAudio(sampleRate, samples));
    }
}
