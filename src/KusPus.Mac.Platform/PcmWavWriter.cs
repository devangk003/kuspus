using System.Buffers.Binary;

namespace KusPus.Mac.Platform;

/// <summary>
/// Minimal streaming RIFF/WAVE writer for 16-bit PCM — the only format
/// <c>whisper-cli</c> accepts. Header sizes are patched on <see cref="Dispose"/>.
/// (NAudio's <c>WaveFileWriter</c> does the same on Windows; this avoids pulling
/// NAudio into the Mac build for ~40 lines of code.)
/// </summary>
public sealed class PcmWavWriter : IDisposable
{
    private const int HeaderSize = 44;
    private readonly FileStream _stream;
    private readonly int _sampleRate;
    private readonly short _channels;
    private long _dataBytes;
    private bool _disposed;

    public PcmWavWriter(string path, int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = (short)channels;
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        _stream.Write(new byte[HeaderSize]); // placeholder, patched in Dispose
    }

    public long BytesWritten => _dataBytes;

    public void Write(ReadOnlySpan<byte> pcm16)
    {
        _stream.Write(pcm16);
        _dataBytes += pcm16.Length;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        Span<byte> h = stackalloc byte[HeaderSize];
        short bitsPerSample = 16;
        short blockAlign = (short)(_channels * bitsPerSample / 8);
        int byteRate = _sampleRate * blockAlign;

        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], (int)Math.Min(int.MaxValue, 36 + _dataBytes));
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16);          // PCM fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], 1);           // PCM
        BinaryPrimitives.WriteInt16LittleEndian(h[22..], _channels);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], _sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(h[32..], blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(h[34..], bitsPerSample);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], (int)Math.Min(int.MaxValue, _dataBytes));

        try
        {
            _stream.Flush();
            _stream.Position = 0;
            _stream.Write(h);
            _stream.Flush(flushToDisk: true);
        }
        finally
        {
            // Disk full / volume gone: the header patch fails, but the handle must not leak.
            _stream.Dispose();
        }
    }
}
