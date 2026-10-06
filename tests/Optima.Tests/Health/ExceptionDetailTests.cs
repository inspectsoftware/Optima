using System.ComponentModel;
using Optima.Core.Health;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Health;

public sealed class ExceptionDetailTests
{
    /// <summary>Windows' text for a code is localized, so the tests ask Windows rather than quote it.</summary>
    private static string WindowsTextFor(int code) => new Win32Exception(code).Message.Trim().TrimEnd('.');

    [Fact]
    public void AFailedWindowsCallKeepsItsCodeAndWindowsOwnText()
    {
        var detail = ExceptionDetail.Capture(new Win32Exception(2, "PowerSetActiveScheme(8c5e7fda) failed"));

        Assert.Equal("Win32Exception", detail.TypeName);
        Assert.Equal(2, detail.NativeErrorCode);
        Assert.Equal(WindowsTextFor(2), detail.NativeErrorText);
        Assert.Equal($"Win32Exception: PowerSetActiveScheme(8c5e7fda) failed (Win32 2: {WindowsTextFor(2)})", detail.Summary);
        Assert.StartsWith($"Win32 2: {WindowsTextFor(2)}", detail.FullText);
        Assert.Contains("PowerSetActiveScheme(8c5e7fda) failed", detail.FullText);
    }

    [Fact]
    public void AMessageThatIsAlreadyWindowsOwnTextIsNotSaidTwice()
    {
        var exception = new Win32Exception(5);

        var detail = ExceptionDetail.Capture(exception);

        Assert.Equal($"Win32Exception: {exception.Message} (Win32 5)", detail.Summary);
    }

    [Fact]
    public void TheNativeCodeIsFoundBehindAWrapper()
    {
        var detail = ExceptionDetail.Capture(
            new InvalidOperationException("Could not switch the plan", new Win32Exception(5, "PowerSetActiveScheme failed")));

        // The row names what was thrown; the code comes from the call underneath it.
        Assert.Equal("InvalidOperationException", detail.TypeName);
        Assert.Equal(5, detail.NativeErrorCode);
        Assert.Contains("Could not switch the plan (Win32 5", detail.Summary);
    }

    [Fact]
    public void ATaskWrapperIsNotWhatTheRowNames()
    {
        var detail = ExceptionDetail.Capture(new AggregateException(new IOException("the disk is full")));

        Assert.Equal("IOException", detail.TypeName);
        Assert.Equal("the disk is full", detail.Message);
        // The full text hides nothing, the wrapper included.
        Assert.Contains("AggregateException", detail.FullText);
    }

    [Fact]
    public void SeveralFailuresAtOnceStayAnAggregate()
    {
        var detail = ExceptionDetail.Capture(new AggregateException(new IOException("one"), new IOException("two")));

        Assert.Equal("AggregateException", detail.TypeName);
    }

    [Fact]
    public void AnOptimaErrorKeepsItsCode()
    {
        var detail = ExceptionDetail.Capture(
            OptimaException.From("VDD_NO_DISPLAY", "The virtual display did not appear", "It never attached."));

        Assert.Equal("VDD_NO_DISPLAY", detail.ErrorCode);
        Assert.Null(detail.NativeErrorCode);
    }

    [Fact]
    public void AnOrdinaryExceptionHasNoNativePart()
    {
        var detail = ExceptionDetail.Capture(new InvalidOperationException("boom"));

        Assert.Null(detail.NativeErrorCode);
        Assert.Equal(string.Empty, detail.NativeErrorText);
        Assert.Equal("InvalidOperationException: boom", detail.Summary);
        Assert.StartsWith("System.InvalidOperationException: boom", detail.FullText);
    }

    [Fact]
    public void ATypedErrorCarriesTheFullDetailOfWhatCausedIt()
    {
        var error = OptimaException.From(
            "POWER_PLAN_REFUSED", "Windows refused the power plan change", "It would not make it active.",
            new Win32Exception(5, "PowerSetActiveScheme failed")).Error;

        // The developer details on an error card are where a remote user copies the cause from.
        Assert.StartsWith($"Win32 5: {WindowsTextFor(5)}", error.DeveloperDetails);
    }
}
