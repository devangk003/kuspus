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
using System.Text.Json.Serialization;
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
/// A model is a set of files (<see cref="ModelDescriptor.Files"/>) under
/// <c>&lt;models dir&gt;/&lt;Subdirectory&gt;</c>. Each file downloads independently and
/// resumably: if a previous attempt left an <c>&lt;file&gt;.tmp</c> on disk, the next call
/// sends an HTTP <c>Range</c> header and continues from the existing byte offset. The
/// SHA-256 hasher is seeded by re-reading the partial bytes so the final SHA covers the
/// full payload. Files that already match their manifest hash are skipped, so a
/// multi-file download that died on file 3 doesn't refetch files 1–2.
/// </summary>
public sealed class ModelManager : IModelManager
{
    private const string EmbeddedManifestResource = "KusPus.Whisper.Resources.models.json";

    private readonly string _modelsDirectory;
    private readonly string? _bundledModelsDirectory;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ModelManager> _logger;

    /// <param name="modelsDirectory">Writable directory downloads land in.</param>
    /// <param name="bundledModelsDirectory">
    /// Optional read-only directory searched <em>before</em> <paramref name="modelsDirectory"/>
    /// when resolving a model. On Windows the installer drops <c>tiny.en</c> into the same
    /// writable dir so this is <c>null</c>; on macOS a signed <c>.app</c> bundle must not
    /// be modified, so the bundled model lives in <c>Contents/Resources</c> and downloads
    /// go to <c>~/Library/Application Support</c>.
    /// </param>
    public ModelManager(
        string modelsDirectory,
        HttpClient httpClient,
        ModelManifest? manifestOverride = null,
        ILogger<ModelManager>? logger = null,
        string? bundledModelsDirectory = null)
    {
        _modelsDirectory = modelsDirectory;
        _bundledModelsDirectory = bundledModelsDirectory;
        _httpClient = httpClient;
        _logger = logger ?? NullLogger<ModelManager>.Instance;
        Manifest = manifestOverride ?? LoadEmbeddedManifest();
    }

    public ModelManifest Manifest { get; }

    public string ModelsDirectory => _modelsDirectory;

    public long DownloadedSizeBytes(ModelDescriptor model)
    {
        var directory = Path.Combine(_modelsDirectory, model.Subdirectory);
        long total = 0;
        foreach (var file in model.Files)
        {
            var fi = new FileInfo(Path.Combine(directory, file.FileName));
            if (fi.Exists)
            {
                total += fi.Length;
            }
        }
        return total;
    }

