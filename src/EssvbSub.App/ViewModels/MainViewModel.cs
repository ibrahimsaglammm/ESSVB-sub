// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<SubtitleEvent>? OverlayEventReceived;
    public event Action<SubtitleStyle>? OverlayStyleChanged;
    public event Action<bool>? OverlayVisibilityChanged;
    public event Action<bool>? OverlayClickThroughChanged;
    public event Action<bool>? OverlayEditModeChanged;
    public event Action<double, double, double, double, bool>? OverlayBoundsChanged;
    public event Func<Task<(CropRegion Region, int X, int Y, int Width, int Height)?>>? SelectRegionRequested;

    public ObservableCollection<WindowTarget> AvailableWindows { get; } = [];
    public ObservableCollection<DiscoveredModel> AvailableModels { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public IReadOnlyList<AiProviderDescriptor> Providers => AiProviderCatalog.All;
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
        set { _settings.CustomInstruction = value; SaveAndNotify(); }
    }

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

    public async Task RefreshModelsAsync()
    {
        var provider = _settings.Provider;
        string endpoint = EndpointUrl;
        string apiKey = SettingsStore.GetApiKey(provider);

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
        try
        {
            AppendLog("Yerel ağda Samsung Tizen TV aranıyor...");
            var tvs = await SamsungTvInstaller.DiscoverTvsOnLanAsync();
            if (tvs.Count == 0)
            {
                AppendLog("Ağda Samsung TV bulunamadı. TV'nin açık ve aynı ağda olduğundan emin olun.");
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
        }
        catch (Exception ex)
        {
            AppendLog($"Samsung TV arama hatası: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task InstallSamsungTvAppAsync()
    {
        if (string.IsNullOrWhiteSpace(SamsungTvIp))
        {
            AppendLog("Samsung TV IP adresini girin veya 'TV Ara'ya basın.");
            return;
        }

        IsBusy = true;
        try
        {
            string tvDir = Path.Combine(AppContext.BaseDirectory, "tv-tizen");
            if (!Directory.Exists(tvDir))
                tvDir = Path.Combine(AppInfo.ExecutableDirectory, "tv-tizen");
            if (!Directory.Exists(tvDir))
                tvDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tv-tizen"));

            string host = SamsungTvIp.Trim();
            string localUrl = $"http://{GetPreferredLanIp(host)}:{_server.ListeningPort}";
            await SamsungTvInstaller.InstallAndLaunchAsync(
                host,
                tvDir,
                EssvbPaths.ProfilesDir,
                openBrowser => SamsungTvInstaller.SignInSamsungAccountAsync(openBrowser, TimeSpan.FromMinutes(3)),
                localUrl,
                _server.AccessToken,
                HdmiPort,
                msg => Dispatcher.UIThread.Post(() => AppendLog(msg)),
                selectedModel: SelectedTvModel);

            AppendLog("Samsung Tizen TV uygulaması kuruldu ve başlatıldı!");
        }
        catch (Exception ex)
        {
            AppendLog($"Samsung TV kurulum hatası: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LaunchSamsungTvAppAsync()
    {
        if (string.IsNullOrWhiteSpace(SamsungTvIp))
        {
            AppendLog("Samsung TV IP adresini girin veya 'TV Ara'ya basın.");
            return;
        }

        IsBusy = true;
        try
        {
            string host = SamsungTvIp.Trim();
            string localUrl = $"http://{GetPreferredLanIp(host)}:{_server.ListeningPort}";
            bool ok = await SamsungTvInstaller.LaunchAsync(host, localUrl, _server.AccessToken, HdmiPort);
            AppendLog(ok
                ? "ESSVB-sub TV'de başlatıldı."
                : "TV'de başlatılamadı (Uygulama TV'ye kurulu mu ve TV Geliştirici Modu açık mı?).");
        }
        catch (Exception ex)
        {
            AppendLog($"TV başlatma hatası: {ex.Message}");
        }
        finally
        {
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
        IsBusy = true;
        AiTestResult = "Test ediliyor…";
        try
        {
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

    public void ToggleStartStop()
    {
        if (IsRunning) StopPipeline();
        else StartPipeline();
    }

    private void StartPipeline()
    {
        StopPipeline();
        LastSourceText = "";
        LastTranslatedText = "";
        _server.SendsToTv = Output != OutputTarget.PcOverlay;
        _server.SendStyle(_settings.Style);

        _ocrReader = CrossPlatformOcrFactory.CreateBestAvailable(_settings.SourceLanguage);
        var translator = BuildTranslator();
        var cache = TranslationCache.ForEngine(EssvbPaths.CacheDir, translator.EngineId, _settings.SourceLanguage, "tr");

        _pipeline = new SubtitlePipeline(_ocrReader, translator, _server, new ReadScheduler(), cache)
        {
            MediaTitle = MediaTitle,
            ShowSpeakerNames = true
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
        if (!_frameGate.TryEnter())
            return;

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
                _frameGate.Exit();
            }
        });
    }

    private void StopPipeline()
    {
        IsRunning = false;
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

        var ai = new UniversalAiTranslator(
            _settings.Provider,
            SettingsStore.GetApiKey(_settings.Provider),
            _settings.Model,
            _settings.Endpoint,
            _settings.CustomInstruction,
            _offlineFallback);

        ai.StatusWarning += msg => Dispatcher.UIThread.Post(() => AppendLog(msg));
        return ai;
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
