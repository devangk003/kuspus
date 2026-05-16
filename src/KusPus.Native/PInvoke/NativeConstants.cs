namespace KusPus.Native.PInvoke;

internal static class NativeConstants
{
    // ── Hooks (user32) ──────────────────────────────────────────────────────
    public const int WH_KEYBOARD_LL = 13;

    public const uint WM_QUIT = 0x0012;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    public const uint LLKHF_INJECTED = 0x10;

    // ── Input (user32) ──────────────────────────────────────────────────────
    public const uint INPUT_KEYBOARD = 1;

    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_SCANCODE = 0x0008;

    public const int ASFW_ANY = -1;

    // ── Process access (kernel32) ───────────────────────────────────────────
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // ── Job Objects (kernel32) ──────────────────────────────────────────────
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    public const int JobObjectExtendedLimitInformation = 9;
}