    public Result<long> Remove(ModelDescriptor model)
    {
        var directory = Path.Combine(_modelsDirectory, model.Subdirectory);
        long freed = 0;
        try
        {
            foreach (var file in model.Files)
            {
                var path = Path.Combine(directory, file.FileName);
                var fi = new FileInfo(path);
                if (!fi.Exists)
                {
                    continue;
                }
                freed += fi.Length;
                File.Delete(path);
            }
            // Multi-file models live in their own folder; drop it once it is empty.
            if (model.Subdirectory.Length > 0 && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Removing model {Id} failed.", model.Id);
            return Result.Fail<long>($"Could not remove {model.DisplayName}: {ex.Message}");
        }
        _logger.LogInformation("Removed model {Id} ({Bytes} bytes).", model.Id, freed);
        return Result.Ok(freed);
    }

    /// <summary>
    /// First on-disk location holding <em>all</em> of a model's files: the writable models
    /// dir wins (a user-downloaded copy supersedes the bundled one), then the bundled dir.
    /// Returns the writable-dir location when neither is complete so callers report that path.
    /// </summary>
    private string LocateModelDirectory(ModelDescriptor model)
    {
        var writable = Path.Combine(_modelsDirectory, model.Subdirectory);
        if (_bundledModelsDirectory is null || AllFilesExist(writable, model))
        {
            return writable;
        }
        var bundled = Path.Combine(_bundledModelsDirectory, model.Subdirectory);
        return AllFilesExist(bundled, model) ? bundled : writable;
    }

    private static bool AllFilesExist(string directory, ModelDescriptor model) =>
        model.Files.All(f => File.Exists(Path.Combine(directory, f.FileName)));

    public Result<ResolvedModel> Resolve(string activeModelId, string? customModelPath)
    {
        if (string.Equals(activeModelId, "custom", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(customModelPath) || !File.Exists(customModelPath))
            {
                return Result.Fail<ResolvedModel>(
                    "Active model is 'custom' but customModelPath is missing or doesn't exist.");
            }

            // Synthesised descriptor for a user-supplied whisper model. SHA is empty because
            // there's no manifest entry to verify against (TECH_SPEC §18: "Custom models are
            // not verified"). IsInstalledAsync treats an empty SHA as installed for exactly this case.
            var descriptor = ModelDescriptor.SingleFile(
                id: "custom",
                displayName: "Custom (" + Path.GetFileName(customModelPath) + ")",
                fileName: Path.GetFileName(customModelPath),
                sizeBytes: new FileInfo(customModelPath).Length,
                sha256: string.Empty,
                url: string.Empty,
                bundled: false);
            return Result.Ok(new ResolvedModel(descriptor, customModelPath));
        }

        var found = Manifest.Models.FirstOrDefault(m =>
            string.Equals(m.Id, activeModelId, StringComparison.Ordinal));
        if (found is null)
        {
            return Result.Fail<ResolvedModel>($"Model '{activeModelId}' not found in manifest.");
        }

        var directory = LocateModelDirectory(found);
        foreach (var file in found.Files)
        {
            var path = Path.Combine(directory, file.FileName);
            if (!File.Exists(path))
            {
                return Result.Fail<ResolvedModel>($"Model file missing on disk: {path}");
            }
        }

        return Result.Ok(new ResolvedModel(found, Path.Combine(directory, found.PrimaryFile.FileName), directory));
    }

    public bool IsInstalled(ModelDescriptor model) => AllFilesExist(LocateModelDirectory(model), model);

    public async Task<bool> IsInstalledAsync(ModelDescriptor model, CancellationToken ct = default)
    {
        var directory = LocateModelDirectory(model);
        foreach (var file in model.Files)
        {
            if (!await FileMatchesAsync(Path.Combine(directory, file.FileName), file.Sha256, ct).ConfigureAwait(false))
            {
                return false;
            }
        }
        return true;
    }

    private static async Task<bool> FileMatchesAsync(string path, string expectedSha256, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        // Empty SHA == "unverified" (synthetic 'custom' descriptor only — manifest entries
        // never legitimately have empty SHA at release time).
        if (string.IsNullOrEmpty(expectedSha256))
        {
            return true;
        }

        var actual = await ComputeSha256Async(path, ct).ConfigureAwait(false);
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Result<string>> DownloadAsync(
        ModelDescriptor model,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (model.Files.Count == 0 || model.Files.Any(f => string.IsNullOrEmpty(f.Url)))
        {
            return Result.Fail<string>("Model has no URL (likely the 'custom' descriptor).");
        }

        var directory = Path.Combine(_modelsDirectory, model.Subdirectory);
        long grandTotal = model.SizeBytes;
        long completed = 0;
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var file in model.Files)
            {
                var finalPath = Path.Combine(directory, file.FileName);
                if (await FileMatchesAsync(finalPath, file.Sha256, ct).ConfigureAwait(false))
                {
                    completed += file.SizeBytes;
                    progress?.Report(new DownloadProgress(completed, grandTotal));
                    continue;
                }

                var result = await DownloadFileAsync(model.Id, file, finalPath, completed, grandTotal, progress, ct)
                    .ConfigureAwait(false);
                if (!result.Success)
                {
                    return Result.Fail<string>(result.Error!, result.Cause);
                }
                completed += file.SizeBytes;
            }

            _logger.LogInformation("Downloaded model {Id} ({Files} file(s)) to {Dir}.", model.Id, model.Files.Count, directory);
            return Result.Ok(Path.Combine(directory, model.PrimaryFile.FileName));
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

    /// <summary>
    /// One file: resumable GET → temp file → SHA check → atomic move. Progress is reported
    /// as <paramref name="baseOffset"/> + bytes of this file so the caller sees one bar.
    /// </summary>
    private async Task<Result<string>> DownloadFileAsync(
        string modelId,
        ModelFile file,
        string finalPath,
        long baseOffset,
        long grandTotal,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        try
        {
            var tempPath = finalPath + ".tmp";

            // ── Resume preparation ──────────────────────────────────────────
            long existingBytes = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;

            // A previous attempt may have written the whole file and been killed before the
            // final move. Asking for "bytes=N-" past EOF gets a 416 forever, so finish (or
            // discard) the complete temp file here instead of re-requesting it.
            if (file.SizeBytes > 0 && existingBytes >= file.SizeBytes)
            {
                var tempSha = await ComputeSha256Async(tempPath, ct).ConfigureAwait(false);
                if (string.Equals(tempSha, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Model {Id}/{File} was already fully downloaded; installing.", modelId, file.FileName);
                    File.Move(tempPath, finalPath, overwrite: true);
                    progress?.Report(new DownloadProgress(baseOffset + existingBytes, grandTotal));
                    return Result.Ok(finalPath);
                }
                File.Delete(tempPath);
                existingBytes = 0;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
            // §18: "Header Accept-Encoding: identity (no compression — whisper models are
            // pre-compressed binaries)." ONNX weights are just as incompressible.
            request.Headers.AcceptEncoding.Clear();
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (existingBytes > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existingBytes, null);
            }

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingBytes > 0)
            {
                // Partial file doesn't line up with the server's copy: drop it and start over.
                _logger.LogInformation("Range not satisfiable for {Id}/{File}; restarting from byte 0.", modelId, file.FileName);
                File.Delete(tempPath);
                return await DownloadFileAsync(modelId, file, finalPath, baseOffset, grandTotal, progress, ct).ConfigureAwait(false);
            }
            response.EnsureSuccessStatusCode();

            // The server may decline to honour Range (returns 200 instead of 206).
            // In that case discard our partial file and start over.
            bool resuming = response.StatusCode == HttpStatusCode.PartialContent && existingBytes > 0;
            if (!resuming && existingBytes > 0)
            {
                _logger.LogInformation(
                    "Server returned {Status} for Range request on {Id}/{File}; restarting download from byte 0.",
                    (int)response.StatusCode, modelId, file.FileName);
                existingBytes = 0;
            }

            // For 206 the server reports the partial range size in Content-Length; we want
            // the total. Prefer ContentRange.Length, then header ContentLength, then manifest size.
            long fileTotal = response.Content.Headers.ContentRange?.Length
                ?? (response.Content.Headers.ContentLength + existingBytes)
                ?? file.SizeBytes;
            // Aggregate total: manifest sum, corrected if the server disagrees about this file.
            long total = grandTotal - file.SizeBytes + fileTotal;

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
                        progress?.Report(new DownloadProgress(baseOffset + downloaded, total));
                        progressStopwatch.Restart();
                    }
                }
                progress?.Report(new DownloadProgress(baseOffset + downloaded, total));

                sha.TransformFinalBlock([], 0, 0);
                actualSha = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            }

            if (!string.Equals(actualSha, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);
                _logger.LogWarning(
                    "Model {Id}/{File} SHA mismatch (expected {Expected}, got {Actual}); discarded.",
                    modelId, file.FileName, file.Sha256, actualSha);
                return Result.Fail<string>(
                    $"SHA-256 mismatch downloading {modelId}: expected {file.Sha256}, got {actualSha}");
            }

            // Atomic install. File.Move(overwrite: true) maps to MoveFileEx with
            // MOVEFILE_REPLACE_EXISTING — atomic on NTFS, equivalent to File.Replace
            // minus the backup-file feature we don't need.
            File.Move(tempPath, finalPath, overwrite: true);
            return Result.Ok(finalPath);
        }
        catch (HttpRequestException ex)
        {
            return Result.Fail<string>($"HTTP error downloading {modelId}: {ex.Message}", ex);
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
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
