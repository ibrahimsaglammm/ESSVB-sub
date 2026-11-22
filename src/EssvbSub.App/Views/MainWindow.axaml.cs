// Copyright (c) 2026 ESSVB. All Rights Reserved.

using Avalonia.Controls;
using Avalonia.Interactivity;
using EssvbSub.App.ViewModels;
using EssvbSub.Core.Models;

namespace EssvbSub.App.Views;

public partial class MainWindow : Window
{
    private OverlayWindow? _overlayWindow;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
        Closed += (_, _) =>
        {
            _overlayWindow?.Close();
            _overlayWindow = null;
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void HookViewModel()
    {
        if (Vm is not { } vm) return;

        vm.OverlayVisibilityChanged += visible =>
        {
            if (visible)
            {
                _overlayWindow ??= new OverlayWindow();
                _overlayWindow.ApplyStyle(vm.CurrentStyle);
                if (!_overlayWindow.IsVisible)
                    _overlayWindow.Show();
                _overlayWindow.SetClickThrough(vm.ClickThroughOverlay);
            }
            else
            {
                _overlayWindow?.Hide();
            }
        };

        vm.OverlayClickThroughChanged += clickThrough => _overlayWindow?.SetClickThrough(clickThrough);
        vm.OverlayStyleChanged += style => _overlayWindow?.ApplyStyle(style);
        vm.OverlayEventReceived += ev => _overlayWindow?.HandleEvent(ev);

        vm.SelectRegionRequested += async () =>
        {
            var picker = new RegionSelectorWindow();
            await picker.ShowDialog(this);
            return picker.SelectedResult;
        };
    }

    private void OnSelectPlayStation(object? sender, RoutedEventArgs e) { if (Vm is not null) Vm.Mode = OperatingMode.PlayStation; }
    private void OnSelectPcGame(object? sender, RoutedEventArgs e) { if (Vm is not null) Vm.Mode = OperatingMode.PcGame; }
    private void OnSelectMediaStream(object? sender, RoutedEventArgs e) { if (Vm is not null) Vm.Mode = OperatingMode.MediaStream; }
    private void OnToggleStartStop(object? sender, RoutedEventArgs e) => Vm?.ToggleStartStop();
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
    private async void OnRefreshModels(object? sender, RoutedEventArgs e) { if (Vm is not null) await Vm.RefreshModelsAsync(); }
    private async void OnTestTranslation(object? sender, RoutedEventArgs e) { if (Vm is not null) await Vm.TestTranslationConnectionAsync(); }
}
