#pragma warning disable CA1848
#pragma warning disable CA1873

using System.Runtime.InteropServices;
using CoreFoundation;

namespace KusPus.Mac.Platform;

/// <summary>
/// Thin CoreAudio HAL wrapper for the bits AVFoundation doesn't expose: enumerate
/// input devices (stable UID + friendly name) and resolve the default input. The
/// settings store keeps the device <b>UID</b> (survives reboots and re-plugs); the
/// numeric <c>AudioDeviceID</c> is only valid for the current session.
/// </summary>
public static class CoreAudioDevices
{
    public sealed record InputDevice(uint DeviceId, string Uid, string Name);

    private const string CoreAudioLib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const uint SystemObject = 1;                       // kAudioObjectSystemObject

    // FourCC selectors / scopes.
    private const uint PropDevices = 0x64657623;               // 'dev#'
    private const uint PropDefaultInputDevice = 0x64496E20;    // 'dIn '
    private const uint PropStreamConfiguration = 0x736C6179;   // 'slay'
    private const uint PropDeviceUid = 0x75696420;             // 'uid '
    private const uint PropName = 0x6C6E616D;                  // 'lnam'
    private const uint PropDeviceIsAlive = 0x6C69766E;         // 'livn'
    private const uint ScopeGlobal = 0x676C6F62;               // 'glob'
    private const uint ScopeInput = 0x696E7074;                // 'inpt'
    private const uint ElementMain = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    [DllImport(CoreAudioLib)]
    private static extern int AudioObjectGetPropertyDataSize(uint objectId, in PropertyAddress address, uint qualifierSize, IntPtr qualifier, out uint dataSize);

    [DllImport(CoreAudioLib)]
    private static extern int AudioObjectGetPropertyData(uint objectId, in PropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint dataSize, IntPtr data);

    [DllImport(CoreAudioLib)]
    private static extern unsafe int AudioObjectAddPropertyListener(uint objectId, in PropertyAddress address, delegate* unmanaged<uint, uint, PropertyAddress*, IntPtr, int> listener, IntPtr clientData);

    [DllImport(CoreAudioLib)]
    private static extern unsafe int AudioObjectRemovePropertyListener(uint objectId, in PropertyAddress address, delegate* unmanaged<uint, uint, PropertyAddress*, IntPtr, int> listener, IntPtr clientData);

    /// <summary>
    /// Calls <paramref name="onDisconnected"/> (on a CoreAudio thread) when the device stops
    /// being alive — unplugged, Bluetooth link dropped. Dispose to stop watching.
    /// </summary>
    public static unsafe IDisposable WatchDeviceAlive(uint deviceId, Action onDisconnected)
    {
        var watch = new AliveWatch(deviceId, onDisconnected);
        var addr = new PropertyAddress { Selector = PropDeviceIsAlive, Scope = ScopeGlobal, Element = ElementMain };
        if (AudioObjectAddPropertyListener(deviceId, in addr, &OnDeviceAliveChanged, watch.Cookie) == 0)
        {
            watch.MarkRegistered();
        }
        return watch;
    }

    private static bool IsAlive(uint deviceId)
    {
        var addr = new PropertyAddress { Selector = PropDeviceIsAlive, Scope = ScopeGlobal, Element = ElementMain };
        uint size = sizeof(uint);
        unsafe
        {
            uint alive;
            // A device that's gone may fail the query outright — treat that as not alive.
            return AudioObjectGetPropertyData(deviceId, in addr, 0, IntPtr.Zero, ref size, (IntPtr)(&alive)) == 0 && alive != 0;
        }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static unsafe int OnDeviceAliveChanged(uint objectId, uint count, PropertyAddress* addresses, IntPtr clientData)
    {
        try
        {
            if (GCHandle.FromIntPtr(clientData).Target is AliveWatch watch && !IsAlive(objectId))
            {
                watch.Fire();
            }
        }
        catch (Exception)
        {
            // Never let an exception unwind into CoreAudio.
        }
        return 0;
    }

    private sealed unsafe class AliveWatch : IDisposable
    {
        private readonly uint _deviceId;
        private Action? _onDisconnected;
        // Field, not property: GCHandle is a struct, and freeing a copy would leave this
        // one looking allocated (double free on Dispose).
        private GCHandle _handle;
        private bool _registered;

        public AliveWatch(uint deviceId, Action onDisconnected)
        {
            _deviceId = deviceId;
            _onDisconnected = onDisconnected;
            _handle = GCHandle.Alloc(this);
        }

        public IntPtr Cookie => GCHandle.ToIntPtr(_handle);

        public void MarkRegistered() => _registered = true;

        public void Fire() => Interlocked.Exchange(ref _onDisconnected, null)?.Invoke();

        public void Dispose()
        {
            _onDisconnected = null;
            if (_registered)
            {
                _registered = false;
                var addr = new PropertyAddress { Selector = PropDeviceIsAlive, Scope = ScopeGlobal, Element = ElementMain };
                _ = AudioObjectRemovePropertyListener(_deviceId, in addr, &OnDeviceAliveChanged, Cookie);
            }
            if (_handle.IsAllocated)
            {
                _handle.Free();
            }
        }
    }

    public static IReadOnlyList<InputDevice> EnumerateInputDevices()
    {
        var result = new List<InputDevice>();
        foreach (var id in AllDeviceIds())
        {
            if (InputChannelCount(id) == 0)
            {
                continue;
            }
            var uid = GetString(id, PropDeviceUid);
            var name = GetString(id, PropName);
            if (uid is not null)
            {
                result.Add(new InputDevice(id, uid, name ?? uid));
            }
        }
        return result;
    }

    public static uint? DefaultInputDeviceId()
    {
        var addr = new PropertyAddress { Selector = PropDefaultInputDevice, Scope = ScopeGlobal, Element = ElementMain };
        uint size = sizeof(uint);
        unsafe
        {
            uint id;
            var status = AudioObjectGetPropertyData(SystemObject, in addr, 0, IntPtr.Zero, ref size, (IntPtr)(&id));
            return status == 0 && id != 0 ? id : null;
        }
    }

    public static InputDevice? FindByUid(string uid) =>
        EnumerateInputDevices().FirstOrDefault(d => string.Equals(d.Uid, uid, StringComparison.Ordinal));

    /// <summary>
    /// Installs an input tap using the macOS 27 error-reporting overload when available
    /// and the legacy overload on macOS 15–26 (the new selector doesn't exist there).
    /// Pass <c>null</c> for <paramref name="format"/> to tap in the node's live format —
    /// the safe choice for an input node, whose cached output format can go stale (a
    /// mismatched format fails with -10868, kAudioUnitErr_FormatNotSupported). Read the
    /// real format from each tap buffer instead. Returns an error message on failure, or <c>null</c>.
    /// </summary>
    public static string? InstallTap(AVFoundation.AVAudioNode node, uint bufferSize, AVFoundation.AVAudioFormat? format, AVFoundation.AVAudioNodeTapBlock block)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (OperatingSystem.IsMacOSVersionAtLeast(27))
        {
            return node.InstallTapOnBus(0, bufferSize, format!, out var error, block)
                ? null
                : error?.LocalizedDescription ?? "InstallTapOnBus failed.";
        }
#pragma warning disable CA1422 // legacy overload is the only one on macOS < 27
        node.InstallTapOnBus(0, bufferSize, format!, block);
#pragma warning restore CA1422
        return null;
    }

