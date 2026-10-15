using FluentAssertions;
using KusPus.Whisper;
using Xunit;
using Xunit.Abstractions;

namespace KusPus.Parakeet.Tests;

/// <summary>
/// Runs the real sherpa-onnx runtime against a real Parakeet model. Skipped unless both
/// <c>KUSPUS_PARAKEET_MODEL_DIR</c> (a directory holding encoder/decoder/joiner/tokens)
/// and <c>KUSPUS_PARAKEET_TEST_WAV</c> (16 kHz mono speech saying the word "Parakeet")
/// are set — CI has neither the 650 MB model nor a microphone.
/// </summary>
public class ParakeetIntegrationTests(ITestOutputHelper output)
{
    private static readonly string? ModelDir = Environment.GetEnvironmentVariable("KUSPUS_PARAKEET_MODEL_DIR");
    private static readonly string? TestWav = Environment.GetEnvironmentVariable("KUSPUS_PARAKEET_TEST_WAV");

    [Fact]
    public async Task Real_model_transcribes_synthesised_speech_with_punctuation_and_casing()
    {
        if (string.IsNullOrEmpty(ModelDir) || string.IsNullOrEmpty(TestWav) || !Directory.Exists(ModelDir) || !File.Exists(TestWav))
        {
            return; // opt-in only
        }

        var files = new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" };
        var descriptor = new ModelDescriptor("parakeet-local", "Parakeet (local)", ModelEngine.Parakeet,
            files.Select(f => new ModelFile(f, new FileInfo(Path.Combine(ModelDir, f)).Length, string.Empty, string.Empty)).ToList(),
            Bundled: false);
        var model = new ResolvedModel(descriptor, Path.Combine(ModelDir, files[0]), ModelDir);
        using var transcriber = new ParakeetTranscriber(idleUnload: TimeSpan.FromSeconds(2));

        var first = await transcriber.TranscribeAsync(TestWav, model);
        first.Success.Should().BeTrue(first.Error);
        output.WriteLine("Transcript: " + first.Value);
        first.Value.Should().ContainEquivalentOf("parakeet");
        first.Value.Should().MatchRegex("[.!?]"); // v2/v3 emit punctuation
        first.Value![0].Should().Be(char.ToUpperInvariant(first.Value[0]), "v2/v3 emit casing");
        transcriber.IsLoaded.Should().BeTrue();

        // Second call reuses the resident recognizer and agrees with the first.
        var second = await transcriber.TranscribeAsync(TestWav, model);
        second.Value.Should().Be(first.Value);

        // Idle unload releases the native recognizer.
        await Task.Delay(TimeSpan.FromSeconds(3));
        transcriber.IsLoaded.Should().BeFalse();
    }
}
