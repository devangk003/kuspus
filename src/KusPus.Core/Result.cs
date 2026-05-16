namespace KusPus.Core;

/// <summary>
/// Boundary return type. Internal code throws exceptions; code that crosses an I/O,
/// network, or P/Invoke boundary returns <see cref="Result{T}"/> instead.
/// See TECH_SPEC §10.
///
/// Construct via <see cref="Result.Ok{T}"/> / <see cref="Result.Fail{T}"/>. The
/// non-generic <see cref="Result"/> factory enables type-inferred call sites
/// (<c>Result.Ok(42)</c> instead of <c>Result&lt;int&gt;.Ok(42)</c>) and satisfies
/// CA1000 "do not declare static members on generic types". TECH_SPEC §10 shows the
/// intent of a record type with Ok/Fail factories; the static-placement is an
/// idiomatic .NET implementation detail.
/// </summary>
public readonly record struct Result<T>(bool Success, T? Value, string? Error, Exception? Cause);

public static class Result
{
    public static Result<T> Ok<T>(T value) =>
        new(Success: true, Value: value, Error: null, Cause: null);

    public static Result<T> Fail<T>(string error, Exception? cause = null) =>
        new(Success: false, Value: default, Error: error, Cause: cause);
}
