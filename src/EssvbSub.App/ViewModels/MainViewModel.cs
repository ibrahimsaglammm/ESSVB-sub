// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using EssvbSub.App.Services;
using EssvbSub.Capture.Desktop;
using EssvbSub.Capture.Ocr;
using EssvbSub.Capture.PlayStation;
using EssvbSub.Capture.TvInstall;
using EssvbSub.Core;
using EssvbSub.Core.Models;
using EssvbSub.Core.Pipeline;
using EssvbSub.Core.Server;
using EssvbSub.Core.Storage;
using EssvbSub.Core.Updates;
using EssvbSub.Translation;
using EssvbSub.Translation.Providers;

namespace EssvbSub.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly EssvbSettings _settings;
    private readonly SecureSubtitleServer _server;
    private readonly FrameGate _frameGate = new();
    private readonly LocalMarianTranslator _offlineFallback = new();

    private SubtitlePipeline? _pipeline;
    private ITextReader? _ocrReader;
    private CrossPlatformWindowCapture? _windowCapture;
    private PsRemotePlaySession? _psSession;
    private AudioLoopbackCapture? _audioLoopback;
    private PairingTicket? _pendingTvTicket;

    private bool _isRunning;
    private bool _isBusy;
    private string _statusMessage = "Hazır";
    private string _metricsMessage = "";
    private string _lastSourceText = "";
    private string _lastTranslatedText = "";
    private string _apiKeyInput = "";
    private string _psnRedirectInput = "";
    private string _psPinInput = "";
    private string _aiTestResult = "";
    private int _connectedTvCount;
    private WindowTarget? _selectedWindow;
    private (int X, int Y, int Width, int Height)? _customScreenRect;
    private DiscoveredModel? _selectedModelItem;
    private bool _useCustomModelEntry;
    private string _modelDiscoveryStatus = "";
    private bool _syncingModels;
    private bool _isOverlayEditMode;
    private bool _isHelpTabActive;
    private HelpGuideItem _selectedHelpGuide;
    private readonly Dictionary<string, Bitmap> _helpBitmapCache = [];
    private string _activeQuotaAlert = "";
    private string? _cachedLocalOllamaModel;
    private Frame? _pendingSkippedFrame;
    private AppUpdateInfo? _availableUpdate;
    private bool _updateBannerDismissed;
    private string _updateCheckStatusText = "Başlangıçta GitHub kontrolü bekliyor.";
    private bool _isCheckingUpdates;
    private bool _isInstallingUpdate;
    private double _updateProgressPercent;
    private string _updateProgressText = "";
    private bool _isTvInstallBusy;
    private string _tvInstalledBadgeText = "Durum Bekleniyor";
    private string _tvInstallStatusText = "TV durumu henüz kontrol edilmedi — 'TV Ara' veya 'Durumu Kontrol Et'e basın.";
    private TvInstallVisualState _tvVisualState = TvInstallVisualState.Neutral;

    private enum TvInstallVisualState { Neutral, Busy, Success, Warning, Error }

    private static readonly IBrush TvBgNeutral = new SolidColorBrush(Color.Parse("#0D121B"));
    private static readonly IBrush TvBorderNeutral = new SolidColorBrush(Color.Parse("#252C3B"));
    private static readonly IBrush TvFgNeutral = new SolidColorBrush(Color.Parse("#9CA3AF"));

    private static readonly IBrush TvBgBusy = new SolidColorBrush(Color.Parse("#111E2E"));
    private static readonly IBrush TvBorderBusy = new SolidColorBrush(Color.Parse("#38BDF8"));
    private static readonly IBrush TvFgBusy = new SolidColorBrush(Color.Parse("#38BDF8"));

    private static readonly IBrush TvBgSuccess = new SolidColorBrush(Color.Parse("#0E241C"));
    private static readonly IBrush TvBorderSuccess = new SolidColorBrush(Color.Parse("#10B981"));
    private static readonly IBrush TvFgSuccess = new SolidColorBrush(Color.Parse("#34D399"));

    private static readonly IBrush TvBgWarning = new SolidColorBrush(Color.Parse("#261E0D"));
    private static readonly IBrush TvBorderWarning = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush TvFgWarning = new SolidColorBrush(Color.Parse("#FBBF24"));

    private static readonly IBrush TvBgError = new SolidColorBrush(Color.Parse("#261016"));
    private static readonly IBrush TvBorderError = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush TvFgError = new SolidColorBrush(Color.Parse("#FF6B7A"));

    public MainViewModel()
    {
        _selectedHelpGuide = HelpGuides[0];
        _settings = SettingsStore.Load();
        _apiKeyInput = SettingsStore.GetApiKey(_settings.Provider);
        if (_settings.UseScreenRegion && _settings.ScreenWidth >= 32 && _settings.ScreenHeight >= 20)
            _customScreenRect = (_settings.ScreenX, _settings.ScreenY, _settings.ScreenWidth, _settings.ScreenHeight);

        string token = SettingsStore.GetOrCreateServerToken();
        string instanceId = SettingsStore.GetOrCreateInstanceId();
        _server = new SecureSubtitleServer(SecureSubtitleServer.DefaultPort, token, instanceId);
        _server.TvClientsChanged += count => Dispatcher.UIThread.Post(() =>
        {
            ConnectedTvCount = count;
            OnPropertyChanged(nameof(TvConnectionSummary));
        });
        _server.PairingRequested += ticket => Dispatcher.UIThread.Post(() =>
        {
            PendingTvTicket = ticket;
            AppendLog($"TV eşleştirme isteği: PIN {ticket.PinCode} ({ticket.ClientAddress})");
        });
        _server.EventBroadcast += ev => Dispatcher.UIThread.Post(() => OverlayEventReceived?.Invoke(ev));

        if (RustFastAccel.IsAvailable)
            AppendLog("Hızlandırma: Yerel Rust motoru aktif (essvb_fast).");

        _ = StartServerSafeAsync();
        RefreshWindows();
        _ = RefreshModelsAsync();
        _ = ProbeLocalOllamaModelAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<SubtitleEvent>? OverlayEventReceived;
    public event Action<SubtitleStyle>? OverlayStyleChanged;
    public event Action<bool>? OverlayVisibilityChanged;
    public event Action<bool>? OverlayClickThroughChanged;
    public event Action<bool>? OverlayEditModeChanged;
    public event Action<double, double, double, double, bool>? OverlayBoundsChanged;
    public event Func<Task<(CropRegion Region, int X, int Y, int Width, int Height)?>>? SelectRegionRequested;
    public event Action<AppUpdateInfo>? UpdatePromptRequested;
    public event Action? AppShutdownRequested;

    public ObservableCollection<WindowTarget> AvailableWindows { get; } = [];
    public ObservableCollection<DiscoveredModel> AvailableModels { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public IReadOnlyList<AiProviderDescriptor> Providers => AiProviderCatalog.All;
    public IReadOnlyList<SourceLanguageOption> SourceLanguages => SourceLanguageCatalog.All;
    public IReadOnlyList<GameGenrePreset> GenrePresets => GameGenreCatalog.All;
    public IReadOnlyList<ConsoleKind> ConsoleTypes { get; } = [ConsoleKind.PlayStation5, ConsoleKind.PlayStation4Pro];
    public IReadOnlyList<SamsungTvModel> TvModels { get; } =
    [
        SamsungTvModel.AutoDetect,
        SamsungTvModel.Cu8500_65,
        SamsungTvModel.Mu9000,
        SamsungTvModel.ModernTizen,
        SamsungTvModel.LegacyTizen24
    ];
    public IReadOnlyList<OutputTarget> OutputTargets { get; } = [OutputTarget.SamsungTv, OutputTarget.PcOverlay, OutputTarget.Both];
    public IReadOnlyList<int> HdmiPorts { get; } = [1, 2, 3, 4];
    public IReadOnlyList<SubtitleTextColor> TextColors { get; } = [SubtitleTextColor.White, SubtitleTextColor.Yellow, SubtitleTextColor.Cyan];
    public IReadOnlyList<SubtitleBackgroundShape> BackgroundShapes { get; } = [SubtitleBackgroundShape.Fit, SubtitleBackgroundShape.Band];

    public OperatingMode Mode
    {
        get => _settings.Mode;
        set
        {
            if (_settings.Mode == value) return;
            _settings.Mode = value;
            if (value == OperatingMode.PcGame)
                _settings.Output = OutputTarget.PcOverlay;
            else if (value == OperatingMode.MediaStream && ConnectedTvCount == 0 && _settings.Output == OutputTarget.SamsungTv)
                _settings.Output = OutputTarget.PcOverlay;
            else if (value == OperatingMode.PlayStation && _settings.Output == OutputTarget.PcOverlay)
                _settings.Output = OutputTarget.SamsungTv;

            _server.SendsToTv = _settings.Output != OutputTarget.PcOverlay;
            SaveAndNotify(
                nameof(Mode),
                nameof(IsPlayStationMode), nameof(IsPcGameMode), nameof(IsMediaStreamMode),
                nameof(IsPlayStationTabActive), nameof(IsPcGameTabActive), nameof(IsMediaStreamTabActive),
                nameof(Output));
        }
    }

    public bool IsPlayStationMode => Mode == OperatingMode.PlayStation;
    public bool IsPcGameMode => Mode == OperatingMode.PcGame;
    public bool IsMediaStreamMode => Mode == OperatingMode.MediaStream;

    public bool IsPlayStationTabActive => !IsHelpTabActive && IsPlayStationMode;
    public bool IsPcGameTabActive => !IsHelpTabActive && IsPcGameMode;
    public bool IsMediaStreamTabActive => !IsHelpTabActive && IsMediaStreamMode;
    public bool IsWorkspaceVisible => !IsHelpTabActive;

    public bool IsHelpTabActive
    {
        get => _isHelpTabActive;
        set
        {
            if (_isHelpTabActive == value) return;
            _isHelpTabActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWorkspaceVisible));
            OnPropertyChanged(nameof(IsPlayStationTabActive));
            OnPropertyChanged(nameof(IsPcGameTabActive));
            OnPropertyChanged(nameof(IsMediaStreamTabActive));
        }
    }

    public IReadOnlyList<HelpGuideItem> HelpGuides { get; } =
    [
        new(1, "1. PC Kurulum", "Adım 1: Bilgisayara Kurulum (Windows 11 & CachyOS)",
            "GitHub'dan tek tıkla Start-ESSVB-sub.bat kurulumu, Masaüstü kısayolu, Windows 11 yerleşik OCR ve CachyOS komutu.",
            "01-pc-kurulum.png"),
        new(2, "2. Samsung TV", "Adım 2: Samsung 65\" CU8500 & MU9000 Otomatik TV Kurulumu",
            "Tizen Studio gerekmeden TV'de 1-2-3-4-5 Developer Mode açma, TV Ara ile bulma ve tek tıkla TV'ye kurup eşleştirme.",
            "02-samsung-tv-kurulum.png"),
        new(3, "3. PS5 / PS4 Pro", "Adım 3: PlayStation 5 & PlayStation 4 Pro Bağlantısı",
            "HDMI kablosu TV'ye bağlıyken DualSense/DualShock ile sıfır gecikmeli oynama; PS Remote Play pencere yakalama ve 2. PSN eşleme.",
            "03-playstation-kurulum.png"),
        new(4, "4. Mod & Model", "Adım 4: Mod ve Çeviri Modeli Seçimi (Yerel & F/P Yüksek AI)",
            "Ollama gemma3:4b-it-q8_0 / qwen3 yerel tarayıcı ve Google Gemini gemini-3.5-flash-lite ★ F/P seçimi, Grok, ChatGPT, Claude rehberi.",
            "04-model-ve-mod-secimi.png"),
        new(5, "5. Altyazı & Kullanım", "Adım 5: Altyazı Penceresi, Karartı (%0–%100), Konuşmacılar & Kullanım",
            "Altyazı kutusunu sürükleyip boyutlandırma, arka plan karartısını %0'a indirme, konuşmacı renk ayrımı ve Rust hızlandırma.",
            "05-altyazi-penceresi-ve-kullanim.png")
    ];

    public HelpGuideItem SelectedHelpGuide
    {
        get => _selectedHelpGuide;
        set
        {
            if (value is null || ReferenceEquals(_selectedHelpGuide, value)) return;
            _selectedHelpGuide = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedHelpGuideBitmap));
            OnPropertyChanged(nameof(CanGoPreviousHelpGuide));
            OnPropertyChanged(nameof(CanGoNextHelpGuide));
        }
    }

    public bool CanGoPreviousHelpGuide => SelectedHelpGuide.Index > 1;
    public bool CanGoNextHelpGuide => SelectedHelpGuide.Index < HelpGuides.Count;

    public Bitmap? SelectedHelpGuideBitmap
    {
        get
        {
            string fileName = SelectedHelpGuide.FileName;
            if (_helpBitmapCache.TryGetValue(fileName, out var cached))
                return cached;

            try
            {
                using Stream? resStream = AssetLoader.Open(new Uri(SelectedHelpGuide.ResourceUri));
                var bmp = new Bitmap(resStream);
                _helpBitmapCache[fileName] = bmp;
                return bmp;
            }
            catch
            {
                try
                {
                    string diskPath = Path.Combine(AppContext.BaseDirectory, "docs", "images", fileName);
                    if (File.Exists(diskPath))
                    {
                        var bmp = new Bitmap(diskPath);
                        _helpBitmapCache[fileName] = bmp;
                        return bmp;
                    }
                }
                catch { }
                return null;
            }
        }
    }

    public string WindowTitle => $"ESSVB-sub {AppInfo.VersionBadge} — Canlı Altyazı Stüdyosu";
    public string AppVersionBadge => AppInfo.VersionBadge;
    public string AppFullVersion => AppInfo.FullVersionLabel;
    public string AppBuildDate => AppInfo.BuildDateLocal;
    public string AppRuntimeSummary => AppInfo.RuntimeSummary;
    public string RustEngineSummary => AppInfo.RustEngineSummary;
    public bool IsRustEngineActive => RustFastAccel.IsAvailable;
    public string AppExecutablePath => AppInfo.ExecutablePath;
    public string AppConfigDirectory => AppInfo.ConfigDirectory;
    public string AppLicenseSummary => AppInfo.LicenseSummary;
    public string GitHubRepoUrl => AppInfo.GitHubRepoUrl;

    public bool AutoCheckUpdates
    {
        get => _settings.AutoCheckUpdates;
        set
        {
            if (_settings.AutoCheckUpdates == value) return;
            _settings.AutoCheckUpdates = value;
            SaveAndNotify();
        }
    }

    public AppUpdateInfo? AvailableUpdate
    {
        get => _availableUpdate;
        private set
        {
            if (Equals(_availableUpdate, value)) return;
            _availableUpdate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAvailableUpdate));
            OnPropertyChanged(nameof(IsUpdateBannerVisible));
            OnPropertyChanged(nameof(UpdateBannerText));
        }
    }

    public bool HasAvailableUpdate => _availableUpdate is not null;
    public bool IsUpdateBannerVisible => _availableUpdate is not null && !_updateBannerDismissed;
    public string UpdateBannerText => _availableUpdate is { } u
        ? $"GitHub'da yeni sürüm mevcut: v{u.LatestVersion} (Mevcut: v{u.CurrentVersion}) — Kurmak için izniniz bekleniyor."
        : "";

    public string UpdateCheckStatusText
    {
        get => _updateCheckStatusText;
        private set { if (_updateCheckStatusText != value) { _updateCheckStatusText = value; OnPropertyChanged(); } }
    }

    public bool IsCheckingUpdates
    {
        get => _isCheckingUpdates;
        private set { if (_isCheckingUpdates != value) { _isCheckingUpdates = value; OnPropertyChanged(); } }
    }

    public bool IsTvInstallBusy
    {
        get => _isTvInstallBusy;
        private set { if (_isTvInstallBusy != value) { _isTvInstallBusy = value; OnPropertyChanged(); } }
    }

    public string TvInstalledBadgeText
    {
        get => _tvInstalledBadgeText;
        private set { if (_tvInstalledBadgeText != value) { _tvInstalledBadgeText = value; OnPropertyChanged(); } }
    }

    public string TvInstallStatusText
    {
        get => _tvInstallStatusText;
        private set { if (_tvInstallStatusText != value) { _tvInstallStatusText = value; OnPropertyChanged(); } }
    }

    public IBrush TvStatusBackground => _tvVisualState switch
    {
        TvInstallVisualState.Busy => TvBgBusy,
        TvInstallVisualState.Success => TvBgSuccess,
        TvInstallVisualState.Warning => TvBgWarning,
        TvInstallVisualState.Error => TvBgError,
        _ => TvBgNeutral
    };

    public IBrush TvStatusBorderBrush => _tvVisualState switch
    {
        TvInstallVisualState.Busy => TvBorderBusy,
        TvInstallVisualState.Success => TvBorderSuccess,
        TvInstallVisualState.Warning => TvBorderWarning,
        TvInstallVisualState.Error => TvBorderError,
        _ => TvBorderNeutral
    };

    public IBrush TvStatusForeground => _tvVisualState switch
    {
        TvInstallVisualState.Busy => TvFgBusy,
        TvInstallVisualState.Success => TvFgSuccess,
        TvInstallVisualState.Warning => TvFgWarning,
        TvInstallVisualState.Error => TvFgError,
        _ => TvFgNeutral
    };

    private void SetTvInstallFeedback(TvInstallVisualState state, string badge, string message)
    {
        _tvVisualState = state;
        TvInstalledBadgeText = badge;
        TvInstallStatusText = message;
        OnPropertyChanged(nameof(TvStatusBackground));
        OnPropertyChanged(nameof(TvStatusBorderBrush));
        OnPropertyChanged(nameof(TvStatusForeground));
    }

    public bool IsInstallingUpdate
    {
        get => _isInstallingUpdate;
        private set { if (_isInstallingUpdate != value) { _isInstallingUpdate = value; OnPropertyChanged(); } }
    }

    public double UpdateProgressPercent
    {
        get => _updateProgressPercent;
        private set { if (Math.Abs(_updateProgressPercent - value) > 0.05) { _updateProgressPercent = value; OnPropertyChanged(); } }
    }

    public string UpdateProgressText
    {
        get => _updateProgressText;
        private set { if (_updateProgressText != value) { _updateProgressText = value; OnPropertyChanged(); } }
    }

    public void SelectHelpGuideByIndex(int zeroBasedIndex)
    {
        if (zeroBasedIndex >= 0 && zeroBasedIndex < HelpGuides.Count)
            SelectedHelpGuide = HelpGuides[zeroBasedIndex];
    }

    public void PreviousHelpGuide()
    {
        if (SelectedHelpGuide.Index > 1)
            SelectedHelpGuide = HelpGuides[SelectedHelpGuide.Index - 2];
    }

    public void NextHelpGuide()
    {
        if (SelectedHelpGuide.Index < HelpGuides.Count)
            SelectedHelpGuide = HelpGuides[SelectedHelpGuide.Index];
    }

    public void OpenGitHubRepo() => TryOpenShellTarget(AppInfo.GitHubRepoUrl);
    public void OpenGitHubReleases() => TryOpenShellTarget(AppInfo.GitHubReleasesUrl);

    public void OpenGitHubReleaseUrl(string? url)
    {
        string safe = GitHubUpdateService.IsTrustedGitHubUrl(url) ? url! : AppInfo.GitHubReleasesUrl;
        TryOpenShellTarget(safe);
    }

    public void RequestUpdateConsentPrompt()
    {
        if (_availableUpdate is { } update)
            UpdatePromptRequested?.Invoke(update);
    }

    public void DismissUpdateBanner()
    {
        _updateBannerDismissed = true;
        OnPropertyChanged(nameof(IsUpdateBannerVisible));
    }

    public void SkipUpdateVersion(string version)
    {
        _settings.SkippedUpdateVersion = version?.Trim() ?? "";
        _updateBannerDismissed = true;
        SaveAndNotify(nameof(IsUpdateBannerVisible));
        UpdateCheckStatusText = $"v{_settings.SkippedUpdateVersion} sürümü atlandı (istediğiniz zaman tekrar kontrol edebilirsiniz).";
    }

    public async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (IsCheckingUpdates || IsInstallingUpdate)
            return;

        IsCheckingUpdates = true;
        if (userInitiated)
            UpdateCheckStatusText = "GitHub Releases kontrol ediliyor…";

        try
        {
            var res = await GitHubUpdateService.CheckForUpdateAsync(AppInfo.Version);
            if (res.Status == UpdateCheckStatus.UpdateAvailable && res.Update is { } update)
            {
                _updateBannerDismissed = false;
                AvailableUpdate = update;
                UpdateCheckStatusText = res.Message;
                AppendLog(res.Message);

                bool skipped = !userInitiated &&
                               string.Equals(_settings.SkippedUpdateVersion, update.LatestVersion, StringComparison.OrdinalIgnoreCase);
                if (!skipped)
                    UpdatePromptRequested?.Invoke(update);
            }
            else if (res.Status == UpdateCheckStatus.UpToDate)
            {
                AvailableUpdate = null;
                UpdateCheckStatusText = res.Message;
                if (userInitiated)
                    AppendLog(res.Message);
            }
            else
            {
                UpdateCheckStatusText = userInitiated
                    ? res.Message
                    : "Başlangıç güncelleme kontrolü tamamlanamadı (ağ veya hız sınırı).";
                AppendLog($"Güncelleme kontrolü: {res.Message}");
            }
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    public async Task<UpdateInstallResult> InstallAvailableUpdateAsync()
    {
        if (_availableUpdate is not { } update)
            return new UpdateInstallResult(false, "Kurulacak yeni sürüm bulunamadı.");

        IsInstallingUpdate = true;
        UpdateProgressPercent = 0;
        UpdateProgressText = "Hazırlanıyor…";
        try
        {
            if (IsRunning)
                StopPipeline();

            var progress = new Progress<UpdateDownloadProgress>(p => Dispatcher.UIThread.Post(() =>
            {
                UpdateProgressPercent = p.Percent;
                UpdateProgressText = p.StageText;
            }));

            var result = await GitHubUpdateService.DownloadAndInstallUpdateAsync(update, progress);
            UpdateCheckStatusText = result.Message;
            AppendLog(result.Message);

            if (result.Success && result.RequiresAppExit)
            {
                await Task.Delay(350);
                AppShutdownRequested?.Invoke();
            }

            return result;
        }
        finally
        {
            IsInstallingUpdate = false;
        }
    }

    public void OpenConfigFolder()
    {
        try { Directory.CreateDirectory(AppInfo.ConfigDirectory); } catch { }
        TryOpenShellTarget(AppInfo.ConfigDirectory);
    }

    public void OpenAppFolder() => TryOpenShellTarget(AppInfo.ExecutableDirectory);

    private void TryOpenShellTarget(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"Bağlantı/klasör açılamadı: {ex.Message}");
        }
    }

    public ConsoleKind SelectedConsoleType
    {
        get => _settings.ConsoleType;
        set
        {
            if (_settings.ConsoleType == value) return;
            _settings.ConsoleType = value;
            SaveAndNotify(nameof(SelectedConsoleType), nameof(ConsoleRegistrationStatus));
        }
    }

    public OutputTarget Output
    {
        get => _settings.Output;
        set { _settings.Output = value; _server.SendsToTv = value != OutputTarget.PcOverlay; SaveAndNotify(); }
    }

    public AiProviderDescriptor SelectedProvider
    {
        get => AiProviderCatalog.Get(_settings.Provider);
        set
        {
            if (value is null || _settings.Provider == value.Kind) return;
            _settings.Provider = value.Kind;
            _settings.Endpoint = value.DefaultEndpoint;
            _settings.Model = value.DefaultModel;
            _apiKeyInput = SettingsStore.GetApiKey(value.Kind);
            SaveAndNotify(nameof(SelectedProvider), nameof(ModelName), nameof(EndpointUrl), nameof(ApiKeyInput),
                nameof(SuggestedModels), nameof(ShowEndpointBox), nameof(ShowApiKeyBox));
            _ = RefreshModelsAsync();
        }
    }

    public IReadOnlyList<string> SuggestedModels => SelectedProvider.SuggestedModels;
    public bool ShowEndpointBox => SelectedProvider.Kind is AiProviderKind.Ollama or AiProviderKind.LmStudio or AiProviderKind.CustomOpenAi;
    public bool ShowApiKeyBox => SelectedProvider.RequiresApiKey || SelectedProvider.Kind == AiProviderKind.CustomOpenAi;

    public DiscoveredModel? SelectedModelItem
    {
        get => _selectedModelItem;
        set
        {
            if (_selectedModelItem == value) return;
            _selectedModelItem = value;
            OnPropertyChanged();
            if (!_syncingModels && value is not null)
                ModelName = value.Id;
        }
    }

    public bool UseCustomModelEntry
    {
        get => _useCustomModelEntry;
        set { if (_useCustomModelEntry != value) { _useCustomModelEntry = value; OnPropertyChanged(); } }
    }

    public string ModelDiscoveryStatus
    {
        get => _modelDiscoveryStatus;
        private set { _modelDiscoveryStatus = value; OnPropertyChanged(); }
    }

    public string ModelName
    {
        get => _settings.Model;
        set { _settings.Model = value; SaveAndNotify(); }
    }

    public string EndpointUrl
    {
        get => _settings.Endpoint;
        set { _settings.Endpoint = value; SaveAndNotify(); }
    }

    public string ApiKeyInput
    {
        get => _apiKeyInput;
        set
        {
            _apiKeyInput = value;
            SettingsStore.SaveApiKey(_settings.Provider, value);
            OnPropertyChanged();
        }
    }

    public string CustomInstruction
    {
        get => _settings.CustomInstruction;
        set
        {
            _settings.CustomInstruction = value;
            SaveAndNotify();
        }
    }

    public SourceLanguageOption SelectedSourceLanguage
    {
        get => SourceLanguageCatalog.Get(_settings.SourceLanguage);
        set
        {
            if (value is null || string.Equals(_settings.SourceLanguage, value.Code, StringComparison.OrdinalIgnoreCase))
                return;
            _settings.SourceLanguage = value.Code;
            if (_pipeline is not null) _pipeline.SourceLanguage = value.Code;
            SaveAndNotify(nameof(SelectedSourceLanguage));
        }
    }

    public GameGenrePreset SelectedGenrePreset
    {
        get => GameGenreCatalog.Get(_settings.GenrePresetId);
        set
        {
            if (value is null || string.Equals(_settings.GenrePresetId, value.Id, StringComparison.OrdinalIgnoreCase))
                return;
            _settings.GenrePresetId = value.Id;
            if (_pipeline is not null) _pipeline.GenrePresetId = value.Id;
            SaveAndNotify(nameof(SelectedGenrePreset));
        }
    }

    public bool StrictBottomCapture
    {
        get => _settings.StrictBottomCapture;
        set
        {
            if (_settings.StrictBottomCapture == value) return;
            _settings.StrictBottomCapture = value;
            if (_pipeline is not null) _pipeline.StrictBottomOnly = value;
            SaveAndNotify();
        }
    }

    public string ActiveQuotaAlert
    {
        get => _activeQuotaAlert;
        private set
        {
            string trimmed = value?.Trim() ?? "";
            if (_activeQuotaAlert == trimmed) return;
            _activeQuotaAlert = trimmed;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasActiveQuotaAlert));
        }
    }

    public bool HasActiveQuotaAlert => !string.IsNullOrWhiteSpace(_activeQuotaAlert);

    public string MediaTitle
    {
        get => _settings.MediaTitle;
        set
        {
            _settings.MediaTitle = value;
            if (_pipeline is not null) _pipeline.MediaTitle = value;
            SaveAndNotify();
        }
    }

    public bool MediaUseAudioLoopback
    {
        get => _settings.MediaUseAudioLoopback;
        set { _settings.MediaUseAudioLoopback = value; SaveAndNotify(nameof(MediaUseAudioLoopback), nameof(MediaUseOcr)); }
    }

    public bool MediaUseOcr => !MediaUseAudioLoopback;

    public bool AudioUseGemini
    {
        get => _settings.AudioUseGemini;
        set { _settings.AudioUseGemini = value; SaveAndNotify(); }
    }

    public string WhisperCliPath
    {
        get => _settings.WhisperCliPath;
        set { _settings.WhisperCliPath = value; SaveAndNotify(); }
    }

    public string WhisperModelPath
    {
        get => _settings.WhisperModelPath;
        set { _settings.WhisperModelPath = value; SaveAndNotify(); }
    }

    public string SamsungTvIp
    {
        get => _settings.SamsungTvIp;
        set { _settings.SamsungTvIp = value; SaveAndNotify(); }
    }

    public SamsungTvModel SelectedTvModel
    {
        get => _settings.TvModel;
        set { _settings.TvModel = value; SaveAndNotify(); }
    }

    public bool AutoLaunchTvApp
    {
        get => _settings.AutoLaunchTvApp;
        set { _settings.AutoLaunchTvApp = value; SaveAndNotify(); }
    }

    public int HdmiPort
    {
        get => _settings.HdmiPort;
        set { _settings.HdmiPort = value; SaveAndNotify(); }
    }

    public string PlayStationIp
    {
        get => _settings.PlayStationIp;
        set { _settings.PlayStationIp = value; SaveAndNotify(); }
    }

    public string PsnRedirectInput
    {
        get => _psnRedirectInput;
        set { _psnRedirectInput = value; OnPropertyChanged(); }
    }

    public string PsPinInput
    {
        get => _psPinInput;
        set { _psPinInput = value; OnPropertyChanged(); }
    }

    public WindowTarget? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            _selectedWindow = value;
            if (value is not null && !_syncingModels)
            {
                _customScreenRect = null;
                _settings.UseScreenRegion = false;
                SettingsStore.Save(_settings);
                OnPropertyChanged(nameof(RegionSummary));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(CaptureSourceSummary));
        }
    }

    public SubtitleStyle CurrentStyle => _settings.Style;
    public double OverlayLeft => _settings.OverlayLeft;
    public double OverlayTop => _settings.OverlayTop;
    public bool HasSavedOverlayPosition => _settings.HasSavedOverlayPosition;

    public bool IsOverlayEditMode
    {
        get => _isOverlayEditMode;
        set
        {
            if (_isOverlayEditMode == value) return;
            _isOverlayEditMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OverlayEditButtonLabel));
            OverlayEditModeChanged?.Invoke(value);
        }
    }

    public string OverlayEditButtonLabel => IsOverlayEditMode
        ? "Düzenlemeyi Kilitle (Tamam)"
        : "Altyazı Penceresini Taşı / Boyutlandır";

    public bool ClickThroughOverlay
    {
        get => _settings.ClickThroughOverlay;
        set
        {
            _settings.ClickThroughOverlay = value;
            SaveAndNotify();
            OverlayClickThroughChanged?.Invoke(value);
        }
    }

    public int FontSize
    {
        get => _settings.Style.FontSize;
        set
        {
            _settings.Style = _settings.Style with { FontSize = Math.Clamp(value, 24, 80) };
            _server.SendStyle(_settings.Style);
            OverlayStyleChanged?.Invoke(_settings.Style);
            SaveAndNotify();
        }
    }

    public int BackgroundOpacity
    {
        get => _settings.Style.BackgroundOpacity;
        set
        {
            _settings.Style = _settings.Style with { BackgroundOpacity = Math.Clamp(value, 0, 100) };
            _server.SendStyle(_settings.Style);
            OverlayStyleChanged?.Invoke(_settings.Style);
            SaveAndNotify(nameof(BackgroundOpacity), nameof(BackgroundOpacityLabel));
        }
    }

    public string BackgroundOpacityLabel => _settings.Style.BackgroundOpacity == 0
        ? "%0 (Karartı Yok)"
        : $"%{_settings.Style.BackgroundOpacity}";

    public SubtitleBackgroundShape BackgroundShape
    {
        get => _settings.Style.BackgroundShape;
        set
        {
            _settings.Style = _settings.Style with { BackgroundShape = value };
            _server.SendStyle(_settings.Style);
            OverlayStyleChanged?.Invoke(_settings.Style);
            SaveAndNotify();
        }
    }

    public bool IsBoldText
    {
        get => _settings.Style.Bold;
        set
        {
            _settings.Style = _settings.Style with { Bold = value };
            _server.SendStyle(_settings.Style);
            OverlayStyleChanged?.Invoke(_settings.Style);
            SaveAndNotify();
        }
    }

    public double OverlayWidth
    {
        get => _settings.OverlayWidth;
        set
        {
            double clamped = Math.Clamp(Math.Round(value), 420, 2200);
            if (Math.Abs(_settings.OverlayWidth - clamped) < 0.5) return;
            _settings.OverlayWidth = clamped;
            SaveAndNotify(nameof(OverlayWidth), nameof(OverlaySizeLabel));
            OverlayBoundsChanged?.Invoke(_settings.OverlayLeft, _settings.OverlayTop, _settings.OverlayWidth, _settings.OverlayHeight, _settings.HasSavedOverlayPosition);
        }
    }

    public double OverlayHeight
    {
        get => _settings.OverlayHeight;
        set
        {
            double clamped = Math.Clamp(Math.Round(value), 100, 540);
            if (Math.Abs(_settings.OverlayHeight - clamped) < 0.5) return;
            _settings.OverlayHeight = clamped;
            SaveAndNotify(nameof(OverlayHeight), nameof(OverlaySizeLabel));
            OverlayBoundsChanged?.Invoke(_settings.OverlayLeft, _settings.OverlayTop, _settings.OverlayWidth, _settings.OverlayHeight, _settings.HasSavedOverlayPosition);
        }
    }

    public string OverlaySizeLabel => $"{(int)_settings.OverlayWidth} × {(int)_settings.OverlayHeight} px";

    public void SyncOverlayBoundsFromUser(double left, double top, double width, double height)
    {
        _settings.OverlayLeft = left;
        _settings.OverlayTop = top;
        _settings.OverlayWidth = Math.Clamp(Math.Round(width), 420, 2200);
        _settings.OverlayHeight = Math.Clamp(Math.Round(height), 100, 540);
        _settings.HasSavedOverlayPosition = true;
        SettingsStore.Save(_settings);
        OnPropertyChanged(nameof(OverlayWidth));
        OnPropertyChanged(nameof(OverlayHeight));
        OnPropertyChanged(nameof(OverlaySizeLabel));
    }

    public void ResetOverlayBounds()
    {
        _settings.OverlayWidth = 1100;
        _settings.OverlayHeight = 240;
        _settings.HasSavedOverlayPosition = false;
        SaveAndNotify(nameof(OverlayWidth), nameof(OverlayHeight), nameof(OverlaySizeLabel));
        OverlayBoundsChanged?.Invoke(_settings.OverlayLeft, _settings.OverlayTop, _settings.OverlayWidth, _settings.OverlayHeight, false);
        AppendLog("Altyazı penceresi konumu ve boyutu varsayılana sıfırlandı.");
    }

    public void ShowOverlayPreview()
    {
        OverlayVisibilityChanged?.Invoke(true);
        _server.SendStyle(_settings.Style);
        _server.SendSubtitle("ATREUS: Bak şuraya, biri yaklaşıyor!", "ATREUS: Look over there, someone is coming!", durationMs: 4500);
        _server.SendSubtitle("KRATOS: Yanımda kal. Yolculuğumuz yeni başlıyor.", "KRATOS: Stay close. Our journey has just begun.", durationMs: 4500);
    }

    public SubtitleTextColor TextColor
    {
        get => _settings.Style.TextColor;
        set
        {
            _settings.Style = _settings.Style with { TextColor = value };
            _server.SendStyle(_settings.Style);
            OverlayStyleChanged?.Invoke(_settings.Style);
            SaveAndNotify();
        }
    }

    public bool StackPrevious
    {
        get => _settings.Style.StackPrevious;
        set
        {
            _settings.Style = _settings.Style with { StackPrevious = value };
            _server.SendStyle(_settings.Style);
            OverlayStyleChanged?.Invoke(_settings.Style);
            SaveAndNotify();
        }
    }

    public bool SpeakerColors
    {
        get => _settings.Style.SpeakerColors;
        set
        {
            _settings.Style = _settings.Style with { SpeakerColors = value };
            _server.SendStyle(_settings.Style);
            OverlayStyleChanged?.Invoke(_settings.Style);
            SaveAndNotify();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StartStopLabel));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); }
    }

    public string StartStopLabel => IsRunning ? "Çeviriyi Durdur" : "Çeviriyi Başlat";
    public string StatusMessage { get => _statusMessage; private set { _statusMessage = value; OnPropertyChanged(); } }
    public string MetricsMessage { get => _metricsMessage; private set { _metricsMessage = value; OnPropertyChanged(); } }
    public string LastSourceText { get => _lastSourceText; private set { _lastSourceText = value; OnPropertyChanged(); } }
    public string LastTranslatedText { get => _lastTranslatedText; private set { _lastTranslatedText = value; OnPropertyChanged(); } }
    public string AiTestResult { get => _aiTestResult; private set { _aiTestResult = value; OnPropertyChanged(); } }
    public int ConnectedTvCount { get => _connectedTvCount; private set { _connectedTvCount = value; OnPropertyChanged(); } }

    public PairingTicket? PendingTvTicket
    {
        get => _pendingTvTicket;
        private set { _pendingTvTicket = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPendingTvTicket)); }
    }
    public bool HasPendingTvTicket => _pendingTvTicket is not null;

    public string PsnAccountStatus => SettingsStore.LoadPsnAccountId() is not null ? "PSN Hesabı Hazır" : "PSN Hesabı Bağlı Değil";
    public string ConsoleRegistrationStatus => SettingsStore.LoadRegisteredConsole(SelectedConsoleType) is { } reg
        ? $"Eşleşmiş: {reg.Name} ({reg.Host})"
        : (PsRemotePlaySession.IsNativeAvailable ? "Eşleşme bekleniyor" : "Yerel libchiaki yok — Pencere modu aktif");
    public string TvConnectionSummary => ConnectedTvCount > 0 ? $"Samsung TV Bağlı ({ConnectedTvCount})" : $"TV Bekleniyor (Port {_server.ListeningPort})";
    public string ObsUrl => $"http://127.0.0.1:{_server.ListeningPort}/obs?k={_server.AccessToken}";
    public string RegionSummary => $"Alan: Sol {_settings.ScanRegion.Left:P0}–{_settings.ScanRegion.Right:P0}, Üst {_settings.ScanRegion.Top:P0}–{_settings.ScanRegion.Bottom:P0}";
    public string CaptureSourceSummary => _customScreenRect is { } r
        ? $"Aktif Kaynak: Özel Ekran Bölgesi ({r.Width}x{r.Height} @ {r.X},{r.Y})"
        : SelectedWindow is { } w
            ? $"Aktif Kaynak: Pencere — {w.Title} ({w.ProcessName})"
            : "Aktif Kaynak: Seçilmedi (Pencere veya Ekran Bölgesi seçin)";

    private async Task StartServerSafeAsync()
    {
        try
        {
            await _server.StartAsync();
            OnPropertyChanged(nameof(TvConnectionSummary));
            OnPropertyChanged(nameof(ObsUrl));
            AppendLog($"Güvenli yerel altyazı sunucusu başlatıldı (Port {_server.ListeningPort}).");
        }
        catch (Exception ex)
        {
            AppendLog($"Sunucu başlatılamadı: {ex.Message}");
        }
    }

    public void RefreshWindows()
    {
        AvailableWindows.Clear();
        foreach (var w in CrossPlatformWindowCapture.ListWindows())
            AvailableWindows.Add(w);

        if (SelectedWindow is null && _customScreenRect is null && AvailableWindows.Count > 0)
        {
            _syncingModels = true;
            try { SelectedWindow = AvailableWindows[0]; }
            finally { _syncingModels = false; }
        }
        OnPropertyChanged(nameof(CaptureSourceSummary));
    }

    public async Task RefreshModelsAsync(bool autoStartOllama = false)
    {
        var provider = _settings.Provider;
        string endpoint = EndpointUrl;
        string apiKey = SettingsStore.GetApiKey(provider);

        if (autoStartOllama && provider == AiProviderKind.Ollama)
        {
            var ensured = await EnsureOllamaReadyForActionAsync(refreshModelsIfStarted: false);
            if (!ensured.IsReady && ensured.Status != OllamaEnsureStatus.NotLocalEndpoint)
            {
                ModelDiscoveryStatus = ensured.Message;
                return;
            }
        }

        ModelDiscoveryStatus = "Modeller taranıyor…";
        var res = await ModelDiscoveryService.DiscoverModelsAsync(provider, endpoint, apiKey);

        _syncingModels = true;
        try
        {
            AvailableModels.Clear();
            foreach (var m in res.Models)
                AvailableModels.Add(m);

            ModelDiscoveryStatus = res.StatusText;

            if (AvailableModels.Count > 0)
            {
                DiscoveredModel? match =
                    AvailableModels.FirstOrDefault(m => string.Equals(m.Id, ModelName, StringComparison.OrdinalIgnoreCase))
                    ?? AvailableModels.FirstOrDefault(m => string.Equals(StripLatestTag(m.Id), StripLatestTag(ModelName), StringComparison.OrdinalIgnoreCase));

                if (match is null && (res.LiveQueried || provider == AiProviderKind.Gemini))
                {
                    match = AvailableModels[0];
                    ModelName = match.Id;
                    AppendLog($"Model otomatik seçildi: {match.Id}");
                }
                else if (match is not null && !string.Equals(match.Id, ModelName, StringComparison.Ordinal))
                {
                    ModelName = match.Id;
                }

                SelectedModelItem = match;
            }
            else
            {
                SelectedModelItem = null;
            }
        }
        finally
        {
            _syncingModels = false;
        }
    }

    private static string StripLatestTag(string id) =>
        id.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? id[..^":latest".Length] : id;

    public async Task SelectScreenRegionAsync()
    {
        if (SelectRegionRequested is null) return;
        var res = await SelectRegionRequested.Invoke();
        if (res is null) return;

        _settings.ScanRegion = res.Value.Region.Clamp();
        _settings.UseScreenRegion = true;
        _settings.ScreenX = res.Value.X;
        _settings.ScreenY = res.Value.Y;
        _settings.ScreenWidth = res.Value.Width;
        _settings.ScreenHeight = res.Value.Height;
        _customScreenRect = (res.Value.X, res.Value.Y, res.Value.Width, res.Value.Height);
        SaveAndNotify(nameof(RegionSummary), nameof(CaptureSourceSummary));
        AppendLog($"Ekran bölgesi güncellendi: {res.Value.Width}x{res.Value.Height} @ ({res.Value.X},{res.Value.Y})");
    }

    public void ClearScreenRegion()
    {
        _customScreenRect = null;
        _settings.UseScreenRegion = false;
        _settings.ScanRegion = CropRegion.Default;
        SaveAndNotify(nameof(RegionSummary), nameof(CaptureSourceSummary));
        AppendLog("Özel ekran bölgesi temizlendi; pencere yakalama kullanılacak.");
    }

    public void OpenPsnLoginPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(PsnAuthHelper.BuildAuthorizeUrl().AbsoluteUri) { UseShellExecute = true });
            AppendLog("Sony PSN giriş sayfası tarayıcıda açıldı. Giriş sonrası yönlendirme adresini yapıştırın.");
        }
        catch (Exception ex)
        {
            AppendLog($"Tarayıcı açılamadı: {ex.Message}");
        }
    }

    public async Task CompletePsnLoginAsync()
    {
        if (string.IsNullOrWhiteSpace(PsnRedirectInput)) return;
        IsBusy = true;
        try
        {
            byte[] accountId = await PsnAuthHelper.ExchangeRedirectForAccountIdAsync(PsnRedirectInput);
            SettingsStore.SavePsnAccountId(accountId);
            PsnRedirectInput = "";
            OnPropertyChanged(nameof(PsnAccountStatus));
            AppendLog("PSN hesap kimliği güvenli kasaya kaydedildi.");
        }
        catch (Exception ex)
        {
            AppendLog($"PSN girişi hatası: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DiscoverPlayStationAsync()
    {
        if (!PsRemotePlaySession.IsNativeAvailable)
        {
            AppendLog("essvb_rp yerel kütüphanesi bulunamadı. PC'de PS Remote Play veya chiaki-ng açıp Pencere modunu kullanabilirsiniz.");
            return;
        }

        IsBusy = true;
        try
        {
            var found = await PsRemotePlaySession.DiscoverAsync(SelectedConsoleType, string.IsNullOrWhiteSpace(PlayStationIp) ? null : PlayStationIp);
            if (found.Count == 0)
            {
                AppendLog($"{SelectedConsoleType} yerel ağda bulunamadı.");
                return;
            }
            PlayStationIp = found[0].Host;
            AppendLog($"Bulundu: {found[0].Name} ({found[0].Host}) [{(found[0].IsAwake ? "Açık" : "Dinlenmede")}]");
        }
        catch (Exception ex)
        {
            AppendLog($"Konsol arama hatası: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RegisterPlayStationAsync()
    {
        byte[]? accountId = SettingsStore.LoadPsnAccountId();
        if (accountId is null)
        {
            AppendLog("Önce PSN hesabınızı bağlayın.");
            return;
        }
        if (string.IsNullOrWhiteSpace(PlayStationIp))
        {
            AppendLog("PlayStation IP adresini girin veya 'Konsol Ara'ya basın.");
            return;
        }

        IsBusy = true;
        try
        {
            var reg = await PsRemotePlaySession.RegisterAsync(SelectedConsoleType, PlayStationIp.Trim(), accountId, PsPinInput);
            SettingsStore.SaveRegisteredConsole(reg);
            PsPinInput = "";
            OnPropertyChanged(nameof(ConsoleRegistrationStatus));
            AppendLog($"Eşleştirme tamamlandı: {reg.Name} ({reg.Host})");
        }
        catch (Exception ex)
        {
            AppendLog($"Eşleştirme hatası: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DiscoverSamsungTvAsync()
    {
        IsBusy = true;
        IsTvInstallBusy = true;
        SetTvInstallFeedback(TvInstallVisualState.Busy, "TV Aranıyor…", "Yerel ağda Samsung Tizen TV'ler taranıyor…");
        try
        {
            AppendLog("Yerel ağda Samsung Tizen TV aranıyor...");
            var tvs = await SamsungTvInstaller.DiscoverTvsOnLanAsync();
            if (tvs.Count == 0)
            {
                const string notFound = "Ağda Samsung TV bulunamadı. TV'nin açık ve bilgisayarla aynı ağda olduğundan emin olun.";
                AppendLog(notFound);
                SetTvInstallFeedback(TvInstallVisualState.Warning, "TV Bulunamadı", notFound);
                return;
            }

            var best = tvs[0];
            SamsungTvIp = best.Host;
            if (best.ModelName is { Length: > 0 } model)
            {
                if (model.Contains("CU8500", StringComparison.OrdinalIgnoreCase))
                    SelectedTvModel = SamsungTvModel.Cu8500_65;
                else if (model.Contains("MU9000", StringComparison.OrdinalIgnoreCase))
                    SelectedTvModel = SamsungTvModel.Mu9000;
            }

            string devStatus = best.DeveloperMode == "1" || best.SdbReachable
                ? "Geliştirici Modu / SDB Açık"
                : "Geliştirici Modu Kapalı (TV Ayarları > Uygulamalar > 12345 ile açın)";
            AppendLog($"TV Bulundu: {best.Name} [{best.ModelName ?? "Samsung"}] ({best.Host}) — {devStatus}");

            var snap = await SamsungTvInstaller.CheckTvStatusAsync(best.Host);
            ApplyTvStatusSnapshot(snap);
        }
        catch (Exception ex)
        {
            AppendLog($"Samsung TV arama hatası: {ex.Message}");
            SetTvInstallFeedback(TvInstallVisualState.Error, "Arama Hatası", ex.Message);
        }
        finally
        {
            IsTvInstallBusy = false;
            IsBusy = false;
        }
    }

    public async Task CheckSamsungTvStatusAsync(bool silentIfBusy = false)
    {
        if (string.IsNullOrWhiteSpace(SamsungTvIp))
        {
            if (!silentIfBusy)
                SetTvInstallFeedback(TvInstallVisualState.Warning, "IP Girilmedi", "Önce Samsung TV IP adresini girin veya 'TV Ara' butonuna basın.");
            return;
        }

        if (silentIfBusy && (IsBusy || IsTvInstallBusy))
            return;

        if (!silentIfBusy)
            IsBusy = true;
        IsTvInstallBusy = true;
        SetTvInstallFeedback(TvInstallVisualState.Busy, "Kontrol Ediliyor…", $"{SamsungTvIp.Trim()} üzerinden TV kurulum ve SDB durumu sorgulanıyor…");
        try
        {
            var snap = await SamsungTvInstaller.CheckTvStatusAsync(SamsungTvIp.Trim());
            ApplyTvStatusSnapshot(snap);
            if (!silentIfBusy)
                AppendLog(snap.Summary);
        }
        catch (Exception ex)
        {
            SetTvInstallFeedback(TvInstallVisualState.Error, "Kontrol Hatası", ex.Message);
            if (!silentIfBusy)
                AppendLog($"TV durum kontrolü hatası: {ex.Message}");
        }
        finally
        {
            IsTvInstallBusy = false;
            if (!silentIfBusy)
                IsBusy = false;
        }
    }

    private void ApplyTvStatusSnapshot(SamsungTvStatusSnapshot snap)
    {
        if (snap.SdbReachable && snap.IsAppInstalled)
        {
            string badge = string.IsNullOrWhiteSpace(snap.InstalledVersion) ? "TV'de Kurulu" : $"TV'de Kurulu (v{snap.InstalledVersion})";
            SetTvInstallFeedback(TvInstallVisualState.Success, badge, snap.Summary);
        }
        else if (snap.SdbReachable)
        {
            SetTvInstallFeedback(TvInstallVisualState.Warning, "TV'de Kurulu Değil", snap.Summary);
        }
        else
        {
            SetTvInstallFeedback(TvInstallVisualState.Error, "SDB Bağlantısı Yok", snap.Summary);
        }
    }

    public async Task InstallSamsungTvAppAsync()
    {
        if (string.IsNullOrWhiteSpace(SamsungTvIp))
        {
            const string noIp = "Samsung TV IP adresini girin veya 'TV Ara' butonuna basın.";
            AppendLog(noIp);
            SetTvInstallFeedback(TvInstallVisualState.Warning, "IP Gerekli", noIp);
            return;
        }

        if (Output == OutputTarget.PcOverlay)
        {
            Output = Mode == OperatingMode.PlayStation ? OutputTarget.SamsungTv : OutputTarget.Both;
            AppendLog($"Altyazı hedefi TV yayını için otomatik '{Output}' olarak güncellendi.");
        }

        IsBusy = true;
        IsTvInstallBusy = true;
        SetTvInstallFeedback(TvInstallVisualState.Busy, "Kuruluyor…", "TV SDB bağlantısı ve sertifika kontrolü başlatılıyor…");
        try
        {
            string tvDir = Path.Combine(AppContext.BaseDirectory, "tv-tizen");
            if (!Directory.Exists(tvDir))
                tvDir = Path.Combine(AppInfo.ExecutableDirectory, "tv-tizen");
            if (!Directory.Exists(tvDir))
                tvDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tv-tizen"));

            string host = SamsungTvIp.Trim();
            string localUrl = $"http://{GetPreferredLanIp(host)}:{_server.ListeningPort}";
            var report = await SamsungTvInstaller.InstallAndLaunchAsync(
                host,
                tvDir,
                EssvbPaths.ProfilesDir,
                openBrowser => SamsungTvInstaller.SignInSamsungAccountAsync(openBrowser, TimeSpan.FromMinutes(3)),
                localUrl,
                _server.AccessToken,
                HdmiPort,
                msg => Dispatcher.UIThread.Post(() =>
                {
                    AppendLog(msg);
                    TvInstallStatusText = msg;
                }),
                selectedModel: SelectedTvModel);

            string badge = string.IsNullOrWhiteSpace(report.InstalledVersion)
                ? "TV'de Kurulu"
                : $"TV'de Kurulu (v{report.InstalledVersion})";
            SetTvInstallFeedback(
                report.Launched ? TvInstallVisualState.Success : TvInstallVisualState.Warning,
                badge,
                report.Summary);
            AppendLog(report.Summary);
        }
        catch (Exception ex)
        {
            SetTvInstallFeedback(TvInstallVisualState.Error, "Kurulum Başarısız", ex.Message);
            AppendLog($"Samsung TV kurulum hatası: {ex.Message}");
        }
        finally
        {
            IsTvInstallBusy = false;
            IsBusy = false;
        }
    }

    public async Task LaunchSamsungTvAppAsync()
    {
        if (string.IsNullOrWhiteSpace(SamsungTvIp))
        {
            const string noIp = "Samsung TV IP adresini girin veya 'TV Ara' butonuna basın.";
            AppendLog(noIp);
            SetTvInstallFeedback(TvInstallVisualState.Warning, "IP Gerekli", noIp);
            return;
        }

        if (Output == OutputTarget.PcOverlay)
        {
            Output = Mode == OperatingMode.PlayStation ? OutputTarget.SamsungTv : OutputTarget.Both;
            AppendLog($"Altyazı hedefi TV yayını için otomatik '{Output}' olarak güncellendi.");
        }

        IsBusy = true;
        IsTvInstallBusy = true;
        SetTvInstallFeedback(TvInstallVisualState.Busy, "Başlatılıyor…", $"{SamsungTvIp.Trim()} üzerindeki ESSVB-sub uygulaması kontrol ediliyor ve başlatılıyor…");
        try
        {
            string host = SamsungTvIp.Trim();
            string localUrl = $"http://{GetPreferredLanIp(host)}:{_server.ListeningPort}";
            var (ok, message) = await SamsungTvInstaller.LaunchWithDetailAsync(host, localUrl, _server.AccessToken, HdmiPort);
            AppendLog(message);

            if (ok)
            {
                SetTvInstallFeedback(TvInstallVisualState.Success, "TV'de Açık", message);
            }
            else
            {
                var snap = await SamsungTvInstaller.CheckTvStatusAsync(host);
                string badge = snap.SdbReachable && !snap.IsAppInstalled ? "TV'de Kurulu Değil" : "Başlatılamadı";
                SetTvInstallFeedback(snap.SdbReachable && !snap.IsAppInstalled ? TvInstallVisualState.Warning : TvInstallVisualState.Error, badge, message);
            }
        }
        catch (Exception ex)
        {
            SetTvInstallFeedback(TvInstallVisualState.Error, "Başlatma Hatası", ex.Message);
            AppendLog($"TV başlatma hatası: {ex.Message}");
        }
        finally
        {
            IsTvInstallBusy = false;
            IsBusy = false;
        }
    }

    public void ApproveTvPairing(bool approve)
    {
        if (_pendingTvTicket is null) return;
        _server.ResolvePairing(_pendingTvTicket.Id, approve);
        AppendLog(approve ? "TV eşleştirmesi onaylandı." : "TV eşleştirmesi reddedildi.");
        PendingTvTicket = null;
    }

    public async Task TestTranslationConnectionAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        AiTestResult = "Test ediliyor…";
        try
        {
            if (_settings.Provider == AiProviderKind.Ollama)
            {
                var ensured = await EnsureOllamaReadyForActionAsync(refreshModelsIfStarted: true);
                if (!ensured.IsReady && ensured.Status != OllamaEnsureStatus.NotLocalEndpoint)
                {
                    AiTestResult = $"Hata: {ensured.Message}";
                    AppendLog($"Ollama bağlantı testi hatası: {ensured.Message}");
                    return;
                }
            }

            var translator = BuildTranslator();
            if (translator is UniversalAiTranslator ai)
            {
                var res = await ai.TestConnectionAsync();
                AiTestResult = res.Success ? $"Başarılı: {res.SampleTranslation}" : $"Hata: {res.Message}";
            }
            else
            {
                AiTestResult = _offlineFallback.HasCTranslateRuntime
                    ? "CTranslate2 çevrimdışı modeli hazır."
                    : "Yerleşik hızlı sözlük aktif (CTranslate2 modeli henüz eklenmedi).";
            }
        }
        catch (Exception ex)
        {
            AiTestResult = $"Hata: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ToggleStartStopAsync()
    {
        if (IsRunning)
        {
            StopPipeline();
            return;
        }

        if (IsBusy)
            return;

        if (_settings.Provider == AiProviderKind.Ollama)
        {
            IsBusy = true;
            try
            {
                var ensured = await EnsureOllamaReadyForActionAsync(refreshModelsIfStarted: true);
                if (!ensured.IsReady && ensured.Status != OllamaEnsureStatus.NotLocalEndpoint)
                {
                    StatusMessage = ensured.Message;
                    ActiveQuotaAlert = $"⚠ {ensured.Message}";
                    AppendLog($"Ollama başlatılamadı: {ensured.Message}");
                    return;
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        StartPipeline();
    }

    private async Task<OllamaEnsureResult> EnsureOllamaReadyForActionAsync(bool refreshModelsIfStarted)
    {
        var res = await OllamaProcessManager.EnsureRunningAsync(
            EndpointUrl,
            msg => Dispatcher.UIThread.Post(() =>
            {
                StatusMessage = msg;
                AiTestResult = msg;
                AppendLog(msg);
            }));

        if (res.Status == OllamaEnsureStatus.StartedSuccessfully)
            AppendLog($"Ollama CLI arka planda başlatıldı ({res.ExecutablePath ?? "ollama"}).");

        if (res.IsReady && (res.Status == OllamaEnsureStatus.StartedSuccessfully ||
                            (refreshModelsIfStarted && string.IsNullOrEmpty(_cachedLocalOllamaModel))))
        {
            if (refreshModelsIfStarted)
                await RefreshModelsAsync(autoStartOllama: false);
            await ProbeLocalOllamaModelAsync();
        }

        return res;
    }

    private void StartPipeline()
    {
        StopPipeline();
        LastSourceText = "";
        LastTranslatedText = "";
        ActiveQuotaAlert = "";
        _pendingSkippedFrame = null;
        _server.SendsToTv = Output != OutputTarget.PcOverlay;
        _server.SendStyle(_settings.Style);
        _server.SendAlert(null);

        _ocrReader = CrossPlatformOcrFactory.CreateBestAvailable(_settings.SourceLanguage);
        if (_ocrReader is WinRtOcrReader winRt &&
            !_settings.SourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            !winRt.RequestedLanguageMatched)
        {
            AppendLog($"Uyarı: Windows'ta '{_settings.SourceLanguage}' OCR dil paketi yüklü değil; '{winRt.ActiveLanguage}' ile okunuyor.");
        }

        var translator = BuildTranslator();
        var cache = TranslationCache.ForEngine(EssvbPaths.CacheDir, translator.EngineId, _settings.SourceLanguage, "tr");
        int scrubbed = cache.ScrubPoisonedEntries();
        if (scrubbed > 0)
            AppendLog($"Önbellekten {scrubbed} bozuk/çevrilmemiş İngilizce kayıt temizlendi.");

        _pipeline = new SubtitlePipeline(_ocrReader, translator, _server, new ReadScheduler(), cache)
        {
            MediaTitle = MediaTitle,
            ShowSpeakerNames = true,
            SourceLanguage = _settings.SourceLanguage,
            StrictBottomOnly = _settings.StrictBottomCapture,
            GenrePresetId = _settings.GenrePresetId,
            CacheSalt = translator is UniversalAiTranslator uai ? uai.CacheFingerprint : ""
        };
        _pipeline.OcrTextObserved += src => Dispatcher.UIThread.Post(() =>
        {
            LastSourceText = src;
            LastTranslatedText = "Çevriliyor…";
        });
        _pipeline.SubtitleTranslated += (src, tr) => Dispatcher.UIThread.Post(() =>
        {
            LastSourceText = src;
            LastTranslatedText = tr;
        });
        _pipeline.MetricsUpdated += m => Dispatcher.UIThread.Post(() =>
        {
            MetricsMessage = $"OCR {m.OcrMs} ms · Çeviri {(m.FromCache ? "0 ms (Önbellek)" : m.TranslationMs + " ms")} · Toplam {m.TotalLatencyMs} ms";
        });
        _pipeline.DiagnosticLogged += msg => Dispatcher.UIThread.Post(() => AppendLog(msg));

        if (Mode == OperatingMode.MediaStream && MediaUseAudioLoopback)
        {
            StartAudioLoopbackMode();
        }
        else if (Mode == OperatingMode.PlayStation &&
                 PsRemotePlaySession.IsNativeAvailable &&
                 SettingsStore.LoadPsnAccountId() is { } accId &&
                 SettingsStore.LoadRegisteredConsole(SelectedConsoleType) is { } regConsole)
        {
            _psSession = new PsRemotePlaySession(regConsole, accId);
            _psSession.FrameReceived += OnCapturedFrame;
            _psSession.StateChanged += st => Dispatcher.UIThread.Post(() => StatusMessage = st);
            _psSession.DiagnosticLogged += msg => Dispatcher.UIThread.Post(() => AppendLog(msg));
            _psSession.Start();
        }
        else
        {
            _windowCapture = new CrossPlatformWindowCapture(fps: 10);
            if (_customScreenRect is { } rect)
                _windowCapture.SetScreenRegion(rect.X, rect.Y, rect.Width, rect.Height);
            else if (SelectedWindow is not null)
                _windowCapture.SetWindowTarget(SelectedWindow);
            else
            {
                AppendLog("Yakalanacak pencere veya ekran bölgesi seçin.");
                StopPipeline();
                return;
            }

            _windowCapture.FrameReceived += OnCapturedFrame;
            _windowCapture.StateChanged += st => Dispatcher.UIThread.Post(() => StatusMessage = st);
            _windowCapture.DiagnosticLogged += msg => Dispatcher.UIThread.Post(() => AppendLog(msg));
            _windowCapture.Start();
        }

        if (Output != OutputTarget.PcOverlay && AutoLaunchTvApp && !string.IsNullOrWhiteSpace(SamsungTvIp))
        {
            string tvHost = SamsungTvIp.Trim();
            string tvLocalUrl = $"http://{GetPreferredLanIp(tvHost)}:{_server.ListeningPort}";
            _ = Task.Run(async () =>
            {
                try
                {
                    bool launched = await SamsungTvInstaller.LaunchAsync(tvHost, tvLocalUrl, _server.AccessToken, HdmiPort);
                    Dispatcher.UIThread.Post(() => AppendLog(launched
                        ? "Samsung TV uygulaması otomatik başlatıldı."
                        : "Samsung TV uygulaması otomatik başlatılamadı (Geliştirici Modu/kurulum kontrol edin)."));
                }
                catch { }
            });
        }

        bool showPcOverlay = Output is OutputTarget.PcOverlay or OutputTarget.Both;
        OverlayVisibilityChanged?.Invoke(showPcOverlay);
        IsRunning = true;
        StatusMessage = $"Çalışıyor ({_ocrReader.EngineName} + {SelectedProvider.DisplayName})";
        AppendLog($"Oturum başlatıldı: Mod={Mode}, Motor={translator.EngineId}");
    }

    private void StartAudioLoopbackMode()
    {
        ISpeechTranscriber transcriber = AudioUseGemini
            ? new CloudAudioTranscriber(useGemini: true, SettingsStore.GetApiKey(AiProviderKind.Gemini))
            : !string.IsNullOrWhiteSpace(WhisperCliPath)
                ? new LocalWhisperTranscriber(WhisperCliPath, WhisperModelPath)
                : new CloudAudioTranscriber(useGemini: false, SettingsStore.GetApiKey(AiProviderKind.OpenAi));

        if (!transcriber.IsReady)
            AppendLog("Uyarı: Ses tanıma için Gemini/OpenAI API anahtarı veya yerel whisper-cli modeli girilmelidir.");

        _audioLoopback = new AudioLoopbackCapture();
        _audioLoopback.StateChanged += st => Dispatcher.UIThread.Post(() => StatusMessage = st);
        _audioLoopback.UtteranceCaptured += wav =>
        {
            if (_pipeline is null || !transcriber.IsReady) return;
            _ = Task.Run(async () =>
            {
                string? transcript = await transcriber.TranscribeWavAsync(wav, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(transcript) && _pipeline is { } p)
                    await p.TranslateTranscriptAsync(transcript);
            });
        };
        _audioLoopback.Start();
    }

    private void OnCapturedFrame(Frame frame)
    {
        if (_frameGate.IsStuck())
        {
            Dispatcher.UIThread.Post(() => AppendLog("OCR zaman aşımı algılandı — kare kapısı sıfırlandı."));
            _frameGate.Reset();
        }

        if (!_frameGate.TryEnter(out long token))
        {
            Volatile.Write(ref _pendingSkippedFrame, frame);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (_pipeline is { } p)
                {
                    var region = _customScreenRect is not null ? new CropRegion(0, 1, 0, 1) : _settings.ScanRegion;
                    await p.ProcessFrameAsync(frame, region);
                }
            }
            finally
            {
                _frameGate.Exit(token);
                var skipped = Interlocked.Exchange(ref _pendingSkippedFrame, null);
                if (skipped is not null && IsRunning)
                    OnCapturedFrame(skipped);
            }
        });
    }

    private void StopPipeline()
    {
        IsRunning = false;
        _pendingSkippedFrame = null;
        ActiveQuotaAlert = "";
        _server.SendAlert(null);
        OverlayVisibilityChanged?.Invoke(false);

        _psSession?.Dispose();
        _psSession = null;

        _windowCapture?.Dispose();
        _windowCapture = null;

        _audioLoopback?.Dispose();
        _audioLoopback = null;

        _pipeline?.Dispose();
        _pipeline = null;

        (_ocrReader as IDisposable)?.Dispose();
        _ocrReader = null;

        _frameGate.Reset();
        StatusMessage = "Durduruldu";
    }

    private ITextTranslator BuildTranslator()
    {
        if (_settings.Provider == AiProviderKind.LocalMarian)
            return _offlineFallback;

        ITextTranslator fallback = _offlineFallback;
        if (!AiProviderCatalog.Get(_settings.Provider).IsLocal &&
            !string.IsNullOrWhiteSpace(_cachedLocalOllamaModel))
        {
            fallback = new UniversalAiTranslator(
                AiProviderKind.Ollama,
                model: _cachedLocalOllamaModel,
                endpoint: AiProviderCatalog.Get(AiProviderKind.Ollama).DefaultEndpoint,
                customInstruction: _settings.CustomInstruction,
                fallback: _offlineFallback,
                sourceLanguage: _settings.SourceLanguage,
                genrePresetId: _settings.GenrePresetId,
                userGlossary: UserGlossaryStore.Default,
                maxRequestsPerMinute: 0);
        }

        var ai = new UniversalAiTranslator(
            _settings.Provider,
            SettingsStore.GetApiKey(_settings.Provider),
            _settings.Model,
            _settings.Endpoint,
            _settings.CustomInstruction,
            fallback,
            sourceLanguage: _settings.SourceLanguage,
            genrePresetId: _settings.GenrePresetId,
            userGlossary: UserGlossaryStore.Default);

        ai.StatusWarning += msg => Dispatcher.UIThread.Post(() => AppendLog(msg));
        ai.AlertChanged += msg => Dispatcher.UIThread.Post(() =>
        {
            ActiveQuotaAlert = msg ?? "";
            _server.SendAlert(msg);
        });
        return ai;
    }

    private async Task ProbeLocalOllamaModelAsync()
    {
        try
        {
            var res = await ModelDiscoveryService.DiscoverModelsAsync(
                AiProviderKind.Ollama,
                AiProviderCatalog.Get(AiProviderKind.Ollama).DefaultEndpoint);
            if (res.LiveQueried && res.Models.Count > 0)
            {
                var preferred = res.Models.FirstOrDefault(m => m.Id.StartsWith("gemma3:4b", StringComparison.OrdinalIgnoreCase))
                                ?? res.Models[0];
                _cachedLocalOllamaModel = preferred.Id;
                Dispatcher.UIThread.Post(() =>
                    AppendLog($"Akıllı yedek hazır: Bulut kotası dolarsa yerel Ollama ({preferred.Id}) devralacak."));
            }
        }
        catch { }
    }

    private static string GetPreferredLanIp(string remoteHost)
    {
        try
        {
            using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, 0);
            s.Connect(remoteHost, SamsungTvInstaller.SdbPort);
            if (s.LocalEndPoint is System.Net.IPEndPoint ep)
                return ep.Address.ToString();
        }
        catch { }
        return "127.0.0.1";
    }

    private void AppendLog(string message)
    {
        Logs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
        while (Logs.Count > 100)
            Logs.RemoveAt(Logs.Count - 1);
    }

    private void SaveAndNotify(params string[] extraProperties)
    {
        SettingsStore.Save(_settings);
        foreach (var prop in extraProperties)
            OnPropertyChanged(prop);
    }

    private void SaveAndNotify([CallerMemberName] string? propertyName = null)
    {
        SettingsStore.Save(_settings);
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        StopPipeline();
        _server.Dispose();
        _offlineFallback.Dispose();
        foreach (var bmp in _helpBitmapCache.Values)
            bmp.Dispose();
        _helpBitmapCache.Clear();
    }
}
