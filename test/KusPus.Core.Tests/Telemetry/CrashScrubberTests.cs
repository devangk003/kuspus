using FluentAssertions;
using KusPus.Core.Telemetry;
using Xunit;

namespace KusPus.Core.Tests.Telemetry;

public class CrashScrubberTests
{
    [Theory]
    [InlineData("text")]
    [InlineData("transcript")]
    [InlineData("clipboard")]
    [InlineData("password")]
    [InlineData("target_app")]
    [InlineData("hwnd")]
    public void sensitive_keys_match(string key)
    {
        CrashScrubber.IsSensitiveKey(key).Should().BeTrue();
    }

    [Theory]
    [InlineData("TEXT")]
    [InlineData("Transcript")]
    [InlineData("PASSWORD")]
    public void key_match_is_case_insensitive(string key)
    {
        CrashScrubber.IsSensitiveKey(key).Should().BeTrue();
    }

    [Theory]
    [InlineData("context_target_app_role")]   // substring of "target_app" but a distinct key
    [InlineData("hwnd_count")]                // substring of "hwnd" but distinct
    [InlineData("body")]
    [InlineData("stacktrace")]
    [InlineData("")]
    public void non_sensitive_keys_are_safe(string key)
    {
        CrashScrubber.IsSensitiveKey(key).Should().BeFalse();
    }

    [Fact]
    public void contains_any_sensitive_key_detects_at_least_one()
    {
        CrashScrubber.ContainsAnySensitiveKey(["request_id", "transcript", "level"])
            .Should().BeTrue();
        CrashScrubber.ContainsAnySensitiveKey(["request_id", "level", "scope"])
            .Should().BeFalse();
        CrashScrubber.ContainsAnySensitiveKey([])
            .Should().BeFalse();
    }

    [Fact]
    public void contains_any_sensitive_key_null_throws()
    {
        var act = () => CrashScrubber.ContainsAnySensitiveKey(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void scrub_path_replaces_localappdata_prefix()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var input = Path.Combine(local, "KusPus", "logs", "kuspus-20260517.log");

        var scrubbed = CrashScrubber.ScrubPath(input);

        scrubbed.Should().StartWith("%LOCALAPPDATA%");
        scrubbed.Should().NotContain(Environment.UserName);
    }

    [Fact]
    public void scrub_path_replaces_appdata_prefix()
    {
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var input = Path.Combine(appdata, "KusPus", "settings.json");

        CrashScrubber.ScrubPath(input).Should().StartWith("%APPDATA%");
    }

    [Fact]
    public void scrub_path_replaces_userprofile_prefix()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var input = Path.Combine(profile, "Desktop", "demo.txt");

        var scrubbed = CrashScrubber.ScrubPath(input);

        scrubbed.Should().StartWith("%USERPROFILE%");
        scrubbed.Should().NotContain(Environment.UserName);
    }

    [Fact]
    public void scrub_path_leaves_non_user_paths_alone()
    {
        var input = @"C:\Windows\System32\kernel32.dll";
        CrashScrubber.ScrubPath(input).Should().Be(input);
    }

    [Fact]
    public void scrub_path_handles_null_and_empty()
    {
        CrashScrubber.ScrubPath(null).Should().BeNull();
        CrashScrubber.ScrubPath("").Should().Be("");
    }

    [Fact]
    public void scrub_path_match_is_case_insensitive()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var input = local.ToUpperInvariant() + @"\KusPus\logs\x.log";

        CrashScrubber.ScrubPath(input).Should().StartWith("%LOCALAPPDATA%");
    }

    [Fact]
    public void scrub_path_prefers_localappdata_over_userprofile_when_both_match()
    {
        // %LOCALAPPDATA% is nested under %USERPROFILE% on Windows. The more specific
        // prefix must win so the resulting token still locates the file inside the
        // sub-directory rather than just under the home dir.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var input = Path.Combine(local, "Foo", "bar.txt");

        var scrubbed = CrashScrubber.ScrubPath(input);

        scrubbed.Should().StartWith("%LOCALAPPDATA%");
        scrubbed.Should().NotContain("%USERPROFILE%");
    }

    [Fact]
    public void scrub_path_replaces_temp_prefix()
    {
        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var input = Path.Combine(temp, "kuspus-recording-abc.wav");

        var scrubbed = CrashScrubber.ScrubPath(input);

        scrubbed.Should().StartWith("%TEMP%");
        scrubbed.Should().NotContain(Environment.UserName);
    }

    [Fact]
    public void scrub_string_redacts_username_outside_path_context()
    {
        // Skip if running under an unusable single-char username — match guard.
        if (Environment.UserName.Length <= 1)
        {
            return;
        }
        var input = $"Failed for user {Environment.UserName} during init.";

        var scrubbed = CrashScrubber.ScrubString(input);

        scrubbed.Should().NotContain(Environment.UserName);
        scrubbed.Should().Contain("<user>");
    }

    [Fact]
    public void scrub_string_applies_path_and_username_in_one_pass()
    {
        if (Environment.UserName.Length <= 1)
        {
            return;
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var input = $"Error at {Path.Combine(local, "KusPus")} for {Environment.UserName}.";

        var scrubbed = CrashScrubber.ScrubString(input);

        scrubbed.Should().Contain("%LOCALAPPDATA%");
        scrubbed.Should().NotContain(Environment.UserName);
    }

    [Fact]
    public void scrub_string_handles_null_and_empty()
    {
        CrashScrubber.ScrubString(null).Should().BeNull();
        CrashScrubber.ScrubString("").Should().Be("");
    }

    [Fact]
    public void scrub_string_username_match_is_case_insensitive()
    {
        if (Environment.UserName.Length <= 1)
        {
            return;
        }
        var upper = Environment.UserName.ToUpperInvariant();
        var input = $"Hello {upper}!";

        var scrubbed = CrashScrubber.ScrubString(input);

        scrubbed.Should().Contain("<user>");
    }
}
