using KusPus.Core;

namespace KusPus.Whisper;

/// <summary>
/// Turns a recorded wav + a resolved model into a transcript. Implemented by the
/// whisper.cpp subprocess runner (<see cref="WhisperRunner"/>), the in-process
/// Parakeet/sherpa-onnx engine, and the router that picks one from
/// <see cref="ModelDescriptor.Engine"/>. See TECH_SPEC §15.
///
/// Takes <see cref="ResolvedModel"/> (descriptor + on-disk location) rather than a bare
/// descriptor so the engine has everything it needs without re-resolving.
/// </summary>
public interface ITranscriber
{
    Task<Result<string>> TranscribeAsync(string wavPath, ResolvedModel model, CancellationToken ct = default);
}
