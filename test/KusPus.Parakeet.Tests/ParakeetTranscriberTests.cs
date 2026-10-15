using FluentAssertions;
using KusPus.Core;
using KusPus.Whisper;
using Xunit;

namespace KusPus.Parakeet.Tests;

public class ParakeetTranscriberTests : IDisposable
{
    private readonly string _tempDir;

    public ParakeetTranscriberTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KusPus_ParakeetTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static ModelDescriptor ParakeetDescriptor(params string[] fileNames) =>
        new("parakeet-test", "Parakeet", ModelEngine.Parakeet,
            fileNames.Select(n => new ModelFile(n, 1, "00", "https://huggingface.co/x/" + n)).ToList(),
            Bundled: false);

    private ResolvedModel Resolved(ModelDescriptor d) =>
        new(d, Path.Combine(_tempDir, d.Files[0].FileName), _tempDir);

    [Fact]
    public async Task Rejects_non_parakeet_models()
    {
        using var t = new ParakeetTranscriber();
        var whisper = ModelDescriptor.SingleFile("ggml-tiny.en", "Tiny", "ggml-tiny.en.bin", 1, "00", "https://x", true);

        var r = await t.TranscribeAsync("x.wav", new ResolvedModel(whisper, Path.Combine(_tempDir, "ggml-tiny.en.bin")));

        r.Success.Should().BeFalse();
        r.Error.Should().Contain("not a Parakeet model");
        t.IsLoaded.Should().BeFalse();
    }

    [Fact]
    public async Task Fails_cleanly_when_manifest_entry_lacks_a_component()
    {
        using var t = new ParakeetTranscriber();
        var d = ParakeetDescriptor("encoder.int8.onnx", "decoder.int8.onnx", "tokens.txt"); // no joiner

        var r = await t.TranscribeAsync("x.wav", Resolved(d));

        r.Success.Should().BeFalse();
        r.Error.Should().Contain("encoder/decoder/joiner/tokens");
    }

    [Fact]
    public async Task Fails_cleanly_when_a_model_file_is_missing_on_disk()
    {
        using var t = new ParakeetTranscriber();
        var d = ParakeetDescriptor("encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt");
        foreach (var f in d.Files.Take(3))
        {
            File.WriteAllBytes(Path.Combine(_tempDir, f.FileName), [1]);
        }

        var r = await t.TranscribeAsync("x.wav", Resolved(d));

        r.Success.Should().BeFalse();
        r.Error.Should().Contain("tokens.txt");
        t.IsLoaded.Should().BeFalse();
    }

    [Fact]
    public async Task Empty_recording_returns_empty_text_without_loading_the_model()
    {
        using var t = new ParakeetTranscriber();
        var d = ParakeetDescriptor("encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt");
        foreach (var f in d.Files)
        {
            File.WriteAllBytes(Path.Combine(_tempDir, f.FileName), [1]);
        }
        var wav = Path.Combine(_tempDir, "empty.wav");
        File.WriteAllBytes(wav, WavReaderTests.Pcm16Wav(16000, 1, []));

        var r = await t.TranscribeAsync(wav, Resolved(d));

        r.Success.Should().BeTrue();
        r.Value.Should().BeEmpty();
        t.IsLoaded.Should().BeFalse("no native model should be loaded for zero samples");
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 2)]
    [InlineData(10, 5)]
    [InlineData(16, 8)]
    [InlineData(32, 8)]
    public void Thread_count_is_half_the_cores_clamped_to_2_8(int cores, int expected) =>
        ParakeetTranscriber.ChooseThreadCount(cores).Should().Be(expected);
}

public class EngineRouterTests
{
    private sealed class Stub(string name) : ITranscriber
    {
        public Task<Result<string>> TranscribeAsync(string wavPath, ResolvedModel model, CancellationToken ct = default) =>
            Task.FromResult(Result.Ok(name));
    }

    [Fact]
    public async Task Routes_by_descriptor_engine()
    {
        var router = new EngineRouter(new Stub("whisper"), new Stub("parakeet"));
        var whisper = ModelDescriptor.SingleFile("ggml-tiny.en", "Tiny", "ggml-tiny.en.bin", 1, "00", "https://x", true);
        var parakeet = new ModelDescriptor("parakeet-tdt-0.6b-v2", "P", ModelEngine.Parakeet,
            [new ModelFile("encoder.int8.onnx", 1, "00", "https://x")], false);

        (await router.TranscribeAsync("a.wav", new ResolvedModel(whisper, "/m/ggml-tiny.en.bin"))).Value.Should().Be("whisper");
        (await router.TranscribeAsync("a.wav", new ResolvedModel(parakeet, "/m/p/encoder.int8.onnx", "/m/p"))).Value.Should().Be("parakeet");
    }
}
