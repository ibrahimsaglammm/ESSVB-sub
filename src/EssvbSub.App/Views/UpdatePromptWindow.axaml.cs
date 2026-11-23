// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using EssvbSub.App.ViewModels;
using EssvbSub.Core;
using EssvbSub.Core.Updates;

namespace EssvbSub.App.Views;

public partial class UpdatePromptWindow : Window
{
    private readonly MainViewModel? _vm;
    private readonly AppUpdateInfo? _update;
    private readonly bool _canInAppInstall;
    private bool _installing;

    public UpdatePromptWindow()
    {
        InitializeComponent();
    }

    public UpdatePromptWindow(MainViewModel vm, AppUpdateInfo update) : this()
    {
        _vm = vm;
        _update = update;
        _canInAppInstall = update.CanInAppUpdateWindows && AppInfo.IsPackagedWindowsExecutable;

        VersionBadgeText.Text = $"v{update.CurrentVersion} ➔ v{update.LatestVersion}";
        ReleaseTitleText.Text = string.IsNullOrWhiteSpace(update.ReleaseName)
            ? $"ESSVB-sub v{update.LatestVersion}"
            : update.ReleaseName;

        string sizeLabel = string.IsNullOrEmpty(update.FormattedSize) ? "Bilinmiyor" : update.FormattedSize;
        ReleaseMetaText.Text = _canInAppInstall
            ? $"Boyut: {sizeLabel}  •  Yayın: {update.PublishedDateLocal}  •  SHA-256 doğrulamalı otomatik kurulum"
            : $"Yayın: {update.PublishedDateLocal}  •  GitHub Releases üzerinden indirilebilir";

        if (!_canInAppInstall)
        {
            PlatformHintText.Text = OperatingSystem.IsLinux()
                ? "CachyOS / Linux: Proje klasöründe `git pull && ./install-cachyos.sh` ile güncelleyebilir veya GitHub sayfasını açabilirsiniz."
                : "Kaynak koddan (dotnet run) çalıştırıldığı için tek tıkla GitHub sayfasından indirip açabilirsiniz.";
            PlatformHintText.IsVisible = true;
            InstallButton.Content = "GitHub'dan İndir";
            OpenGitHubButton.IsVisible = false;
        }

        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(update.ReleaseNotes)
            ? "Bu sürüm için ek sürüm notu girilmemiş."
            : update.ReleaseNotes;

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => _vm.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName is nameof(MainViewModel.UpdateProgressPercent) or nameof(MainViewModel.UpdateProgressText))
        {
            Dispatcher.UIThread.Post(() =>
            {
                DownloadProgressBar.Value = _vm.UpdateProgressPercent;
                ProgressStatusText.Text = _vm.UpdateProgressText;
            });
        }
    }

    private async void OnInstallUpdate(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || _update is null || _installing)
            return;

        if (!_canInAppInstall)
        {
            _vm.OpenGitHubReleaseUrl(_update.HtmlUrl);
            Close();
            return;
        }

        _installing = true;
        InstallButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        OpenGitHubButton.IsEnabled = false;
        ResultText.IsVisible = false;
        ProgressPanel.IsVisible = true;
        DownloadProgressBar.Value = 2;
        ProgressStatusText.Text = "Güncelleme başlatılıyor…";

        try
        {
            var result = await _vm.InstallAvailableUpdateAsync();
            if (!result.Success)
            {
                ResultText.Text = result.Message;
                ResultText.Foreground = new SolidColorBrush(Color.Parse("#FF6B7A"));
                ResultText.IsVisible = true;
                _installing = false;
                InstallButton.IsEnabled = true;
                SkipButton.IsEnabled = true;
                LaterButton.IsEnabled = true;
                OpenGitHubButton.IsEnabled = true;
                return;
            }

            ResultText.Text = result.Message;
            ResultText.Foreground = new SolidColorBrush(Color.Parse("#34D399"));
            ResultText.IsVisible = true;
            if (!result.RequiresAppExit)
            {
                LaterButton.Content = "Kapat";
                LaterButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            ResultText.Text = $"Güncelleme hatası: {ex.Message}";
            ResultText.Foreground = new SolidColorBrush(Color.Parse("#FF6B7A"));
            ResultText.IsVisible = true;
            _installing = false;
            InstallButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            OpenGitHubButton.IsEnabled = true;
        }
    }

    private void OnOpenGitHub(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null && _update is not null)
            _vm.OpenGitHubReleaseUrl(_update.HtmlUrl);
    }

    private void OnSkipVersion(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null && _update is not null)
            _vm.SkipUpdateVersion(_update.LatestVersion);
        Close();
    }

    private void OnLater(object? sender, RoutedEventArgs e) => Close();
}
