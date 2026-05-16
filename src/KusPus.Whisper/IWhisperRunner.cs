using KusPus.Core;

namespace KusPus.Whisper;

/// <summary>
/// Spawns <c>whisper.exe</c>, feeds it a wav and a model, returns the transcript.
/// See TECH_SPEC §15.
///
/// Deviation from spec: §15 shows <c>TranscribeAsync(string wavPath, ModelDescriptor model, ...)</c>
/// but <see cref="ModelDescriptor"/> has no on-disk path field. Real impl takes
/// <see cref="ResolvedModel"/> (descriptor + path) so the runner has everything it
/// needs without re-resolving.
/// </summary>
public interface IWhisperRunner
{
    Task<Result<string>> TranscribeAsync(string wavPath, ResolvedModel model, CancellationToken ct = default);
}
