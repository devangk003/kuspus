using KusPus.Core;

namespace KusPus.Whisper;

/// <summary>
/// Reads the bundled manifest, resolves the active model to a file path, downloads
/// models from HuggingFace (SHA-256 verified), and reports installation state.
/// See TECH_SPEC §18.
/// </summary>
public interface IModelManager
{
    /// <summary>The manifest the manager was constructed with (embedded resource or override).</summary>
    ModelManifest Manifest { get; }

    /// <summary>
    /// Resolve a model id (or <c>"custom"</c>) plus an optional custom path to a
    /// descriptor and an absolute filesystem path. Returns <see cref="Result{T}"/>.Fail
    /// when the id is unknown or the file is missing on disk.
    /// </summary>
    Result<ResolvedModel> Resolve(string activeModelId, string? customModelPath);

    /// <summary>True when the model file exists on disk and its SHA-256 matches the manifest.</summary>
    Task<bool> IsInstalledAsync(ModelDescriptor model, CancellationToken ct = default);

    /// <summary>
    /// Download the model from <see cref="ModelDescriptor.Url"/> into the manager's models
    /// directory, verifying SHA-256 in-stream. Atomic install via temp file + move.
    /// </summary>
    Task<Result<string>> DownloadAsync(
        ModelDescriptor model,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);
}
