using System.Security.Cryptography;
using FluentAssertions;
using Xunit;

namespace KusPus.Whisper.Tests;

public class WhisperRunnerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _whisperDir;

    public WhisperRunnerTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "KusPus_WhisperRunnerTests_" + Guid.NewGuid().ToString("N"));
        _whisperDir = Path.Combine(_tempDir, "whisper");
        Directory.CreateDirectory(_whisperDir);
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

    // ── BuildArgumentList ────────────────────────────────────────────────────

    [Fact]
    public void BuildArgumentList_matches_TECH_SPEC_section_15()
    {
        var args = WhisperRunner.BuildArgumentList(@"C:\m.bin", @"C:\rec.wav", threads: 4);

        args.Should().Equal(
            "-m", @"C:\m.bin",
            "-f", @"C:\rec.wav",
            "-nt",
            "--output-txt",
            "-l", "en",
            "-t", "4");
    }

    [Fact]
    public void BuildArgumentList_serialises_thread_count_with_invariant_culture()
    {
        // Sanity: not affected by a comma-as-thousands-separator locale.
        var args = WhisperRunner.BuildArgumentList(@"m", @"w", threads: 1234);
        args[^1].Should().Be("1234");
    }

    // ── ChooseThreadCount ────────────────────────────────────────────────────

    [Theory]
    [InlineData("ggml-tiny.en", 8, 4)]       // cap at 4 for tiny.en
    [InlineData("ggml-tiny", 16, 4)]         // cap at 4 for tiny (multilingual)
    [InlineData("ggml-base.en", 8, 7)]       // ProcessorCount - 1 wins
    [InlineData("ggml-base.en", 16, 8)]      // cap at 8 for larger
    [InlineData("ggml-medium", 32, 8)]       // cap at 8 for larger
    [InlineData("ggml-tiny.en", 2, 2)]       // never drops below 2
    [InlineData("ggml-large-v3", 1, 2)]      // never drops below 2 even on 1-core
    public void ChooseThreadCount_obeys_caps_and_floor(string modelId, int procs, int expected)
    {
        WhisperRunner.ChooseThreadCount(modelId, procs).Should().Be(expected);
    }

    // ── TranscribeAsync early-validation paths ───────────────────────────────

    [Fact]
    public async Task TranscribeAsync_fails_when_whisper_exe_missing()
    {
        var runner = MakeRunnerWithoutWhisperExe();
        var result = await runner.TranscribeAsync(
            wavPath: Path.Combine(_tempDir, "in.wav"),
            model: MakeResolvedModel());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("whisper.exe not found");
    }

    [Fact]
    public async Task TranscribeAsync_fails_when_model_file_missing()
    {
        WriteFakeWhisperExe(out var sha);
        var runner = new WhisperRunner(_whisperDir, sha);
        var modelPath = Path.Combine(_tempDir, "missing-model.bin");
        File.WriteAllBytes(Path.Combine(_tempDir, "in.wav"), [0]);

        var result = await runner.TranscribeAsync(
            wavPath: Path.Combine(_tempDir, "in.wav"),
            model: new ResolvedModel(MakeDescriptor(), modelPath));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Model file not found");
    }

    [Fact]
    public async Task TranscribeAsync_fails_when_audio_file_missing()
    {
        WriteFakeWhisperExe(out var sha);
        var runner = new WhisperRunner(_whisperDir, sha);
        var modelPath = Path.Combine(_tempDir, "model.bin");
        File.WriteAllBytes(modelPath, [0]);

        var result = await runner.TranscribeAsync(
            wavPath: Path.Combine(_tempDir, "missing.wav"),
            model: new ResolvedModel(MakeDescriptor(), modelPath));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Audio file not found");
    }

    // ── SHA integrity ────────────────────────────────────────────────────────

    [Fact]
    public async Task TranscribeAsync_fails_when_whisper_sha_mismatches()
    {
        WriteFakeWhisperExe(out _);
        const string wrongSha = "deadbeef00000000000000000000000000000000000000000000000000000000";
        var runner = new WhisperRunner(_whisperDir, wrongSha);

        var modelPath = Path.Combine(_tempDir, "model.bin");
        var wavPath = Path.Combine(_tempDir, "in.wav");
        File.WriteAllBytes(modelPath, [0]);
        File.WriteAllBytes(wavPath, [0]);

        var result = await runner.TranscribeAsync(wavPath, new ResolvedModel(MakeDescriptor(), modelPath));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("integrity check failed");
    }

    [Fact]
    public async Task TranscribeAsync_caches_integrity_failure_across_calls()
    {
        WriteFakeWhisperExe(out _);
        var runner = new WhisperRunner(_whisperDir,
            expectedWhisperSha256: "0000000000000000000000000000000000000000000000000000000000000000");

        var modelPath = Path.Combine(_tempDir, "model.bin");
        var wavPath = Path.Combine(_tempDir, "in.wav");
        File.WriteAllBytes(modelPath, [0]);
        File.WriteAllBytes(wavPath, [0]);
        var model = new ResolvedModel(MakeDescriptor(), modelPath);

        var first = await runner.TranscribeAsync(wavPath, model);
        var second = await runner.TranscribeAsync(wavPath, model);

        first.Success.Should().BeFalse();
        second.Success.Should().BeFalse();
        // Same message means the cached failure short-circuits subsequent calls.
        second.Error.Should().Be(first.Error);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private WhisperRunner MakeRunnerWithoutWhisperExe() =>
        new(_whisperDir, expectedWhisperSha256: "irrelevant");

    private void WriteFakeWhisperExe(out string sha256Hex)
    {
        // "Fake whisper.exe" — bytes are arbitrary; we only need a real file to SHA.
        var bytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0xDE, 0xAD, 0xBE, 0xEF };
        var path = Path.Combine(_whisperDir, "whisper.exe");
        File.WriteAllBytes(path, bytes);
        sha256Hex = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static ModelDescriptor MakeDescriptor() => new(
        Id: "ggml-tiny.en",
        DisplayName: "Tiny (English)",
        FileName: "ggml-tiny.en.bin",
        SizeBytes: 1,
        Sha256: "irrelevant",
        Url: string.Empty,
        Bundled: true);

    private ResolvedModel MakeResolvedModel() =>
        new(MakeDescriptor(), Path.Combine(_tempDir, "model.bin"));
}
