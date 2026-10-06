using System.ComponentModel;
using System.Reflection;
using Optima.Core.Models;

namespace Optima.Core.Health;

/// <summary>
/// What an exception has to say, taken once where it is caught. The log page used to keep a type
/// name and a message, which for a failed Windows call is the least useful part: the code Windows
/// returned and its own description of it were dropped, and with them any way to tell one
/// "X failed" from another.
/// </summary>
public sealed record ExceptionDetail
{
    public required string TypeName { get; init; }

    public required string Message { get; init; }

    public int HResult { get; init; }

    /// <summary>The Win32 error code, when a failed Windows call is anywhere in the chain.</summary>
    public int? NativeErrorCode { get; init; }

    /// <summary>Windows' own description of <see cref="NativeErrorCode"/>.</summary>
    public string NativeErrorText { get; init; } = string.Empty;

    /// <summary>The Optima error code, when the chain carries an <see cref="OptimaException"/>.</summary>
    public string ErrorCode { get; init; } = string.Empty;

    /// <summary>The native error line, then the exception with its stack and inner exceptions.</summary>
    public required string FullText { get; init; }

    /// <summary>One line for a log row: type, message, and the native error when there is one.</summary>
    public string Summary => NativeErrorCode is not { } code
        ? $"{TypeName}: {Message}"
        // A message that is already Windows' own text does not need it said twice.
        : NativeErrorText.Length == 0 || Message.Contains(NativeErrorText, StringComparison.OrdinalIgnoreCase)
            ? $"{TypeName}: {Message} (Win32 {code})"
            : $"{TypeName}: {Message} (Win32 {code}: {NativeErrorText})";

    public static ExceptionDetail Capture(Exception exception)
    {
        var shown = Unwrap(exception);
        var native = Chain(shown).OfType<Win32Exception>().FirstOrDefault(e => e.NativeErrorCode != 0);
        var typed = Chain(shown).OfType<OptimaException>().FirstOrDefault();
        var nativeText = native is null ? string.Empty : DescribeNative(native.NativeErrorCode);

        return new ExceptionDetail
        {
            TypeName = shown.GetType().Name,
            Message = shown.Message,
            HResult = shown.HResult,
            NativeErrorCode = native?.NativeErrorCode,
            NativeErrorText = nativeText,
            ErrorCode = typed?.Error.Code ?? string.Empty,
            // The original, wrappers included: a full text that hid a layer would not be full.
            FullText = native is null
                ? exception.ToString()
                : $"Win32 {native.NativeErrorCode}: {nativeText}{Environment.NewLine}{exception}",
        };
    }

    /// <summary>
    /// A task or a reflection call wraps the one exception that matters in a type that says nothing;
    /// the row should name the real one.
    /// </summary>
    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: 1 } or TargetInvocationException
            && exception.InnerException is { } inner)
        {
            exception = inner;
        }
        return exception;
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    /// <summary>The exception built from a bare code carries the text Windows has for it.</summary>
    private static string DescribeNative(int code)
        => new Win32Exception(code).Message.Trim().TrimEnd('.');
}
