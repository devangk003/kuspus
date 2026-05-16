using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Xunit;

namespace KusPus.Whisper.Tests;

public class ModelManagerTests : IDisposable
{
    private readonly string _tempDir;

    public ModelManagerTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "KusPus_ModelManagerTests_" + Guid.NewGuid().ToString("N"));
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

    private static readonly string[] Section18Models =
    [
        "ggml-tiny.en",
        "ggml-base.en",
        "ggml-small.en",
        "ggml-medium.en",
        "ggml-large-v3",
    ];

    [Fact]
    public void Embedded_manifest_contains_all_five_models_per_section_18()
    {
        using var client = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage()));
        var mgr = new ModelManager(_tempDir, client);

        mgr.Manifest.SchemaVersion.Should().Be(1);
        mgr.Manifest.Models.Select(m => m.Id).Should().BeEquivalentTo(Section18Models);
    }

    [Fact]
    public async Task DownloadAsync_sends_Accept_Encoding_identity_per_section_18()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03 };
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var manifest = new ModelManifest(
            SchemaVersion: 1,
            HfRepoCommit: "test",
            Models: new[]
            {
                new ModelDescriptor("test", "Test", "test.bin", payload.Length, sha, "https://example/test.bin", false),
            });

        string? seenAcceptEncoding = null;
        using var client = new HttpClient(new FakeHttpMessageHandler(req =>
        {
            seenAcceptEncoding = req.Headers.AcceptEncoding.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }));
        var mgr = new ModelManager(_tempDir, client, manifestOverride: manifest);

        await mgr.DownloadAsync(manifest.Models[0]);

        seenAcceptEncoding.Should().Be("identity");
    }

    [Fact]
    public async Task DownloadAsync_sends_Range_header_when_partial_tmp_exists_and_restarts_if_server_ignores_it()
    {
        var payload = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80 };
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var manifest = new ModelManifest(
            SchemaVersion: 1,
            HfRepoCommit: "test",
            Models: new[]
            {
                new ModelDescriptor("test", "Test", "test.bin", payload.Length, sha, "https://example/test.bin", false),
            });

        // Pre-seed a stale partial download.
        var tempPath = Path.Combine(_tempDir, "test.bin.tmp");
        File.WriteAllBytes(tempPath, new byte[] { 0xFF, 0xFF, 0xFF });

        var seenRange = (long?)null;
        using var client = new HttpClient(new FakeHttpMessageHandler(req =>
        {
            seenRange = req.Headers.Range?.Ranges.First().From;
            // Server doesn't honour Range — returns the FULL payload with 200.
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }));
        var mgr = new ModelManager(_tempDir, client, manifestOverride: manifest);

        var result = await mgr.DownloadAsync(manifest.Models[0]);

        seenRange.Should().Be(3, because: "the existing .tmp had 3 bytes");
        result.Success.Should().BeTrue();
        File.ReadAllBytes(Path.Combine(_tempDir, "test.bin"))
            .Should().Equal(payload, because: "the server-ignored-Range path restarts from byte 0");
    }

    [Fact]
    public void Resolve_returns_Fail_for_unknown_model_id()
    {
        var mgr = MakeManager();

        var result = mgr.Resolve("does-not-exist", customModelPath: null);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found in manifest");
    }

    [Fact]
    public void Resolve_returns_Fail_when_known_model_file_is_missing()
    {
        var mgr = MakeManager();

        var result = mgr.Resolve("ggml-tiny.en", customModelPath: null);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("missing on disk");
    }

    [Fact]
    public void Resolve_succeeds_when_known_model_file_exists()
    {
        var mgr = MakeManager();
        var path = Path.Combine(_tempDir, "ggml-tiny.en.bin");
        File.WriteAllBytes(path, [0xAB, 0xCD]);

        var result = mgr.Resolve("ggml-tiny.en", customModelPath: null);

        result.Success.Should().BeTrue();
        result.Value!.Descriptor.Id.Should().Be("ggml-tiny.en");
        result.Value!.Path.Should().Be(path);
    }

    [Fact]
    public void Resolve_uses_customModelPath_when_activeModelId_is_custom()
    {
        var mgr = MakeManager();
        var customPath = Path.Combine(_tempDir, "fine-tune.bin");
        File.WriteAllBytes(customPath, [0x12, 0x34, 0x56]);

        var result = mgr.Resolve("custom", customPath);

        result.Success.Should().BeTrue();
        result.Value!.Descriptor.Id.Should().Be("custom");
        result.Value!.Path.Should().Be(customPath);
    }

    [Fact]
    public void Resolve_returns_Fail_when_custom_is_active_but_path_is_missing()
    {
        var mgr = MakeManager();

        var result = mgr.Resolve("custom", customModelPath: null);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task IsInstalledAsync_returns_false_when_file_missing()
    {
        var mgr = MakeManager();
        var descriptor = mgr.Manifest.Models[0];

        (await mgr.IsInstalledAsync(descriptor)).Should().BeFalse();
    }

    [Fact]
    public async Task IsInstalledAsync_returns_true_when_file_present_and_sha_matches()
    {
        // Hand-roll a descriptor whose SHA matches the bytes we'll write.
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var manifest = new ModelManifest(
            SchemaVersion: 1,
            HfRepoCommit: "test",
            Models: new[]
            {
                new ModelDescriptor("test", "Test", "test.bin", bytes.Length, sha, "https://example/", false),
            });

        var mgr = MakeManager(manifestOverride: manifest);
        File.WriteAllBytes(Path.Combine(_tempDir, "test.bin"), bytes);

        (await mgr.IsInstalledAsync(manifest.Models[0])).Should().BeTrue();
    }

    [Fact]
    public async Task IsInstalledAsync_returns_false_when_sha_mismatches()
    {
        var manifest = new ModelManifest(
            SchemaVersion: 1,
            HfRepoCommit: "test",
            Models: new[]
            {
                new ModelDescriptor(
                    "test", "Test", "test.bin", 5,
                    Sha256: "deadbeef0000000000000000000000000000000000000000000000000000beef",
                    Url: "https://example/", Bundled: false),
            });

        var mgr = MakeManager(manifestOverride: manifest);
        File.WriteAllBytes(Path.Combine(_tempDir, "test.bin"), new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });

        (await mgr.IsInstalledAsync(manifest.Models[0])).Should().BeFalse();
    }

    [Fact]
    public async Task DownloadAsync_succeeds_when_response_bytes_match_sha()
    {
        var payload = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 };
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var manifest = new ModelManifest(
            SchemaVersion: 1,
            HfRepoCommit: "test",
            Models: new[]
            {
                new ModelDescriptor("test", "Test", "test.bin", payload.Length, sha, "https://example/test.bin", false),
            });

        using var client = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        }));
        var mgr = new ModelManager(_tempDir, client, manifestOverride: manifest);

        var result = await mgr.DownloadAsync(manifest.Models[0]);

        result.Success.Should().BeTrue();
        var finalPath = Path.Combine(_tempDir, "test.bin");
        File.Exists(finalPath).Should().BeTrue();
        File.ReadAllBytes(finalPath).Should().Equal(payload);
    }

    [Fact]
    public async Task DownloadAsync_deletes_temp_and_fails_when_sha_mismatches()
    {
        var manifest = new ModelManifest(
            SchemaVersion: 1,
            HfRepoCommit: "test",
            Models: new[]
            {
                new ModelDescriptor(
                    "test", "Test", "test.bin", 5,
                    Sha256: "deadbeef0000000000000000000000000000000000000000000000000000beef",
                    Url: "https://example/test.bin", Bundled: false),
            });

        using var client = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0xAA, 0xBB, 0xCC }),
        }));
        var mgr = new ModelManager(_tempDir, client, manifestOverride: manifest);

        var result = await mgr.DownloadAsync(manifest.Models[0]);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("SHA-256 mismatch");
        File.Exists(Path.Combine(_tempDir, "test.bin.tmp")).Should().BeFalse();
        File.Exists(Path.Combine(_tempDir, "test.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task DownloadAsync_returns_Fail_on_HTTP_error()
    {
        var manifest = new ModelManifest(
            SchemaVersion: 1,
            HfRepoCommit: "test",
            Models: new[]
            {
                new ModelDescriptor("test", "Test", "test.bin", 0, "ignored", "https://example/test.bin", false),
            });

        using var client = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var mgr = new ModelManager(_tempDir, client, manifestOverride: manifest);

        var result = await mgr.DownloadAsync(manifest.Models[0]);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("HTTP error");
    }

    [Fact]
    public async Task DownloadAsync_reports_progress_at_least_once()
    {
        // 2 MB payload — the impl throttles to ~1 MB intervals + final report.
        var payload = new byte[2 * 1024 * 1024];
        new Random(42).NextBytes(payload);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var manifest = new ModelManifest(
            SchemaVersion: 1,
            HfRepoCommit: "test",
            Models: new[]
            {
                new ModelDescriptor("test", "Test", "test.bin", payload.Length, sha, "https://example/test.bin", false),
            });

        using var client = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        }));
        var mgr = new ModelManager(_tempDir, client, manifestOverride: manifest);

        var reports = new List<DownloadProgress>();
        var progress = new Progress<DownloadProgress>(p => reports.Add(p));

        var result = await mgr.DownloadAsync(manifest.Models[0], progress);

        result.Success.Should().BeTrue();
        // Progress<T> dispatches asynchronously; give it a moment.
        await Task.Delay(50);
        reports.Should().NotBeEmpty();
        reports.Last().BytesDownloaded.Should().Be(payload.Length);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private ModelManager MakeManager(ModelManifest? manifestOverride = null)
    {
        var client = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotImplemented)));
        return new ModelManager(_tempDir, client, manifestOverride);
    }
}
