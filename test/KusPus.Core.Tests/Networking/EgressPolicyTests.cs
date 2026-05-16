using FluentAssertions;
using KusPus.Core.Networking;
using Xunit;

namespace KusPus.Core.Tests.Networking;

public class EgressPolicyTests
{
    [Fact]
    public void offline_mode_blocks_everything_including_allowlisted_hosts()
    {
        var hf = new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/abc/ggml-tiny.en.bin");
        var sentry = new Uri("https://o123.ingest.sentry.io/api/4/envelope/");

        EgressPolicy.IsAllowed(offlineMode: true, crashReportsOptedIn: true, hf).Should().BeFalse();
        EgressPolicy.IsAllowed(offlineMode: true, crashReportsOptedIn: true, sentry).Should().BeFalse();
    }

    [Fact]
    public void huggingface_host_is_allowed_regardless_of_crash_opt_in()
    {
        var uri = new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/abc/ggml-tiny.en.bin");

        EgressPolicy.IsAllowed(offlineMode: false, crashReportsOptedIn: false, uri).Should().BeTrue();
        EgressPolicy.IsAllowed(offlineMode: false, crashReportsOptedIn: true, uri).Should().BeTrue();
    }

    [Fact]
    public void sentry_host_requires_crash_opt_in()
    {
        var uri = new Uri("https://o123.ingest.sentry.io/api/4/envelope/");

        EgressPolicy.IsAllowed(offlineMode: false, crashReportsOptedIn: false, uri).Should().BeFalse();
        EgressPolicy.IsAllowed(offlineMode: false, crashReportsOptedIn: true, uri).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://huggingface.co/x")]
    [InlineData("https://cdn-lfs.huggingface.co/x")]
    [InlineData("https://api.huggingface.co/x")]
    public void huggingface_subdomains_match(string url)
    {
        EgressPolicy.IsAllowed(offlineMode: false, crashReportsOptedIn: false, new Uri(url))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("https://ingest.sentry.io/api/")]
    [InlineData("https://o42.ingest.sentry.io/api/")]
    [InlineData("https://o12345.ingest.sentry.io/api/9/envelope/")]
    [InlineData("https://o4511400964849664.ingest.de.sentry.io/4511400971010128")]  // EU region
    [InlineData("https://o123.ingest.us.sentry.io/api/")]                            // US explicit
    public void sentry_ingest_hosts_match_when_opted_in_including_regional(string url)
    {
        EgressPolicy.IsAllowed(offlineMode: false, crashReportsOptedIn: true, new Uri(url))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("https://evil.com/")]
    [InlineData("https://huggingface.co.attacker.com/")]
    [InlineData("https://notreally-huggingface.co/")]
    [InlineData("https://sentry.io/")]                       // bare apex — no ingest label
    [InlineData("https://www.sentry.io/")]                   // no ingest label
    [InlineData("https://notingest.sentry.io/")]             // label is not exactly "ingest"
    [InlineData("https://ingest.sentry.io.evil.com/")]
    [InlineData("https://sentry.io.ingest.example.com/")]
    public void unrelated_or_lookalike_hosts_are_blocked(string url)
    {
        EgressPolicy.IsAllowed(offlineMode: false, crashReportsOptedIn: true, new Uri(url))
            .Should().BeFalse();
    }

    [Fact]
    public void non_https_schemes_blocked()
    {
        var http = new Uri("http://huggingface.co/x");
        var ftp = new Uri("ftp://huggingface.co/x");

        EgressPolicy.IsAllowed(offlineMode: false, crashReportsOptedIn: true, http).Should().BeFalse();
        EgressPolicy.IsAllowed(offlineMode: false, crashReportsOptedIn: true, ftp).Should().BeFalse();
    }

    [Fact]
    public void host_match_is_case_insensitive()
    {
        var upper = new Uri("https://HuggingFace.CO/x");
        EgressPolicy.IsAllowed(offlineMode: false, crashReportsOptedIn: false, upper).Should().BeTrue();
    }

    [Fact]
    public void null_uri_throws()
    {
        var act = () => EgressPolicy.IsAllowed(offlineMode: false, crashReportsOptedIn: false, uri: null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
