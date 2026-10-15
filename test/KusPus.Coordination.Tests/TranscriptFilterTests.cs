using FluentAssertions;
using Xunit;

namespace KusPus.Coordination.Tests;

public class TranscriptFilterTests
{
    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData("  [BLANK_AUDIO]  ", "")]
    [InlineData("[ Silence ]", "")]
    [InlineData("(silence)", "")]
    [InlineData("[MUSIC]", "")]
    [InlineData("[BLANK_AUDIO] hello there [BLANK_AUDIO]", "hello there")]
    [InlineData("hello (applause) world", "hello world")]
    public void Removes_non_speech_markers(string input, string expected)
    {
        TranscriptFilter.Clean(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("the API (version two) is live", "the API (version two) is live")]
    [InlineData("call [Alice] tomorrow", "call [Alice] tomorrow")]
    [InlineData("plain sentence.", "plain sentence.")]
    public void Keeps_spoken_parentheticals(string input, string expected)
    {
        TranscriptFilter.Clean(input).Should().Be(expected);
    }

    [Fact]
    public void Trims_whitespace()
    {
        TranscriptFilter.Clean("  hi  \n").Should().Be("hi");
    }

    [Fact]
    public void Joins_whisper_segment_lines_with_spaces()
    {
        TranscriptFilter.Clean(" first segment.\n second segment.\r\n third.\n")
            .Should().Be("first segment. second segment. third.");
    }
}
