// Copyright (c) 2026 ESSVB. All Rights Reserved.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using EssvbSub.Core.Models;

namespace EssvbSub.App.Views;

public partial class RegionSelectorWindow : Window
{
    private Point _start;
    private bool _dragging;

    public (CropRegion Region, int X, int Y, int Width, int Height)? SelectedResult { get; private set; }

    public RegionSelectorWindow()
    {
        InitializeComponent();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                SelectedResult = null;
                Close();
            }
        };

        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _dragging = true;
            _start = e.GetPosition(RootCanvas);
            SelectionBox.IsVisible = true;
            UpdateRect(_start, _start);
        };

        PointerMoved += (_, e) =>
        {
            if (_dragging)
                UpdateRect(_start, e.GetPosition(RootCanvas));
        };

        PointerReleased += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            var end = e.GetPosition(RootCanvas);
            double x = Math.Min(_start.X, end.X);
            double y = Math.Min(_start.Y, end.Y);
            double w = Math.Abs(end.X - _start.X);
            double h = Math.Abs(end.Y - _start.Y);

            if (w >= 40 && h >= 20 && Bounds.Width > 0 && Bounds.Height > 0)
            {
                double scale = DesktopScaling;
                var region = new CropRegion(
                    Top: Math.Clamp(y / Bounds.Height, 0, 0.95),
                    Bottom: Math.Clamp((y + h) / Bounds.Height, 0.05, 1.0),
                    Left: Math.Clamp(x / Bounds.Width, 0, 0.95),
                    Right: Math.Clamp((x + w) / Bounds.Width, 0.05, 1.0)).Clamp();

                int screenX = Position.X + (int)Math.Round(x * scale);
                int screenY = Position.Y + (int)Math.Round(y * scale);
                int screenW = (int)Math.Round(w * scale);
                int screenH = (int)Math.Round(h * scale);
                SelectedResult = (region, screenX, screenY, screenW, screenH);
            }
            Close();
        };
    }

    private void UpdateRect(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(b.X - a.X);
        double h = Math.Abs(b.Y - a.Y);
        Canvas.SetLeft(SelectionBox, x);
        Canvas.SetTop(SelectionBox, y);
        SelectionBox.Width = w;
        SelectionBox.Height = h;
    }
}
