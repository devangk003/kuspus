// ModelManager logs only on first-call manifest load, on download completion, and on
// download failures — same "startup-only" log pattern as PrefsStore/HistoryStore.
// LoggerMessage source-gen boilerplate is overkill for that volume.
#pragma warning disable CA1848 // Use the LoggerMessage delegates
#pragma warning disable CA1873 // Avoid potentially expensive logging argument evaluation

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using KusPus.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KusPus.Whisper;

/// <summary>
/// Embedded-manifest-driven model manager. See TECH_SPEC §18.
///
/// HTTP is injected (the caller owns the <see cref="HttpClient"/> lifecycle). Phase 11
/// wraps the client with an allowlist-enforcing handler; for Phase 3 the manager is
/// HTTP-agnostic.
///
/// Resumable downloads: if a previous attempt left an <c>&lt;file&gt;.tmp</c> on disk,
/// the next call sends an HTTP <c>Range</c> header and continues from the existing
/// byte offset. The SHA-256 hasher is seeded by re-reading the partial bytes so the
/// final SHA covers the full payload.
/// </summary>
public sealed class ModelManager : IModelManager
{
    private const string EmbeddedManifestResource = "KusPus.Whisper.Resources.models.json";

    private readonly string _modelsDirectory;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ModelManager> _logger;

    public ModelManager(
        string modelsDirectory,
        HttpClient httpClient,
        ModelManifest? manifestOverride = null,
        ILogger<ModelManager>? logger = null)
    {
        _modelsDirectory = modelsDirectory;
        _httpClient = httpClient;
        _logger = logger ?? NullLogger<ModelManager>.Instance;
        Manifest = manifestOverride ?? LoadEmbeddedManifest();
    }

    public ModelManifest Manifest { get; }

    public Result<ResolvedModel> Resolve(string activeModelId, string? customModelPath)
    {
        if (string.Equals(activeModelId, "custom", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(customModelPath) || !File.Exists(customModelPath))
            {
                return Result.Fail<ResolvedModel>(
                    "Active model is 'custom' but customModelPath is missing or doesn't exist.");
            }

            // Synthesised descriptor for a user-supplied model. SHA is empty because there's no
            // manifest entry to verify against (TECH_SPEC §18: "Custom models are not verified").
            // IsInstalledAsync treats an empty SHA as installed for exactly this case.
            var descriptor = new ModelDescriptor(
                Id: "custom",
                DisplayName: "Custom (" + Path.GetFileName(customModelPath) + ")",
                FileName: Path.GetFileName(customModelPath),
                SizeBytes: new FileInfo(customModelPath).Length,
                Sha256: string.Empty,
                Url: string.Empty,
                Bundled: false);
            return Result.Ok(new ResolvedModel(descriptor, customModelPath));
        }

        var found = Manifest.Models.FirstOrDefault(m =>
            string.Equals(m.Id, activeModelId, StringComparison.Ordinal));
        if (found is null)
        {
            return Result.Fail<ResolvedModel>($"Model '{activeModelId}' not found in manifest.");
        }

        var path = Path.Combine(_modelsDirectory, found.FileName);
        if (!File.Exists(path))
        {
            return Result.Fail<ResolvedModel>($"Model file missing on disk: {path}");
        }

        return Result.Ok(new ResolvedModel(found, path));
    }

