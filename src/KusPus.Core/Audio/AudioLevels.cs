namespace KusPus.Core.Audio;

/// <summary>
/// Level-meter maths shared by the Windows (NAudio) and macOS (AVAudioEngine)
/// recorders. The pill renders <see cref="LevelChannels"/> bars at ~15 Hz.
/// </summary>
public static class AudioLevels
{
    /// <summary>Number of RMS bands the pill's meter expects per frame.</summary>
    public const int LevelChannels = 20;

    /// <summary>Target post interval (~15 Hz).</summary>
    public const int LevelPostIntervalMs = 66;

    /// <summary>
    /// Computes <paramref name="rms"/>.Length-band RMS over a 16-bit little-endian
    /// mono PCM buffer. Each band covers an equal slice of the buffer; values are
    /// normalised to 0..1 (full-scale sine ≈ 0.707).
    /// </summary>
    public static void ComputeRms(ReadOnlySpan<byte> pcm16Mono, Span<float> rms)
    {
        if (rms.Length == 0)
        {
            return;
        }

        int sampleCount = pcm16Mono.Length / 2;
        int samplesPerWindow = Math.Max(1, sampleCount / rms.Length);

        for (int w = 0; w < rms.Length; w++)
        {
            int start = w * samplesPerWindow;
            int end = Math.Min(start + samplesPerWindow, sampleCount);
            if (end <= start)
            {
                rms[w] = 0f;
                continue;
            }

            double sumSq = 0;
            for (int i = start; i < end; i++)
            {
                short s = (short)(pcm16Mono[i * 2] | (pcm16Mono[(i * 2) + 1] << 8));
                double normalised = s / (double)short.MaxValue;
                sumSq += normalised * normalised;
            }
            rms[w] = (float)Math.Sqrt(sumSq / (end - start));
        }
    }
}
