namespace KusPus.Whisper;

/// <summary>One entry in the bundled <c>models.json</c> manifest. See TECH_SPEC §18.</summary>
public sealed record ModelDescriptor(
    string Id,
    string DisplayName,
    string FileName,
    long SizeBytes,
    string Sha256,
    string Url,
    bool Bundled);

/// <summary>Root of <c>KusPus.Whisper/Resources/models.json</c>. See TECH_SPEC §18.</summary>
public sealed record ModelManifest(
    int SchemaVersion,
    string HfRepoCommit,
    IReadOnlyList<ModelDescriptor> Models);

/// <summary>Result of <see cref="IModelManager.Resolve"/> — the model and its on-disk path.</summary>
public sealed record ResolvedModel(ModelDescriptor Descriptor, string Path);

/// <summary>Per-tick progress reported during <see cref="IModelManager.DownloadAsync"/>.</summary>
public sealed record DownloadProgress(long BytesDownloaded, long TotalBytes);
