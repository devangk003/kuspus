using KusPus.Core.Platform;

namespace KusPus.Core.Hotkeys;

/// <summary>
/// Platform-neutral chord state machine shared by the Windows
/// <c>WH_KEYBOARD_LL</c> engine and the macOS <c>CGEventTap</c> engine. Each raw
/// key transition goes through <see cref="Step"/>, which updates the held-key set
/// and returns the <see cref="HotkeyEvent"/>s to publish (zero, one, or — never
/// today — several). Not thread-safe: the owning engine serialises calls under its
/// own lock and emits the returned events <em>after</em> releasing it.
/// </summary>
public sealed class ChordTracker
{
    private readonly HashSet<VirtualKey> _heldModifiers = new();
    private HotkeyChord _chord;
    private bool _heldChordKey;
    private bool _chordEngaged;

    public ChordTracker(HotkeyChord? chord = null)
    {
        _chord = chord ?? HotkeyChord.Default;
    }

    public HotkeyChord Chord => _chord;

    /// <summary>True between a <see cref="ChordEngaged"/> and the following <see cref="ChordReleased"/>.</summary>
    public bool IsEngaged => _chordEngaged;

    /// <summary>
    /// Replaces the chord. Idempotent: if the chord didn't actually change, held
    /// state is preserved so a runtime settings save doesn't wipe mid-chord state
    /// and leave hold-mode stranded in 'engaged' forever.
    /// </summary>
    /// <returns><c>true</c> when the chord changed and state was reset.</returns>
    public bool SetChord(HotkeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        if (ChordsEqual(_chord, chord))
        {
            return false;
        }
        _chord = chord;
        Reset();
        return true;
    }

    /// <summary>Forgets every held key (e.g. after the OS hook was lost and re-installed).</summary>
    public void Reset()
    {
        _heldModifiers.Clear();
        _heldChordKey = false;
        _chordEngaged = false;
    }

    /// <summary>
    /// Feed one key transition. Returns the events the engine should publish, in order.
    /// </summary>
    public IReadOnlyList<HotkeyEvent> Step(VirtualKey key, bool isKeyDown)
    {
        var chord = _chord;
        bool isChordModifier = chord.Modifiers.Contains(key);
        bool isChordKey = chord.Key.HasValue && chord.Key.Value == key;
        bool wasEngaged = _chordEngaged;

        if (isChordModifier)
        {
            if (isKeyDown) { _heldModifiers.Add(key); } else { _heldModifiers.Remove(key); }
        }
        if (isChordKey)
        {
            _heldChordKey = isKeyDown;
        }

        bool allModsHeld =
            chord.Modifiers.Count > 0 &&
            chord.Modifiers.All(_heldModifiers.Contains);
        bool engaged = allModsHeld && (!chord.Key.HasValue || _heldChordKey);

        if (engaged && !wasEngaged)
        {
            _chordEngaged = true;
            return [new ChordEngaged()];
        }
        if (!engaged && wasEngaged)
        {
            _chordEngaged = false;
            return [new ChordReleased()];
        }
        if (engaged && isKeyDown && !isChordModifier && !isChordKey)
        {
            return [new OtherKeyPressed()];
        }
        return [];
    }

    private static bool ChordsEqual(HotkeyChord a, HotkeyChord b) =>
        a.Modifiers.Count == b.Modifiers.Count &&
        a.Modifiers.All(b.Modifiers.Contains) &&
        a.Key == b.Key;
}
