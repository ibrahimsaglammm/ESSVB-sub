// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
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
    private static readonly string[] SpeakerPalette =
    [
        "#F5B83D", // ESSVB Gold
        "#38BDF8", // Sky Cyan
        "#34D399", // Emerald
        "#F472B6", // Rose Pink
        "#A78BFA", // Soft Violet
        "#FB923C"  // Warm Amber
    ];

    private readonly SubtitleStack _stack = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer = new();
    private readonly Dictionary<string, string> _activeSpeakerHex = new(StringComparer.OrdinalIgnoreCase);

    private bool _clickThrough = true;
    private bool _editMode;
    private bool _applyingBounds;
    private SubtitleStyle _style = new();

    public event Action<double, double, double, double>? BoundsChangedByUser;

    public OverlayWindow()
    {
        InitializeComponent();

        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            RefreshUi();
        };

        Opened += (_, _) => ApplyNativeWindowMode();

        PointerPressed += (_, e) =>
        {
            if ((_editMode || !_clickThrough) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };

        PositionChanged += (_, _) => NotifyUserBoundsChanged();
        SizeChanged += (_, _) => NotifyUserBoundsChanged();
    }

    public void SetClickThrough(bool clickThrough)
    {
        _clickThrough = clickThrough;
        ApplyNativeWindowMode();
        RefreshUi();
    }

    public void SetEditMode(bool editing)
    {
        _editMode = editing;
        EditChrome.IsVisible = editing;
        Focusable = editing;
        ContentHost.Margin = editing ? new Thickness(16, 36, 16, 14) : new Thickness(16, 8, 16, 10);
        ApplyNativeWindowMode();
        RefreshUi();
    }

    public void ApplyStyle(SubtitleStyle style)
    {
        _style = style;
        _stack.StackPrevious = style.StackPrevious;
        RefreshUi();
    }

    public void ApplyBounds(double left, double top, double width, double height, bool hasSavedPosition)
    {
        _applyingBounds = true;
        try
        {
            double targetW = Math.Clamp(width, MinWidth, 3840);
            double targetH = Math.Clamp(height, MinHeight, 1080);
            Width = targetW;
            Height = targetH;

            if (!hasSavedPosition)
            {
                PlaceDefaultBottomCenter(targetW, targetH);
            }
            else if (Screens.Primary is { } primary)
            {
                double scale = primary.Scaling;
                int pixW = (int)Math.Round(targetW * scale);
                int pixH = (int)Math.Round(targetH * scale);
                int minX = primary.WorkingArea.X;
                int minY = primary.WorkingArea.Y;
                int maxX = Math.Max(minX, primary.WorkingArea.X + primary.WorkingArea.Width - Math.Min(pixW, primary.WorkingArea.Width));
                int maxY = Math.Max(minY, primary.WorkingArea.Y + primary.WorkingArea.Height - Math.Min(pixH, primary.WorkingArea.Height));
                Position = new PixelPoint(
                    Math.Clamp((int)Math.Round(left), minX, maxX),
                    Math.Clamp((int)Math.Round(top), minY, maxY));
            }
            else
            {
                Position = new PixelPoint((int)Math.Round(left), (int)Math.Round(top));
            }
        }
        finally
        {
            _applyingBounds = false;
        }
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

    private void OnResizeGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_editMode && _clickThrough) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { Tag: string edgeTag }) return;

        WindowEdge? edge = edgeTag switch
        {
            "West" => WindowEdge.West,
            "East" => WindowEdge.East,
            "North" => WindowEdge.North,
            "South" => WindowEdge.South,
            "NorthWest" => WindowEdge.NorthWest,
            "NorthEast" => WindowEdge.NorthEast,
            "SouthWest" => WindowEdge.SouthWest,
            "SouthEast" => WindowEdge.SouthEast,
            _ => null
        };

        if (edge is { } target)
        {
            e.Handled = true;
            BeginResizeDrag(target, e);
        }
    }

    private void ApplyNativeWindowMode()
    {
        bool effectiveClickThrough = !_editMode && _clickThrough;
        PlatformOverlayInterop.SetClickThrough(this, effectiveClickThrough);
    }

    private void NotifyUserBoundsChanged()
    {
        if (_applyingBounds || (!_editMode && _clickThrough))
            return;

        if (double.IsNaN(Width) || double.IsNaN(Height) || Width < MinWidth || Height < MinHeight)
            return;

        BoundsChangedByUser?.Invoke(Position.X, Position.Y, Math.Round(Width), Math.Round(Height));
    }

    private void PlaceDefaultBottomCenter(double targetW, double targetH)
    {
        if (Screens.Primary is not { } primary) return;
        double scale = primary.Scaling;
        int w = primary.WorkingArea.Width;
        int h = primary.WorkingArea.Height;
        int winW = (int)Math.Round(targetW * scale);
        int winH = (int)Math.Round(targetH * scale);
        Position = new PixelPoint(
            primary.WorkingArea.X + Math.Max(0, (w - winW) / 2),
            primary.WorkingArea.Y + Math.Max(0, h - winH - (int)(36 * scale)));
    }

    private void RefreshUi()
    {
        double now = _clock.Elapsed.TotalSeconds;
        var lines = _stack.Lines(now);

        LinesHost.Children.Clear();
        bool isBand = _style.BackgroundShape == SubtitleBackgroundShape.Band;
        LinesHost.HorizontalAlignment = isBand ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;

        if (lines.Count > 0)
        {
            foreach (var line in lines)
                LinesHost.Children.Add(BuildLineCard(line));
        }
        else if (_editMode || !_clickThrough)
        {
            // Live multi-speaker preview while positioning/styling the overlay
            if (_style.StackPrevious)
                LinesHost.Children.Add(BuildLineCard(new SubtitleStack.Line(-1, "ATREUS: Dikkat et, orada biri var!", SubtitleStack.Role.Previous, Fading: false)));
            LinesHost.Children.Add(BuildLineCard(new SubtitleStack.Line(-2, "KRATOS: Yanımda kal. Yolculuğumuz yeni başlıyor.", SubtitleStack.Role.Current, Fading: false)));
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
            SubtitleStack.Role.Previous => 0.72,
            _ => 0.52
        };

        int rawBgOpacity = Math.Clamp(_style.BackgroundOpacity, 0, 100);
        byte bgAlpha = rawBgOpacity <= 2
            ? (byte)0
            : (byte)Math.Round(rawBgOpacity / 100.0 * 255.0 * (isCurrent ? 1.0 : 0.82));

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

        // Crisp black outline/drop-shadow whenever background dimming is low or removed (0%)
        if (rawBgOpacity < 55)
        {
            tb.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = rawBgOpacity <= 5 ? 6 : 4,
                OffsetX = 0,
                OffsetY = 2,
                Opacity = 1.0
            };
        }

        string? accentHex = ApplyMultiSpeakerRuns(tb, line.Wrapped.Text);

        bool isBand = _style.BackgroundShape == SubtitleBackgroundShape.Band;
        IBrush cardBg = bgAlpha == 0
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(bgAlpha, 10, 13, 18));

        var border = new Border
        {
            Background = cardBg,
            CornerRadius = new CornerRadius(isBand ? 6 : 10),
            Padding = bgAlpha == 0
                ? new Thickness(isCurrent ? 10 : 8, 2)
                : new Thickness(isCurrent ? 22 : 16, isCurrent ? 9 : 6),
            HorizontalAlignment = isBand ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
            Opacity = opacity,
            Child = tb
        };

        if (bgAlpha > 0 && !string.IsNullOrEmpty(accentHex) && Color.TryParse(accentHex, out var accentColor))
        {
            border.BorderBrush = new SolidColorBrush(accentColor);
            border.BorderThickness = new Thickness(4, 0, 0, 0);
        }

        return border;
    }

    private string? ApplyMultiSpeakerRuns(TextBlock tb, string text)
    {
        if (_style.SpeakerColors && SpeakerPrefix.Parse(text) is { } split)
        {
            string hex = ResolveSpeakerHex(split.Name);
            tb.Inlines?.Add(new Run($"{split.Name}: ")
            {
                Foreground = new SolidColorBrush(Color.Parse(hex)),
                FontWeight = FontWeight.Bold
            });
            tb.Inlines?.Add(new Run(split.Body));
            return hex;
        }

        if (_style.SpeakerColors && text.Contains('\n'))
        {
            string[] splitLines = text.Split('\n');
            bool hasDialogueMarkers = splitLines.Length > 1 &&
                splitLines.Any(l => l.TrimStart() is { Length: > 1 } t && (t[0] == '-' || t[0] == '—' || t[0] == '•'));

            if (hasDialogueMarkers)
            {
                for (int i = 0; i < splitLines.Length; i++)
                {
                    if (i > 0) tb.Inlines?.Add(new LineBreak());
                    string colorHex = SpeakerPalette[i % SpeakerPalette.Length];
                    tb.Inlines?.Add(new Run(splitLines[i])
                    {
                        Foreground = new SolidColorBrush(Color.Parse(colorHex))
                    });
                }
                return SpeakerPalette[0];
            }
        }

        tb.Text = text;
        return null;
    }

    private string ResolveSpeakerHex(string speakerName)
    {
        if (_activeSpeakerHex.TryGetValue(speakerName, out var hex))
            return hex;

        int idx = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(speakerName)) % SpeakerPalette.Length;
        string picked = SpeakerPalette[idx];
        _activeSpeakerHex[speakerName] = picked;
        return picked;
    }
}
