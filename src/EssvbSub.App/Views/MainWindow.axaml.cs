// Copyright (c) 2026 ESSVB. All Rights Reserved.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using EssvbSub.App.ViewModels;
using EssvbSub.Core.Models;
using EssvbSub.Core.Updates;

namespace EssvbSub.App.Views;

public partial class MainWindow : Window
{
    private OverlayWindow? _overlayWindow;
    private MainViewModel? _hookedVm;
    private bool _updateDialogOpen;
    private bool _startupUpdateCheckDone;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
        Opened += OnWindowOpenedAsync;
        Closed += (_, _) =>
        {
            _overlayWindow?.Close();
            _overlayWindow = null;
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private async void OnWindowOpenedAsync(object? sender, EventArgs e)
    {
        if (_startupUpdateCheckDone)
            return;
        _startupUpdateCheckDone = true;

        try
        {
            await Task.Delay(1500);
            if (Vm is { AutoCheckUpdates: true } vm)
                await vm.CheckForUpdatesAsync(userInitiated: false);
        }
        catch { }
    }

    private void HookViewModel()
    {
        if (Vm is not { } vm || ReferenceEquals(_hookedVm, vm))
            return;
        _hookedVm = vm;

        OverlayWindow EnsureOverlay()
        {
            if (_overlayWindow is null)
            {
                _overlayWindow = new OverlayWindow();
                _overlayWindow.BoundsChangedByUser += (l, t, w, h) => vm.SyncOverlayBoundsFromUser(l, t, w, h);
                _overlayWindow.ApplyBounds(vm.OverlayLeft, vm.OverlayTop, vm.OverlayWidth, vm.OverlayHeight, vm.HasSavedOverlayPosition);
            }
            _overlayWindow.ApplyStyle(vm.CurrentStyle);
            return _overlayWindow;
        }

        vm.OverlayVisibilityChanged += visible =>
        {
            if (visible || vm.IsOverlayEditMode)
            {
                var ov = EnsureOverlay();
                if (!ov.IsVisible)
                    ov.Show();
                ov.SetEditMode(vm.IsOverlayEditMode);
                ov.SetClickThrough(vm.ClickThroughOverlay);
            }
            else
            {
                _overlayWindow?.Hide();
            }
        };

        vm.OverlayEditModeChanged += editing =>
        {
            if (editing)
            {
                var ov = EnsureOverlay();
                if (!ov.IsVisible)
                    ov.Show();
                ov.SetEditMode(true);
            }
            else if (_overlayWindow is not null)
            {
                _overlayWindow.SetEditMode(false);
                _overlayWindow.SetClickThrough(vm.ClickThroughOverlay);
                if (!vm.IsRunning)
                    _overlayWindow.Hide();
            }
        };

        vm.OverlayBoundsChanged += (l, t, w, h, hasPos) => _overlayWindow?.ApplyBounds(l, t, w, h, hasPos);
        vm.OverlayClickThroughChanged += clickThrough => _overlayWindow?.SetClickThrough(clickThrough);
        vm.OverlayStyleChanged += style => _overlayWindow?.ApplyStyle(style);
        vm.OverlayEventReceived += ev => _overlayWindow?.HandleEvent(ev);

        vm.SelectRegionRequested += async () =>
        {
            var picker = new RegionSelectorWindow();
            await picker.ShowDialog(this);
            return picker.SelectedResult;
        };

        vm.UpdatePromptRequested += async update => await ShowUpdatePromptDialogAsync(vm, update);
        vm.AppShutdownRequested += () =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(0);
            else
                Close();
        };
    }

    private async Task ShowUpdatePromptDialogAsync(MainViewModel vm, AppUpdateInfo update)
    {
        if (_updateDialogOpen)
            return;

        _updateDialogOpen = true;
        try
        {
            var dialog = new UpdatePromptWindow(vm, update);
            await dialog.ShowDialog(this);
        }
        finally
        {
            _updateDialogOpen = false;
        }
    }

