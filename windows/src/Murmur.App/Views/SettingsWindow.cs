using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Core;
using Murmur.Speech;

namespace Murmur.App.Views;

/// <summary>Settings: the hotkey and the model.</summary>
public sealed class SettingsWindow : Window
{
    /// <summary>
    /// The keys offered, in recommendation order.
    /// </summary>
    /// <remarks>
    /// Right Alt is included but listed last and carries a warning: on German, Polish, UK,
    /// Nordic and most Latin-American layouts it is AltGr, and binding push-to-talk there
    /// breaks typing <c>@</c>, <c>€</c>, <c>\</c> and <c>|</c>.
    /// </remarks>
    private static readonly (int Key, string Label, string? Warning)[] Keys =
    [
        (0xA3, "RIGHT CTRL", null),
        (0xA1, "RIGHT SHIFT", null),
        (0x14, "CAPS LOCK", null),
        (0x7C, "F13", null),
        (0x86, "COPILOT", "The Copilot key is the one key Murmur swallows, so holding it "
                        + "dictates instead of opening Copilot. It is not a single key: it "
                        + "sends Left Win + Left Shift + F23, and only the F23 is taken. If "
                        + "nothing happens when you hold it, run murmur --keylog to see what "
                        + "your keyboard actually sends — a few vendors send Win+C instead."),
        (0xA5, "RIGHT ALT", "Right Alt is AltGr on many European layouts — binding it here "
                          + "will interfere with typing @, €, \\ and |."),
    ];

    private readonly AppSettings _settings;
    private readonly StackPanel _keyRow;
    private readonly TextBlock _keyWarning;
    private bool _downloading;

    /// <summary>Builds the settings window.</summary>
    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;

