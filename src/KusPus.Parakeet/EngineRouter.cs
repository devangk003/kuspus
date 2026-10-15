using KusPus.Core;
using KusPus.Whisper;

namespace KusPus.Parakeet;

/// <summary>
/// The <see cref="ITranscriber"/> the rest of the app talks to: picks the engine from
/// <see cref="ModelDescriptor.Engine"/> so the coordinator, onboarding and the
/// "test transcription" buttons never need to know which runtime a model uses.
/// </summary>
public sealed class EngineRouter : ITranscriber
{
    private readonly ITranscriber _whisper;
    private readonly ITranscriber _parakeet;

    public EngineRouter(ITranscriber whisper, ITranscriber parakeet)
    {
        _whisper = whisper;
        _parakeet = parakeet;
    }

    public Task<Result<string>> TranscribeAsync(string wavPath, ResolvedModel model, CancellationToken ct = default) =>
        model.Descriptor.Engine switch
        {
            ModelEngine.Whisper => _whisper.TranscribeAsync(wavPath, model, ct),
            ModelEngine.Parakeet => _parakeet.TranscribeAsync(wavPath, model, ct),
            _ => Task.FromResult(Result.Fail<string>($"Unknown transcription engine '{model.Descriptor.Engine}'.")),
        };
}