    public async Task<bool> IsInstalledAsync(ModelDescriptor model, CancellationToken ct = default)
    {
        var path = Path.Combine(_modelsDirectory, model.FileName);
        if (!File.Exists(path))
        {
            return false;
        }

        // Empty SHA == "unverified" (synthetic 'custom' descriptor only — manifest entries
        // never legitimately have empty SHA at release time).
        if (string.IsNullOrEmpty(model.Sha256))
        {
            return true;
        }

        var actual = await ComputeSha256Async(path, ct).ConfigureAwait(false);
        return string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Result<string>> DownloadAsync(
        ModelDescriptor model,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(model.Url))
        {
            return Result.Fail<string>("Model has no URL (likely the 'custom' descriptor).");
        }

        try
        {
            Directory.CreateDirectory(_modelsDirectory);
            var finalPath = Path.Combine(_modelsDirectory, model.FileName);
            var tempPath = finalPath + ".tmp";

            // ── Resume preparation ──────────────────────────────────────────
            long existingBytes = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;

            using var request = new HttpRequestMessage(HttpMethod.Get, model.Url);
            // §18: "Header Accept-Encoding: identity (no compression — whisper models are
            // pre-compressed binaries)."
            request.Headers.AcceptEncoding.Clear();
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (existingBytes > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existingBytes, null);
            }

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // The server may decline to honour Range (returns 200 instead of 206).
            // In that case discard our partial file and start over.
            bool resuming = response.StatusCode == HttpStatusCode.PartialContent && existingBytes > 0;
            if (!resuming && existingBytes > 0)
            {
                _logger.LogInformation(
                    "Server returned {Status} for Range request on {Id}; restarting download from byte 0.",
                    (int)response.StatusCode, model.Id);
                existingBytes = 0;
            }

            // For 206 the server reports the partial range size in Content-Length; we want
            // the total. Prefer ContentRange.Length, then header ContentLength, then manifest size.
            long totalBytes = response.Content.Headers.ContentRange?.Length
                ?? (response.Content.Headers.ContentLength + existingBytes)
                ?? model.SizeBytes;

            string actualSha;
            await using (var inStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            {
                using var sha = SHA256.Create();

                // Seed the hasher with any bytes already on disk from a previous attempt.
                if (resuming)
                {
                    await SeedSha256FromPartialAsync(sha, tempPath, ct).ConfigureAwait(false);
                }

                // FileMode.Append continues at EOF for resume; otherwise overwrite from byte 0.
                await using var fileStream = new FileStream(
                    tempPath,
                    resuming ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);

                var buffer = new byte[81_920];
                long downloaded = existingBytes;
                var progressStopwatch = Stopwatch.StartNew();
                int read;
                while ((read = await inStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    downloaded += read;

                    // §18: "Progress events at 1 Hz to the UI."
                    if (progressStopwatch.ElapsedMilliseconds >= 1_000)
                    {
                        progress?.Report(new DownloadProgress(downloaded, totalBytes));
                        progressStopwatch.Restart();
                    }
                }
                progress?.Report(new DownloadProgress(downloaded, totalBytes));

                sha.TransformFinalBlock([], 0, 0);
                actualSha = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            }

            if (!string.Equals(actualSha, model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);
                _logger.LogWarning(
                    "Model {Id} SHA mismatch (expected {Expected}, got {Actual}); discarded.",
                    model.Id, model.Sha256, actualSha);
                return Result.Fail<string>(
                    $"SHA-256 mismatch downloading {model.Id}: expected {model.Sha256}, got {actualSha}");
            }

            // Atomic install. File.Move(overwrite: true) maps to MoveFileEx with
            // MOVEFILE_REPLACE_EXISTING — atomic on NTFS, equivalent to File.Replace
            // minus the backup-file feature we don't need.
            File.Move(tempPath, finalPath, overwrite: true);

            _logger.LogInformation("Downloaded model {Id} to {Path}.", model.Id, finalPath);
            return Result.Ok(finalPath);
        }
        catch (HttpRequestException ex)
        {
            return Result.Fail<string>($"HTTP error downloading {model.Id}: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            return Result.Fail<string>($"I/O error downloading {model.Id}: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex)
        {
            return Result.Fail<string>("Download cancelled.", ex);
        }
    }

    // ── private ──────────────────────────────────────────────────────────────

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task SeedSha256FromPartialAsync(SHA256 sha, string partialPath, CancellationToken ct)
    {
        await using var partial = File.OpenRead(partialPath);
        var seedBuf = new byte[81_920];
        int read;
        while ((read = await partial.ReadAsync(seedBuf, ct).ConfigureAwait(false)) > 0)
        {
            sha.TransformBlock(seedBuf, 0, read, null, 0);
        }
    }

    private static ModelManifest LoadEmbeddedManifest()
    {
        var assembly = typeof(ModelManager).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedManifestResource)
            ?? throw new InvalidOperationException(
                $"Embedded manifest '{EmbeddedManifestResource}' not found in assembly.");
        return JsonSerializer.Deserialize<ModelManifest>(stream, ManifestJsonOptions)
            ?? throw new InvalidOperationException("Failed to parse embedded models.json.");
    }

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