        Title = "Murmur Settings";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Background = Tokens.Brushes.Chassis;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _keyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
        };

        _keyWarning = new TextBlock
        {
            FontFamily = Tokens.Fonts.Grotesque,
            FontSize = Tokens.Fonts.Label,
            Foreground = new SolidColorBrush(Tokens.Colors.MeterAmber),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        foreach (var (key, label, warning) in Keys)
        {
            var button = new TransportKey { Content = label, EngagedColor = Tokens.Colors.Ink };
            button.Click += (_, _) => SelectKey(key, warning);
            _keyRow.Children.Add(button);
        }

        Content = BuildContent();
        SelectKey(_settings.Data.PushToTalkKey, WarningFor(_settings.Data.PushToTalkKey));
    }

    private static string? WarningFor(int key) =>
        Keys.FirstOrDefault(k => k.Key == key).Warning;

    private StackPanel BuildContent() => new StackPanel
    {
        Margin = new Thickness(Tokens.Space.Panel),
        Spacing = Tokens.Space.Wide,
        Children =
        {
            Section("PUSH TO TALK", new StackPanel
            {
                Spacing = Tokens.Space.Snug,
                Children =
                {
                    _keyRow,
                    _keyWarning,
                    Note("Hold this key anywhere to dictate. The key is passed through to the "
                       + "focused app rather than swallowed, so it never gets stuck down — "
                       + "the Copilot key is the single exception, because passing it on "
                       + "would open Copilot every time you dictate."),
                },
            }),

            Section("MODEL", BuildModelSection()),

            Section("BEHAVIOUR", new StackPanel
            {
                Spacing = Tokens.Space.Snug,
                Children =
                {
                    Toggle("Type transcripts into the focused app", _settings.Data.InjectText,
                        v => Save(_settings.Data with { InjectText = v })),
                    Toggle("Keep a transcript history", _settings.Data.KeepHistory,
                        v => Save(_settings.Data with { KeepHistory = v })),
                    Toggle("Pause music and video while dictating",
                        _settings.Data.PauseMediaWhileDictating,
                        v => Save(_settings.Data with { PauseMediaWhileDictating = v })),
                    Note("The microphone hears the speakers, so anything playing lands in the "
                       + "transcript. Takes effect on restart."),
                },
            }),
        },
    };

    private StackPanel BuildModelSection()
    {
        var located = ParakeetTranscriber.Locate();
        var found = located is not null;

        var lamp = new Lamp
        {
            IsLit = found,
            LampColor = found ? Tokens.Colors.MeterGreen : Tokens.Colors.MeterAmber,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var caption = new TextBlock
        {
            Text = found ? "Parakeet ready" : "Model not installed",
            FontFamily = Tokens.Fonts.Grotesque,
            FontSize = Tokens.Fonts.Body,
            Foreground = Tokens.Brushes.Ink,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            Children = { lamp, caption },
        };

        var detail = found
            // Showing the resolved path matters: "model not found" is unactionable without
            // knowing which directory was actually checked.
            ? Note($"Loaded from {located}")
            : Note("Windows has no built-in speech engine, so Murmur cannot transcribe until "
                 + "the Parakeet model is installed. It is a 661 MB one-time download, "
                 + "fetched straight from the publisher and kept in your user folder. "
                 + "Transcription itself never touches the network.");

        var panel = new StackPanel { Spacing = Tokens.Space.Snug, Children = { status, detail } };

        if (!found) panel.Children.Add(BuildDownloader(lamp, caption, detail));

        return panel;
    }

    /// <summary>
    /// The install step: a button, a bar, and a line of plain text.
    /// </summary>
    /// <remarks>
    /// Deliberately not a wizard. There is exactly one thing to fetch and one place it can go,
    /// so anything more than a button would be ceremony around a single decision the user has
    /// already made by opening this window.
    /// </remarks>
    private StackPanel BuildDownloader(Lamp lamp, TextBlock caption, TextBlock detail)
    {
        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Height = 6,
            IsVisible = false,
            Foreground = new SolidColorBrush(Tokens.Colors.MeterGreen),
        };

        var line = Note(string.Empty);
        line.IsVisible = false;

        var button = new TransportKey { Content = "DOWNLOAD MODEL", EngagedColor = Tokens.Colors.Ink };

        button.Click += async (_, _) =>
        {
            if (_downloading) return;
            _downloading = true;

            button.IsEnabled = false;
            bar.IsVisible = true;
            line.IsVisible = true;
            line.Text = "Starting…";

            // Marshalled back to the UI thread by Progress<T>, which captures this context at
            // construction — the download itself runs off it.
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                if (p.Fraction is { } fraction)
                {
                    bar.IsIndeterminate = false;
                    bar.Value = fraction;
                }
                else
                {
                    bar.IsIndeterminate = true;
                }

                line.Text = p.ToString();
            });

            try
            {
                // Its own client, with a timeout long enough for 622 MB on a slow line. The
                // default 100 seconds would abort every download on anything but fast fibre.
                using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
                var directory = await ModelDownloader.DownloadAsync(http, progress: progress).ConfigureAwait(true);

                lamp.IsLit = true;
                lamp.LampColor = Tokens.Colors.MeterGreen;
                caption.Text = "Parakeet ready";
                detail.Text = $"Loaded from {directory}";
                bar.IsVisible = false;
                line.Text = "Installed. Restart Murmur to load it.";
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
            {
                // Nothing partial survives — the downloader deletes its own .part files — so
                // pressing the button again is a clean retry rather than a repair.
                bar.IsVisible = false;
                line.Text = $"Download failed: {e.Message} Press to try again.";
                button.IsEnabled = true;
                _downloading = false;
            }
        };

        return new StackPanel
        {
            Spacing = Tokens.Space.Snug,
            Children = { button, bar, line },
        };
    }

    private void SelectKey(int key, string? warning)
    {
        for (var i = 0; i < Keys.Length; i++)
        {
            ((TransportKey)_keyRow.Children[i]).IsEngaged = Keys[i].Key == key;
        }

        _keyWarning.Text = warning ?? string.Empty;
        _keyWarning.IsVisible = warning is not null;

        if (_settings.Data.PushToTalkKey != key) Save(_settings.Data with { PushToTalkKey = key });
    }

    private void Save(SettingsData data) => _settings.Update(data);

    private static BrushedPanel Section(string label, Control content) => new BrushedPanel
    {
        Child = new StackPanel
        {
            Margin = new Thickness(Tokens.Space.Roomy),
            Spacing = Tokens.Space.Base,
            Children = { new Silkscreen { Text = label, IsLarge = true }, content },
        },
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        FontFamily = Tokens.Fonts.Grotesque,
        FontSize = Tokens.Fonts.Label,
        Foreground = new SolidColorBrush(Tokens.Colors.InkSecondary),
        TextWrapping = TextWrapping.Wrap,
    };

    private static CheckBox Toggle(string label, bool value, Action<bool> onChange)
    {
        var box = new CheckBox
        {
            IsChecked = value,
            Content = new TextBlock
            {
                Text = label,
                FontFamily = Tokens.Fonts.Grotesque,
                FontSize = Tokens.Fonts.Body,
                Foreground = Tokens.Brushes.Ink,
            },
        };

        box.IsCheckedChanged += (_, _) => onChange(box.IsChecked ?? false);
        return box;
    }
}
