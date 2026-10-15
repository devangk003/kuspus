using KusPus.Core;

namespace KusPus.Whisper;

/// <summary>
/// Reads the bundled manifest, resolves the active model to its on-disk files, downloads
/// models from HuggingFace (SHA-256 verified, file by file), and reports installation state.
/// See TECH_SPEC §18.
/// </summary>
public interface IModelManager
{
    /// <summary>The manifest the manager was constructed with (embedded resource or override).</summary>
    ModelManifest Manifest { get; }

    /// <summary>The writable directory downloads land in (shown to the user as "where models live").</summary>
    string ModelsDirectory { get; }

    /// <summary>
    /// Bytes the model's files occupy in <see cref="ModelsDirectory"/> — i.e. what
    /// <see cref="Remove"/> would reclaim. Bundled copies inside the app are not counted.
    /// </summary>
    long DownloadedSizeBytes(ModelDescriptor model);

    /// <summary>
    /// Delete the model's downloaded files from <see cref="ModelsDirectory"/>. Never touches
    /// the bundled copy, so a bundled model stays usable. Returns the bytes freed.
    /// </summary>
    Result<long> Remove(ModelDescriptor model);

    /// <summary>
    /// Resolve a model id (or <c>"custom"</c>) plus an optional custom path to a
    /// descriptor and absolute filesystem paths. Returns <see cref="Result{T}"/>.Fail
    /// when the id is unknown or any of the model's files is missing on disk.
    /// </summary>
    Result<ResolvedModel> Resolve(string activeModelId, string? customModelPath);

    /// <summary>
    /// Cheap presence check (no hashing): every file of the model exists in the writable
    /// or bundled models directory. What the model pickers render from.
    /// </summary>
    bool IsInstalled(ModelDescriptor model);

    /// <summary>True when every file exists on disk and its SHA-256 matches the manifest.</summary>
    Task<bool> IsInstalledAsync(ModelDescriptor model, CancellationToken ct = default);

    /// <summary>
    /// Download every file of the model into the manager's models directory, verifying
    /// SHA-256 in-stream. Files already present with the right hash are skipped; progress
    /// is aggregate over the whole model. Atomic per-file install via temp file + move.
    /// Returns the primary file's path.
    /// </summary>
    Task<Result<string>> DownloadAsync(
        ModelDescriptor model,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);
}
