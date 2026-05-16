// IconBuilder: one-shot tool that converts an SVG file to a multi-resolution .ico
// suitable for Windows app icons (taskbar, tray, File Explorer, Task Manager).
//
// Usage:
//   dotnet run --project tools/IconBuilder -- <input.svg> <output.ico>
//
// Sizes embedded: 16, 24, 32, 48, 64, 128, 256.
// 256-sized frames are PNG-encoded (per ICO spec for large frames); smaller frames
// are 32bpp BGRA bitmaps.

using System.Drawing;
using System.Drawing.Imaging;
using Svg;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: IconBuilder <input.svg> <output.ico>");
    return 2;
}

string svgPath = args[0];
string icoPath = args[1];

if (!File.Exists(svgPath))
{
    Console.Error.WriteLine($"Input not found: {svgPath}");
    return 1;
}

Console.WriteLine($"Loading SVG: {svgPath}");
var doc = SvgDocument.Open(svgPath);

int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
var frames = new List<(int Size, byte[] Data, bool IsPng)>();

foreach (int size in sizes)
{
    Console.WriteLine($"  rendering {size}x{size}…");
    using var bmp = doc.Draw(size, size);

    if (size >= 64)
    {
        // PNG encode for larger sizes — both for size efficiency and because
        // 256x256 BMP frames in ICO format have known compatibility issues.
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        frames.Add((size, ms.ToArray(), true));
    }
    else
    {
        // 32bpp BGRA bitmap data. We write the BITMAPINFOHEADER (no file header)
        // followed by raw BGRA pixel data, bottom-up, plus an AND mask.
        frames.Add((size, EncodeBmpForIco(bmp), false));
    }
}

WriteIco(icoPath, frames);
Console.WriteLine($"Wrote {icoPath} ({new FileInfo(icoPath).Length} bytes, {frames.Count} frames)");
return 0;

static byte[] EncodeBmpForIco(Bitmap bmp)
{
    int size = bmp.Width;
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);

    // BITMAPINFOHEADER (40 bytes). Note: ICO uses double height in this header to
    // accommodate the AND mask, even though we never read it as height.
    w.Write(40);                  // biSize
    w.Write(size);                // biWidth
    w.Write(size * 2);            // biHeight (image + mask)
    w.Write((short)1);            // biPlanes
    w.Write((short)32);           // biBitCount
    w.Write(0);                   // biCompression (BI_RGB)
    w.Write(0);                   // biSizeImage
    w.Write(0);                   // biXPelsPerMeter
    w.Write(0);                   // biYPelsPerMeter
    w.Write(0);                   // biClrUsed
    w.Write(0);                   // biClrImportant

    // Pixel data: BGRA, bottom-up.
    var bits = bmp.LockBits(
        new Rectangle(0, 0, size, size),
        ImageLockMode.ReadOnly,
        PixelFormat.Format32bppArgb);
    try
    {
        int stride = bits.Stride;
        var row = new byte[stride];
        for (int y = size - 1; y >= 0; y--)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                bits.Scan0 + (y * stride), row, 0, stride);
            w.Write(row);
        }
    }
    finally
    {
        bmp.UnlockBits(bits);
    }

    // AND mask — all transparent, packed 1-bit (rows padded to 4-byte boundary).
    int rowBytes = ((size + 31) / 32) * 4;
    var maskRow = new byte[rowBytes];
    for (int y = 0; y < size; y++)
    {
        w.Write(maskRow);
    }

    return ms.ToArray();
}

static void WriteIco(string path, List<(int Size, byte[] Data, bool IsPng)> frames)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);

    // ICONDIR (6 bytes).
    w.Write((short)0);                    // reserved
    w.Write((short)1);                    // type (1 = ICO)
    w.Write((short)frames.Count);         // count

    // ICONDIRENTRY[] (16 bytes each), data offsets need to come after all entries.
    int entriesSize = 6 + (frames.Count * 16);
    int dataOffset = entriesSize;
    foreach (var (size, data, _) in frames)
    {
        byte width = (byte)(size == 256 ? 0 : size);
        byte height = (byte)(size == 256 ? 0 : size);
        w.Write(width);                   // width (0 means 256)
        w.Write(height);                  // height
        w.Write((byte)0);                 // colorCount (0 for 32bpp)
        w.Write((byte)0);                 // reserved
        w.Write((short)1);                // planes
        w.Write((short)32);               // bitCount
        w.Write(data.Length);             // sizeInBytes
        w.Write(dataOffset);              // offset
        dataOffset += data.Length;
    }

    foreach (var (_, data, _) in frames)
    {
        w.Write(data);
    }
}
