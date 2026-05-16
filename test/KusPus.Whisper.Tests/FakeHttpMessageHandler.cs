namespace KusPus.Whisper.Tests;

/// <summary>
/// Minimal in-test <see cref="HttpMessageHandler"/>. Construct with a responder lambda
/// that maps <see cref="HttpRequestMessage"/> to <see cref="HttpResponseMessage"/>.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(_responder(request));
    }
}
