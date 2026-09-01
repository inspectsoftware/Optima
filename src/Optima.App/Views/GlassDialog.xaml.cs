using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Optima.App.Controls;

namespace Optima.App.Views;

/// <summary>Tone of a glass dialog: sets the leading edge and the kicker text.</summary>
public enum DialogTone
{
    Neutral,
    Warning,
    Danger,
}

/// <summary>
/// The one dialog for questions and notices (replaces stock MessageBoxes). A glass strip over
/// its own ambient field, a heading, prose, and up to three buttons; the last button is the
/// primary and answers Enter, the first answers Escape.
/// </summary>
public partial class GlassDialog : Window
{
    public static readonly DependencyProperty HeadingProperty = DependencyProperty.Register(
        nameof(Heading), typeof(string), typeof(GlassDialog), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(string), typeof(GlassDialog), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty KickerProperty = DependencyProperty.Register(
        nameof(Kicker), typeof(string), typeof(GlassDialog), new PropertyMetadata("OPTIMA"));

    public static readonly DependencyProperty EdgeBrushProperty = DependencyProperty.Register(
        nameof(EdgeBrush), typeof(Brush), typeof(GlassDialog), new PropertyMetadata(null));

    public string Heading { get => (string)GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }
    public string Body { get => (string)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public string Kicker { get => (string)GetValue(KickerProperty); set => SetValue(KickerProperty, value); }
    public Brush? EdgeBrush { get => (Brush?)GetValue(EdgeBrushProperty); set => SetValue(EdgeBrushProperty, value); }

    /// <summary>Index of the button that was pressed; -1 when closed another way.</summary>
    public int Answer { get; private set; } = -1;

    public GlassDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the dialog modally and returns the index of the chosen button (-1 if closed).
    /// The owner is used when it is on screen; otherwise the dialog centers on the screen
    /// and stays on top so it cannot get lost behind a game.
    /// </summary>
    public static int Ask(Window? owner, string heading, string body, DialogTone tone, params string[] buttons)
    {
        var dialog = new GlassDialog
        {
            Heading = heading,
            Body = body,
            Kicker = tone switch
            {
                DialogTone.Warning => "OPTIMA · ATTENTION",
                DialogTone.Danger => "OPTIMA · CONFIRM",
                _ => "OPTIMA",
            },
        };
        dialog.EdgeBrush = tone switch
        {
            DialogTone.Warning => dialog.TryFindResource("Brush.Warn") as Brush,
            DialogTone.Danger => dialog.TryFindResource("Brush.Fail") as Brush,
            _ => dialog.TryFindResource("Brush.Strip.Edge") as Brush,
        };
        if (tone != DialogTone.Neutral)
        {
            dialog.Ambient.State = AmbientState.Attention;
        }

        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var last = i == buttons.Length - 1;
            var first = i == 0 && buttons.Length > 1;
            var button = new Button
            {
                Content = buttons[i],
                Margin = new Thickness(0, 0, last ? 0 : 8, 0),
                MinWidth = 96,
                IsDefault = last,
                IsCancel = first,
            };
            button.SetResourceReference(StyleProperty, last ? "PrimaryButton" : first ? "GhostButton" : "SecondaryButton");
            button.Click += (_, _) =>
            {
                dialog.Answer = index;
                dialog.DialogResult = true;
            };
            dialog.Buttons.Items.Add(button);
        }

        if (owner is { IsVisible: true } && owner.WindowState != WindowState.Minimized)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
            dialog.ShowInTaskbar = true;
        }
        dialog.ShowDialog();
        return dialog.Answer;
    }

    /// <summary>Yes/no convenience: true when the last (primary) button was chosen.</summary>
    public static bool Confirm(Window? owner, string heading, string body, string cancel, string confirm, DialogTone tone = DialogTone.Warning)
        => Ask(owner, heading, body, tone, cancel, confirm) == 1;

    public static void Notice(Window? owner, string heading, string body, DialogTone tone = DialogTone.Neutral)
        => Ask(owner, heading, body, tone, "OK");
}
