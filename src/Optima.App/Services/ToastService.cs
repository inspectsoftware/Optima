using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Health;
using Optima.Core.Models;

namespace Optima.App.Services;

public enum ToastKind
{
    Ok,
    Info,
    Warn,
}

/// <summary>One notice in the corner of the window.</summary>
public sealed partial class ToastItem : ObservableObject
{
    public required string Title { get; init; }

    public required string Text { get; init; }

    public ToastKind Kind { get; init; }

    /// <summary>The one thing the notice lets the player do, or empty when it only informs.</summary>
    [ObservableProperty]
    private string _actionLabel = string.Empty;

    public IRelayCommand? ActionCommand { get; init; }

    public required IRelayCommand DismissCommand { get; init; }

    /// <summary>True from the moment it is dismissed until it has faded and is taken off the list.</summary>
    [ObservableProperty]
    private bool _leaving;
}

/// <summary>
/// Notices over the page, whatever page is open. Optima now does things by itself, and a repair
/// nobody was told about is indistinguishable from a program acting on its own: every one is said
/// here, and the ones that interrupt are said before they run, with time to stop them.
/// </summary>
public sealed class ToastService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);

    /// <summary>More than this at once is a wall, not a notice; the oldest go first.</summary>
    private const int MaxVisible = 3;

    public ObservableCollection<ToastItem> Toasts { get; } = [];

    /// <summary>Shows a notice that goes away by itself. Safe to call from any thread.</summary>
    public void Show(string title, string text, ToastKind kind, string? actionLabel = null, Action? action = null)
        => OnUi(() =>
        {
            ToastItem? toast = null;
            toast = new ToastItem
            {
                Title = title,
                Text = text,
                Kind = kind,
                ActionLabel = actionLabel ?? string.Empty,
                ActionCommand = action is null ? null : new RelayCommand(() =>
                {
                    Remove(toast);
                    action();
                }),
                DismissCommand = new RelayCommand(() => Remove(toast)),
            };
            Add(toast);

            var timer = new DispatcherTimer { Interval = Lifetime };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Remove(toast);
            };
            timer.Start();
        });

    /// <summary>
    /// Says what is about to happen and waits. True when the time ran out with nobody objecting;
    /// false when the player pressed cancel. With no window to show it in, nobody can object, so
    /// the answer is no.
    /// </summary>
    public Task<bool> ConfirmCountdownAsync(string title, string text, TimeSpan delay, CancellationToken ct = default)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            return Task.FromResult(false);
        }

        dispatcher.BeginInvoke(() =>
        {
            var remaining = (int)Math.Ceiling(delay.TotalSeconds);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            ToastItem? toast = null;
            CancellationTokenRegistration registration = default;

            void Finish(bool? result)
            {
                timer.Stop();
                registration.Dispose();
                Remove(toast);
                if (result is { } value)
                {
                    answer.TrySetResult(value);
                }
                else
                {
                    answer.TrySetCanceled(ct);
                }
            }

            toast = new ToastItem
            {
                Title = title,
                Text = text,
                Kind = ToastKind.Warn,
                ActionLabel = $"cancel ({remaining})",
                ActionCommand = new RelayCommand(() => Finish(false)),
                DismissCommand = new RelayCommand(() => Finish(false)),
            };
            Add(toast);

            timer.Tick += (_, _) =>
            {
                remaining--;
                if (remaining <= 0)
                {
                    Finish(true);
                    return;
                }
                toast.ActionLabel = $"cancel ({remaining})";
            };
            timer.Start();
            registration = ct.Register(() => dispatcher.BeginInvoke(() => Finish(null)));
        });
        return answer.Task;
    }

    private void Add(ToastItem toast)
    {
        Toasts.Add(toast);
        while (Toasts.Count > MaxVisible)
        {
            Toasts.RemoveAt(0);
        }
    }

    /// <summary>Lets the notice fade and the stack close up, then takes it off the list. Called on the UI thread.</summary>
    private async void Remove(ToastItem? toast)
    {
        if (toast is null || toast.Leaving)
        {
            return;
        }
        toast.Leaving = true;
        await Task.Delay(Motion.Duration(Optima.Core.Theming.MotionSpec.MoveMs));
        Toasts.Remove(toast);
    }

    private static void OnUi(Action action)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            return;
        }
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}

/// <summary>A repair that ran, as the few words a notice has room for.</summary>
public static class RepairNotice
{
    /// <summary>What the repair was about: the error guide's title for the issue's code.</summary>
    public static string About(RepairAttempt attempt)
    {
        var separator = attempt.IssueKey.IndexOf('|');
        var code = separator >= 0 ? attempt.IssueKey[..separator] : attempt.IssueKey;
        return ErrorCatalog.Find(code)?.Title ?? code;
    }

    public static (string Title, string Text, ToastKind Kind) Describe(RepairAttempt attempt) => attempt.Outcome switch
    {
        RepairOutcome.Fixed => ("Optima repaired: " + About(attempt), attempt.Summary, ToastKind.Ok),
        RepairOutcome.NotNeeded => ("Nothing needed repairing: " + About(attempt), attempt.Summary, ToastKind.Info),
        RepairOutcome.NeedsUser => ("A repair needs you: " + About(attempt), attempt.Summary, ToastKind.Warn),
        _ => ("A repair did not work: " + About(attempt), attempt.Summary, ToastKind.Warn),
    };
}
