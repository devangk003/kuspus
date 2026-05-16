using FluentAssertions;
using KusPus.Core.Defaults;
using KusPus.Core.Hotkeys;
using Xunit;

namespace KusPus.Core.Tests;

public class DefaultSettingsTests
{
    [Fact]
    public void ForFirstRun_matches_TECH_SPEC_section_9_1_JSON_shape()
    {
        var s = DefaultSettings.ForFirstRun();

        s.SchemaVersion.Should().Be(1);

        s.Hotkey.Modifiers.Should().Equal(VirtualKey.LeftCtrl, VirtualKey.LeftWin);
        s.Hotkey.KeyCode.Should().BeNull();
        s.Hotkey.HoldThresholdMs.Should().Be(250);

        s.Audio.InputDeviceId.Should().BeNull();
        s.Audio.CaptureSampleRate.Should().Be(16000);

        s.Models.ActiveModelId.Should().Be("ggml-tiny.en");
        s.Models.CustomModelPath.Should().BeNull();

        s.Ui.Theme.Should().Be("auto");
        s.Ui.PillPosition.Should().Be("bottom-center");

        s.History.Enabled.Should().BeTrue();

        s.Privacy.OfflineMode.Should().BeFalse();
        s.Privacy.CrashReportsOptIn.Should().BeFalse();

        s.Autostart.Should().BeFalse();

        s.Onboarding.Completed.Should().BeFalse();
        s.Onboarding.Version.Should().Be(1);
    }
}
