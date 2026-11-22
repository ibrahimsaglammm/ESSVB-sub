// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EssvbSub.App.Services;
using EssvbSub.Core.Models;
using EssvbSub.Core.Pipeline;
using EssvbSub.Core.Server;

namespace EssvbSub.App.Views;

public partial class OverlayWindow : Window
{
    private readonly SubtitleStack _stack = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer = new();
    private readonly Dictionary<string, string> _activeSpeakerHex = new(StringComparer.OrdinalIgnoreCase);

    private bool _clickThrough = true;
    private SubtitleStyle _style = new();

    public OverlayWindow()
    {
        InitializeComponent();

        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            RefreshUi();
        };

        Opened += (_, _) =>
        {
            PlaceDefaultBottomCenter();
            PlatformOverlayInterop.SetClickThrough(this, _clickThrough);
        };

        PointerPressed += (_, e) =>
        {
            if (!_clickThrough && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
    }

    public void SetClickThrough(bool clickThrough)
    {
        _clickThrough = clickThrough;
        PlatformOverlayInterop.SetClickThrough(this, clickThrough);
        RefreshUi();
    }

    public void ApplyStyle(SubtitleStyle style)
    {
        _style = style;
        _stack.StackPrevious = style.StackPrevious;
        RefreshUi();
    }

    public void HandleEvent(SubtitleEvent ev)
    {
        double now = _clock.Elapsed.TotalSeconds;
        switch (ev.Kind)
        {
            case SubtitleEventKind.Subtitle:
                if (ev.Speakers is not null)
                {
                    foreach (var sp in ev.Speakers)
                        _activeSpeakerHex[sp.Name] = sp.HexColor;
                }
                _stack.Show(ev.Text, ev.DurationMs, now, ev.Replaces);
                break;

            case SubtitleEventKind.Clear:
                _stack.Clear(now);
                break;
        }
        RefreshUi();
    }

    private void PlaceDefaultBottomCenter()
    {
        if (Screens.Primary is not { } primary) return;
        double scale = primary.Scaling;
        int w = primary.WorkingArea.Width;
        int h = primary.WorkingArea.Height;
        int winW = (int)(Width * scale);
        int winH = (int)(Height * scale);
        Position = new PixelPoint(
            primary.WorkingArea.X + Math.Max(0, (w - winW) / 2),
            primary.WorkingArea.Y + Math.Max(0, h - winH - (int)(36 * scale)));
    }

    private void RefreshUi()
    {
        double now = _clock.Elapsed.TotalSeconds;
        var lines = _stack.Lines(now);

        LinesHost.Children.Clear();
        foreach (var line in lines)
        {
            LinesHost.Children.Add(BuildLineCard(line));
        }

        if (lines.Count == 0 && !_clickThrough)
        {
            LinesHost.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 14, 17, 23)),
                BorderBrush = new SolidColorBrush(Color.Parse("#F5B83D")),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(20, 10),
                Child = new TextBlock
                {
                    Text = "ESSVB-sub Altyazı Konumu (Taşımak için sürükleyin)",
                    FontSize = 20,
                    Foreground = new SolidColorBrush(Color.Parse("#F3EEE4")),
                    FontWeight = FontWeight.SemiBold
                }
            });
        }

        _timer.Stop();
        if (_stack.NextChange(now) is { } next)
        {
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(0.02, next - now));
            _timer.Start();
        }
    }

    private Border BuildLineCard(SubtitleStack.Line line)
    {
        bool isCurrent = line.Role == SubtitleStack.Role.Current;
        double opacity = line.Fading ? 0.25 : line.Role switch
        {
            SubtitleStack.Role.Current => 1.0,
            SubtitleStack.Role.Previous => 0.68,
            _ => 0.50
        };

        byte bgAlpha = (byte)Math.Round(Math.Clamp(_style.BackgroundOpacity, 0, 100) / 100.0 * 255.0 * (isCurrent ? 1.0 : 0.82));
        double fontSize = Math.Max(14, _style.FontSize * (isCurrent ? 0.72 : 0.56) * line.Wrapped.Scale);

        Color textColor = _style.TextColor switch
        {
            SubtitleTextColor.Yellow => Color.Parse("#FFE135"),
            SubtitleTextColor.Cyan => Color.Parse("#38BDF8"),
            _ => Colors.White
        };

        var tb = new TextBlock
        {
            FontSize = fontSize,
            FontWeight = _style.Bold ? FontWeight.Bold : FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(textColor)
        };

        if (_style.SpeakerColors && SpeakerPrefix.Parse(line.Wrapped.Text) is { } split &&
            _activeSpeakerHex.TryGetValue(split.Name, out var hex))
        {
            tb.Inlines?.Add(new Avalonia.Controls.Documents.Run($"{split.Name}: ")
            {
                Foreground = new SolidColorBrush(Color.Parse(hex)),
                FontWeight = FontWeight.Bold
            });
            tb.Inlines?.Add(new Avalonia.Controls.Documents.Run(split.Body));
        }
        else
        {
            tb.Text = line.Wrapped.Text;
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(bgAlpha, 10, 13, 18)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(isCurrent ? 22 : 16, isCurrent ? 9 : 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = opacity,
            Child = tb
        };
    }
}
