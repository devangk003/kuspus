using FluentAssertions;
using Sentry;
using Sentry.Protocol;
using Xunit;

namespace KusPus.Coordination.Tests;

public class CrashReporterScrubTests
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void Scrubs_user_paths_in_tag_and_extra_values_and_debug_images()
    {
        var path = Path.Combine(Home, "Downloads", "KusPus.app", "Contents", "MacOS", "KusPus");
        var evt = new SentryEvent();
        evt.SetTag("bundle", path);
        evt.SetExtra("cwd", path);
        evt.DebugImages = [new DebugImage { CodeFile = path, DebugFile = path }];
        evt.SentryExceptions = [new SentryException
        {
            Value = "boom",
            Module = path,
            Stacktrace = new SentryStackTrace { Frames = [new SentryStackFrame { Module = path, Package = path }] },
        }];

        var result = CrashReporter.BeforeSend(evt, new SentryHint());

        result.Should().NotBeNull();
        result!.Tags["bundle"].Should().NotContain(Home);
        ((string)result.Extra["cwd"]!).Should().NotContain(Home);
        result.DebugImages![0].CodeFile.Should().NotContain(Home);
        result.DebugImages[0].DebugFile.Should().NotContain(Home);
        var ex = result.SentryExceptions!.Single();
        ex.Module.Should().NotContain(Home);
        ex.Stacktrace!.Frames[0].Module.Should().NotContain(Home);
        ex.Stacktrace.Frames[0].Package.Should().NotContain(Home);
    }
}
