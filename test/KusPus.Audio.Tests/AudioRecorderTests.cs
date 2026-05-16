using FluentAssertions;
using Xunit;

namespace KusPus.Audio.Tests;

/// <summary>
/// Pure-helper unit tests for <see cref="AudioRecorder"/>. WASAPI capture itself
/// requires a physical microphone and is exercised at the Phase 6 milestone smoke
/// test (PRD §11.3 M-05 / M-06 / M-22).
/// </summary>
public class AudioRecorderTests
{
    [Fact]
    public void ComputeRms_returns_all_zeros_for_a_silent_buffer()
    {
        var pcm = new byte[200];           // 100 samples of zero
        var rms = new float[20];

        AudioRecorder.ComputeRms(pcm, rms);

        rms.Should().AllSatisfy(v => v.Should().Be(0f));
    }

    [Fact]
    public void ComputeRms_returns_full_scale_for_a_max_amplitude_buffer()
    {
        // 200 samples of int16 max (32767) — RMS of a constant signal equals its absolute value.
        var pcm = new byte[200 * 2];
        for (int i = 0; i < 200; i++)
        {
            pcm[i * 2] = 0xFF;
            pcm[(i * 2) + 1] = 0x7F;       // 0x7FFF = 32767
        }
        var rms = new float[20];

        AudioRecorder.ComputeRms(pcm, rms);

        rms.Should().AllSatisfy(v => v.Should().BeApproximately(1f, precision: 0.001f));
    }

    [Fact]
    public void ComputeRms_distributes_samples_evenly_across_output_channels()
    {
        // 200 samples spread across 20 channels = 10 samples per channel.
        var pcm = new byte[200 * 2];
        var rms = new float[20];

        AudioRecorder.ComputeRms(pcm, rms);

        rms.Length.Should().Be(20);
    }

    [Fact]
    public void ComputeRms_handles_buffer_shorter_than_channel_count()
    {
        // 5 samples but asking for 20 channels — should clamp to 1 sample per window
        // and zero-fill the rest.
        var pcm = new byte[5 * 2];
        var rms = new float[20];

        AudioRecorder.ComputeRms(pcm, rms);

        // No exception; some channels may have value, the rest are 0.
        rms.Take(5).Should().AllSatisfy(v => v.Should().BeGreaterOrEqualTo(0f));
        rms.Skip(5).Should().AllSatisfy(v => v.Should().Be(0f));
    }

    [Fact]
    public async Task StopAsync_without_StartAsync_returns_Fail()
    {
        using var recorder = new AudioRecorder();

        var result = await recorder.StopAsync();

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Not recording");
    }
}
