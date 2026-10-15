using FluentAssertions;
using Xunit;

namespace KusPus.Whisper.Tests;

public class Sha256SumsTests
{
    private const string Hash = "468076088f44a9c0e3cdd7790a35a89e2477ea2911f25e19c8690f157f99c34f";

    [Theory]
    [InlineData(Hash + "  whisper-cli")]
    [InlineData(Hash + " *whisper-cli")]
    [InlineData(Hash + "  ./whisper-cli")]
    [InlineData(Hash + "  models/../whisper-cli")]
    [InlineData("  " + Hash + "  whisper-cli  ")]
    public void ParseLine_matches_name_variants(string line)
    {
        Sha256Sums.ParseLine(line, "whisper-cli").Should().Be(Hash);
    }

    [Theory]
    [InlineData(Hash + "  whisper-cli.exe")]
    [InlineData(Hash + "  models/ggml-tiny.en.bin")]
    [InlineData("not-a-hash  whisper-cli")]
    [InlineData("")]
    [InlineData(Hash)]
    public void ParseLine_rejects_other_lines(string line)
    {
        Sha256Sums.ParseLine(line, "whisper-cli").Should().BeNull();
    }

    [Fact]
    public void ParseLine_lowercases_hash()
    {
        Sha256Sums.ParseLine(Hash.ToUpperInvariant() + "  whisper-cli", "whisper-cli").Should().Be(Hash);
    }

    [Fact]
    public void FindHash_reads_the_matching_line_and_handles_missing_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kuspus-sums-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllLines(path,
            [
                "0000000000000000000000000000000000000000000000000000000000000000  libwhisper.1.dylib",
                Hash + "  whisper-cli",
                "1111111111111111111111111111111111111111111111111111111111111111  models/ggml-tiny.en.bin",
            ]);
            Sha256Sums.FindHash(path, "whisper-cli").Should().Be(Hash);
            Sha256Sums.FindHash(path, "ggml-tiny.en.bin").Should().Be("1111111111111111111111111111111111111111111111111111111111111111");
            Sha256Sums.FindHash(path, "nope").Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
        Sha256Sums.FindHash(path, "whisper-cli").Should().BeNull();
    }
}
