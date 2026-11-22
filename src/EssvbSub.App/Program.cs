// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Text;
using Avalonia;
using EssvbSub.Capture.Ocr;
using EssvbSub.Core;
using EssvbSub.Core.Pipeline;

namespace EssvbSub.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--self-check", StringComparison.OrdinalIgnoreCase)))
            return RunSelfCheck();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static int RunSelfCheck()
    {
        bool singleFile = AppInfo.IsSingleFileBundle;
        bool rustAvailable = RustFastAccel.IsAvailable;
        bool tvTizenFound =
            File.Exists(Path.Combine(AppContext.BaseDirectory, "tv-tizen", "config.xml")) ||
            File.Exists(Path.Combine(AppInfo.ExecutableDirectory, "tv-tizen", "config.xml"));

        bool ocrReady = true;
        if (OperatingSystem.IsWindows())
        {
            using var reader = WinRtOcrReader.TryCreate("en");
            ocrReady = reader is not null;
        }

        bool helpAssetsFound = CheckEmbeddedHelpAssets();

        Console.Out.WriteLine($"Version={AppInfo.FullVersionLabel}");
        Console.Out.WriteLine($"SingleFile={singleFile}");
        Console.Out.WriteLine($"RustAvailable={rustAvailable}");
        Console.Out.WriteLine($"TvTizenFound={tvTizenFound}");
        Console.Out.WriteLine($"OcrReady={ocrReady}");
        Console.Out.WriteLine($"HelpAssetsFound={helpAssetsFound}");
        Console.Out.Flush();

        return rustAvailable && tvTizenFound && ocrReady && helpAssetsFound ? 0 : 2;
    }

    private static bool CheckEmbeddedHelpAssets()
    {
        try
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream("!AvaloniaResources");
            if (stream is null)
                return false;

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            string text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            string[] required =
            [
                "01-pc-kurulum.png",
                "02-samsung-tv-kurulum.png",
                "03-playstation-kurulum.png",
                "04-model-ve-mod-secimi.png",
                "05-altyazi-penceresi-ve-kullanim.png"
            ];
            return required.All(name => text.Contains(name, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }
}
