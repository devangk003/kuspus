using KusPus.Core.Hotkeys;

namespace KusPus.Core.Settings;

/// <summary>
/// In-memory mirror of <c>%APPDATA%\KusPus\settings.json</c>. Records are immutable;
/// updates create a new instance via <c>with</c>. The single source of truth for
/// defaults is <c>KusPus.Core.Defaults.DefaultSettings</c> per TECH_SPEC §9.4 — the
/// inline <c>new()</c> property initialisers below exist only so a fresh
/// <c>AppSettings()</c> from any caller yields the same shape.
/// </summary>
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public HotkeySettings Hotkey { get; init; } = new();
    public AudioSettings Audio { get; init; } = new();
    public ModelSettings Models { get; init; } = new();
    public UiSettings Ui { get; init; } = new();
    public HistorySettings History { get; init; } = new();
    public PrivacySettings Privacy { get; init; } = new();
    public bool Autostart { get; init; }
    public OnboardingSettings Onboarding { get; init; } = new();
}

public sealed record HotkeySettings
{
    public IReadOnlyList<VirtualKey> Modifiers { get; init; } =
        [VirtualKey.LeftCtrl, VirtualKey.LeftWin];

    public VirtualKey? KeyCode { get; init; }

    public int HoldThresholdMs { get; init; } = 250;
}

public sealed record AudioSettings
{
    public string? InputDeviceId { get; init; }
    public int CaptureSampleRate { get; init; } = 16000;
}

public sealed record ModelSettings
{
    public string ActiveModelId { get; init; } = "ggml-tiny.en";
    public string? CustomModelPath { get; init; }
}

public sealed record UiSettings
{
    /// <summary>One of <c>auto</c>, <c>light</c>, <c>dark</c>.</summary>
    public string Theme { get; init; } = "auto";

    public string PillPosition { get; init; } = "bottom-center";
}

public sealed record HistorySettings
{
    public bool Enabled { get; init; } = true;
}

public sealed record PrivacySettings
{
    public bool OfflineMode { get; init; }
    public bool CrashReportsOptIn { get; init; }
}

public sealed record OnboardingSettings
{
    public bool Completed { get; init; }
    public int Version { get; init; } = 1;
}
