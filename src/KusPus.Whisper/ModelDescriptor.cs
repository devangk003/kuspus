namespace KusPus.Whisper;

/// <summary>Which inference engine a manifest model runs on.</summary>
public enum ModelEngine
{
    /// <summary>whisper.cpp subprocess (<c>whisper.exe</c> / <c>whisper-cli</c>); one <c>ggml-*.bin</c> file.</summary>
    Whisper,

    /// <summary>NVIDIA Parakeet-TDT via sherpa-onnx, in-process; encoder/decoder/joiner ONNX + tokens.</summary>
    Parakeet,
}

/// <summary>One downloadable file of a model. Whisper models have exactly one; Parakeet has four.</summary>
public sealed record ModelFile(
    string FileName,
    long SizeBytes,
    string Sha256,
    string Url);

/// <summary>
/// Human-facing facts about a model, shown on the Models page so people can choose with
/// confidence. Everything here is sourced from the maker's model card (see
/// <see cref="ModelCardUrl"/>); <see cref="LeaderboardWer"/> is the Hugging Face Open ASR
/// leaderboard average and is only filled in where the card states it.
/// </summary>
/// <param name="Maker">Organisation that trained the model ("OpenAI", "NVIDIA").</param>
/// <param name="Released">Release month, for display ("May 2025").</param>
/// <param name="License">Licence name ("MIT", "CC-BY-4.0").</param>
/// <param name="LicenseUrl">Where the licence text lives.</param>
/// <param name="ModelCardUrl">The maker's model card.</param>
/// <param name="Description">One or two plain-English sentences: what it's good for.</param>
/// <param name="Languages">Language coverage, for display ("English", "25 European languages").</param>
/// <param name="SpeedTier">1 (slowest) … 5 (fastest), relative to the other models here.</param>
/// <param name="AccuracyTier">1 (basic) … 5 (best), relative to the other models here.</param>
/// <param name="Parameters">Parameter count, for display ("600M", "1.55B").</param>
/// <param name="Quantization">Weight format of the files shipped ("int8 ONNX", "f16 ggml").</param>
/// <param name="Recommended">The one model the app suggests to new users.</param>
/// <param name="LeaderboardWer">Open ASR leaderboard average WER (%), when published on the card.</param>
public sealed record ModelInfo(
    string Maker,
    string Released,
    string License,
    string LicenseUrl,
    string ModelCardUrl,
    string Description,
    string Languages,
    int SpeedTier,
    int AccuracyTier,
    string Parameters,
    string Quantization,
    bool Recommended = false,
    double? LeaderboardWer = null);

/// <summary>
/// One entry in the bundled <c>models.json</c> manifest (schema 2). See TECH_SPEC §18.
/// <see cref="Files"/> is the complete on-disk set; <see cref="SizeBytes"/> is their sum.
/// <see cref="Info"/> is optional metadata for the Models page.
/// </summary>
public sealed record ModelDescriptor(
    string Id,
    string DisplayName,
    ModelEngine Engine,
    IReadOnlyList<ModelFile> Files,
    bool Bundled,
    ModelInfo? Info = null)
{
    /// <summary>Total download/install size across all files.</summary>
    public long SizeBytes => Files.Sum(f => f.SizeBytes);

    /// <summary>
    /// The file an engine is pointed at first: the <c>.bin</c> for whisper, the encoder
    /// for Parakeet. Kept as a single path so <see cref="ResolvedModel.Path"/> stays meaningful.
    /// </summary>
    public ModelFile PrimaryFile => Files[0];

    /// <summary>
    /// Sub-directory (relative to a models dir) the files live in. Whisper models stay flat
    /// (<c>models/ggml-tiny.en.bin</c>, as shipped since v1.0); multi-file engines get their
    /// own folder (<c>models/parakeet-tdt-0.6b-v2/encoder.int8.onnx</c>) so file names
    /// shared between models (every Parakeet build has an <c>encoder.int8.onnx</c>) can't collide.
    /// </summary>
    public string Subdirectory => Engine == ModelEngine.Whisper ? string.Empty : Id;

    /// <summary>Convenience for the common single-file (whisper) case.</summary>
    public static ModelDescriptor SingleFile(
        string id, string displayName, string fileName, long sizeBytes, string sha256, string url, bool bundled) =>
        new(id, displayName, ModelEngine.Whisper, [new ModelFile(fileName, sizeBytes, sha256, url)], bundled);
}

/// <summary>Root of <c>KusPus.Whisper/Resources/models.json</c>. See TECH_SPEC §18.</summary>
public sealed record ModelManifest(
    int SchemaVersion,
    string HfRepoCommit,
    IReadOnlyList<ModelDescriptor> Models);

/// <summary>
/// Result of <see cref="IModelManager.Resolve"/> — the model, the absolute path of its
/// primary file and the directory holding all of its files.
/// </summary>
public sealed record ResolvedModel(ModelDescriptor Descriptor, string Path, string Directory)
{
    /// <summary>Single-file convenience (whisper, tests): directory = the file's directory.</summary>
    public ResolvedModel(ModelDescriptor descriptor, string path)
        : this(descriptor, path, System.IO.Path.GetDirectoryName(path) ?? string.Empty)
    {
    }

    /// <summary>Absolute path of one of the model's files by name.</summary>
    public string PathFor(string fileName) => System.IO.Path.Combine(Directory, fileName);
}

/// <summary>Per-tick progress reported during <see cref="IModelManager.DownloadAsync"/> (aggregate over all files).</summary>
public sealed record DownloadProgress(long BytesDownloaded, long TotalBytes);
