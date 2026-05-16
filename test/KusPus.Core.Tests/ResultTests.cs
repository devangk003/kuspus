using FluentAssertions;
using KusPus.Core;
using Xunit;

namespace KusPus.Core.Tests;

public class ResultTests
{
    [Fact]
    public void Ok_carries_value_and_no_error()
    {
        var r = Result.Ok(42);

        r.Success.Should().BeTrue();
        r.Value.Should().Be(42);
        r.Error.Should().BeNull();
        r.Cause.Should().BeNull();
    }

    [Fact]
    public void Fail_carries_error_message_and_optional_cause()
    {
        var ex = new InvalidOperationException("boom");
        var r = Result.Fail<string>("kaboom", ex);

        r.Success.Should().BeFalse();
        r.Value.Should().BeNull();
        r.Error.Should().Be("kaboom");
        r.Cause.Should().Be(ex);
    }

    [Fact]
    public void Fail_without_cause_leaves_cause_null()
    {
        var r = Result.Fail<int>("oops");

        r.Success.Should().BeFalse();
        r.Cause.Should().BeNull();
        r.Error.Should().Be("oops");
    }
}