    /// <summary>
    /// Taps an input node in its live format (null format). If the binding refuses a null
    /// format, falls back to the hardware-side format. Returns an error message or null.
    /// </summary>
    public static string? InstallInputTap(AVFoundation.AVAudioInputNode input, uint bufferSize, AVFoundation.AVAudioNodeTapBlock block)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            return InstallTap(input, bufferSize, null, block);
        }
        catch (ArgumentNullException)
        {
            return InstallTap(input, bufferSize, HardwareInputFormat(input), block);
        }
    }

    /// <summary>
    /// The input node's hardware-side format. <c>GetBusOutputFormat(0)</c> can lag behind
    /// the device (e.g. mid Bluetooth profile switch); the input-scope format follows the
    /// hardware.
    /// </summary>
    public static AVFoundation.AVAudioFormat HardwareInputFormat(AVFoundation.AVAudioInputNode input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var hw = input.GetBusInputFormat(0);
        return hw.SampleRate > 0 && hw.ChannelCount > 0 ? hw : input.GetBusOutputFormat(0);
    }

    /// <summary>Friendly name of the current system default input, or null.</summary>
    public static string? DefaultInputDeviceName()
    {
        var id = DefaultInputDeviceId();
        return id is null ? null : GetString(id.Value, PropName);
    }

    // ── private ──────────────────────────────────────────────────────────────

    private static uint[] AllDeviceIds()
    {
        var addr = new PropertyAddress { Selector = PropDevices, Scope = ScopeGlobal, Element = ElementMain };
        if (AudioObjectGetPropertyDataSize(SystemObject, in addr, 0, IntPtr.Zero, out var size) != 0 || size == 0)
        {
            return [];
        }
        var ids = new uint[size / sizeof(uint)];
        unsafe
        {
            fixed (uint* p = ids)
            {
                if (AudioObjectGetPropertyData(SystemObject, in addr, 0, IntPtr.Zero, ref size, (IntPtr)p) != 0)
                {
                    return [];
                }
            }
        }
        return ids;
    }

    private static int InputChannelCount(uint deviceId)
    {
        var addr = new PropertyAddress { Selector = PropStreamConfiguration, Scope = ScopeInput, Element = ElementMain };
        if (AudioObjectGetPropertyDataSize(deviceId, in addr, 0, IntPtr.Zero, out var size) != 0 || size < 4)
        {
            return 0;
        }
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(deviceId, in addr, 0, IntPtr.Zero, ref size, buf) != 0)
            {
                return 0;
            }
            // AudioBufferList { UInt32 mNumberBuffers; (4 bytes pad) AudioBuffer mBuffers[]; }
            // AudioBuffer holds a pointer, so mBuffers is 8-byte aligned at offset 8.
            // AudioBuffer    { UInt32 mNumberChannels; UInt32 mDataByteSize; void* mData; }
            int buffers = Marshal.ReadInt32(buf);
            int channels = 0;
            for (int i = 0; i < buffers; i++)
            {
                channels += Marshal.ReadInt32(buf, 8 + (i * 16));
            }
            return channels;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static string? GetString(uint deviceId, uint selector)
    {
        var addr = new PropertyAddress { Selector = selector, Scope = ScopeGlobal, Element = ElementMain };
        uint size = (uint)IntPtr.Size;
        IntPtr cfString;
        unsafe
        {
            IntPtr tmp;
            if (AudioObjectGetPropertyData(deviceId, in addr, 0, IntPtr.Zero, ref size, (IntPtr)(&tmp)) != 0 || tmp == IntPtr.Zero)
            {
                return null;
            }
            cfString = tmp;
        }
        try
        {
            return CFString.FromHandle(cfString);
        }
        finally
        {
            CFRelease(cfString);
        }
    }
}