    private void OnSelectPlayStation(object? sender, RoutedEventArgs e) { if (Vm is not null) { Vm.IsHelpTabActive = false; Vm.Mode = OperatingMode.PlayStation; } }
    private void OnSelectPcGame(object? sender, RoutedEventArgs e) { if (Vm is not null) { Vm.IsHelpTabActive = false; Vm.Mode = OperatingMode.PcGame; } }
    private void OnSelectMediaStream(object? sender, RoutedEventArgs e) { if (Vm is not null) { Vm.IsHelpTabActive = false; Vm.Mode = OperatingMode.MediaStream; } }
    private void OnSelectHelpTab(object? sender, RoutedEventArgs e) { if (Vm is not null) Vm.IsHelpTabActive = true; }
    private void OnSelectHelpGuide(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && sender is Control { Tag: string tagText } && int.TryParse(tagText, out int idx))
            Vm.SelectHelpGuideByIndex(idx);
    }
    private void OnPrevHelpGuide(object? sender, RoutedEventArgs e) => Vm?.PreviousHelpGuide();
    private void OnNextHelpGuide(object? sender, RoutedEventArgs e) => Vm?.NextHelpGuide();
    private void OnOpenGitHubRepo(object? sender, RoutedEventArgs e) => Vm?.OpenGitHubRepo();
    private void OnOpenGitHubReleases(object? sender, RoutedEventArgs e) => Vm?.OpenGitHubReleases();
    private async void OnCheckForUpdates(object? sender, RoutedEventArgs e) { if (Vm is not null) await Vm.CheckForUpdatesAsync(userInitiated: true); }
    private void OnOpenUpdateConsent(object? sender, RoutedEventArgs e) => Vm?.RequestUpdateConsentPrompt();
    private void OnDismissUpdateBanner(object? sender, RoutedEventArgs e) => Vm?.DismissUpdateBanner();
    private void OnOpenAppFolder(object? sender, RoutedEventArgs e) => Vm?.OpenAppFolder();
    private void OnOpenConfigFolder(object? sender, RoutedEventArgs e) => Vm?.OpenConfigFolder();

    private void OnOpenHelpImageFullscreen(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedHelpGuideBitmap is not { } bmp)
            return;

        var modal = new Window
        {
            Title = $"ESSVB-sub — {Vm.SelectedHelpGuide.FullTitle}",
            Width = 1280,
            Height = 820,
            MinWidth = 760,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#0A0D12"))
        };

        var closeBtn = new Button
        {
            Content = "Kapat",
            HorizontalAlignment = HorizontalAlignment.Right,
            Classes = { "primary" },
            Margin = new Thickness(0, 10, 0, 0)
        };
        closeBtn.Click += (_, _) => modal.Close();

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(14)
        };
        var img = new Image
        {
            Source = bmp,
            Stretch = Stretch.Uniform
        };
        Grid.SetRow(img, 0);
        Grid.SetRow(closeBtn, 1);
        layout.Children.Add(img);
        layout.Children.Add(closeBtn);
        modal.Content = layout;
        modal.Show(this);
    }
    private async void OnToggleStartStop(object? sender, RoutedEventArgs e) { if (Vm is not null) await Vm.ToggleStartStopAsync(); }
    private void OnRefreshWindows(object? sender, RoutedEventArgs e) => Vm?.RefreshWindows();
    private async void OnSelectRegion(object? sender, RoutedEventArgs e) { if (Vm is not null) await Vm.SelectScreenRegionAsync(); }
    private void OnClearRegion(object? sender, RoutedEventArgs e) => Vm?.ClearScreenRegion();
    private void OnOpenPsnLogin(object? sender, RoutedEventArgs e) => Vm?.OpenPsnLoginPage();
    private async void OnCompletePsnLogin(object? sender, RoutedEventArgs e) { if (Vm is not null) await Vm.CompletePsnLoginAsync(); }
    private async void OnDiscoverPlayStation(object? sender, RoutedEventArgs e) { if (Vm is not null) await Vm.DiscoverPlayStationAsync(); }
    private async void OnRegisterPlayStation(object? sender, RoutedEventArgs e) { if (Vm is not null) await Vm.RegisterPlayStationAsync(); }
    private async void OnDiscoverSamsungTv(object? sender, RoutedEventArgs e) { if (Vm is not null) await Vm.DiscoverSamsungTvAsync(); }
    private async void OnInstallSamsungTv(object? sender, RoutedEventArgs e) { if (Vm is not null) await Vm.InstallSamsungTvAppAsync(); }
    private async void OnLaunchSamsungTv(object? sender, RoutedEventArgs e) { if (Vm is not null) await Vm.LaunchSamsungTvAppAsync(); }
    private void OnApproveTvPairing(object? sender, RoutedEventArgs e) => Vm?.ApproveTvPairing(true);
    private void OnRejectTvPairing(object? sender, RoutedEventArgs e) => Vm?.ApproveTvPairing(false);
    private async void OnRefreshModels(object? sender, RoutedEventArgs e) { if (Vm is not null) await Vm.RefreshModelsAsync(autoStartOllama: true); }
    private async void OnTestTranslation(object? sender, RoutedEventArgs e) { if (Vm is not null) await Vm.TestTranslationConnectionAsync(); }
    private void OnToggleOverlayEdit(object? sender, RoutedEventArgs e) { if (Vm is not null) Vm.IsOverlayEditMode = !Vm.IsOverlayEditMode; }
    private void OnShowOverlayPreview(object? sender, RoutedEventArgs e) => Vm?.ShowOverlayPreview();
    private void OnResetOverlayBounds(object? sender, RoutedEventArgs e) => Vm?.ResetOverlayBounds();
}
