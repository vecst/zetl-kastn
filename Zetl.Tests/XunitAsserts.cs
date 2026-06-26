using System;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace ZETL.Tests;

internal static class XunitAsserts
{
    public static void AssertTrue([DoesNotReturnIf(false)] bool condition, string message)
    {
        Assert.True(condition, message);
    }

    public static void AssertFalse([DoesNotReturnIf(true)] bool condition, string message)
    {
        Assert.False(condition, message);
    }

    public static void AssertEqual<T>(T expected, T actual, string message)
    {
        // xUnit's Assert.Equal doesn't natively take a message string in this overload, 
        // so we wrap it in a True assertion if they don't match.
        if (!System.Collections.Generic.EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Assert.Fail($"{message}\nExpected: {expected}\nActual: {actual}");
        }
    }

    public static void AssertNotNull([NotNull] object? value, string message)
    {
        Assert.True(value != null, message);
    }

    public static void AssertNull(object? value, string message)
    {
        Assert.True(value == null, message);
    }


    public static void AssertThrows<TException>(Action action, string message) where TException : Exception
    {
        var ex = Record.Exception(action);
        Assert.True(ex is TException, message);
    }
}
