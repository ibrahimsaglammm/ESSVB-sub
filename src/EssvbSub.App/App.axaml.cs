// Copyright (c) 2026 ESSVB. All Rights Reserved.

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EssvbSub.App.ViewModels;
using EssvbSub.App.Views;

namespace EssvbSub.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            var mainWin = new MainWindow { DataContext = vm };
            desktop.MainWindow = mainWin;
            desktop.ShutdownRequested += (_, _) => vm.Dispose();
            desktop.Exit += (_, _) => vm.Dispose();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => vm.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
