// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using EssvbSub.App.Services;
using EssvbSub.Capture.Desktop;
using EssvbSub.Capture.Ocr;
using EssvbSub.Capture.PlayStation;
using EssvbSub.Capture.TvInstall;
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

    public MainViewModel()
    {
        _settings = SettingsStore.Load();
        _apiKeyInput = SettingsStore.GetApiKey(_settings.Provider);

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

        _ = StartServerSafeAsync();
        RefreshWindows();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<SubtitleEvent>? OverlayEventReceived;
    public event Action<bool>? OverlayVisibilityChanged;
    public event Action<bool>? OverlayClickThroughChanged;
    public event Func<Task<(CropRegion Region, int X, int Y, int Width, int Height)?>>? SelectRegionRequested;

    public ObservableCollection<WindowTarget> AvailableWindows { get; } = [];
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

    public OperatingMode Mode
    {
        get => _settings.Mode;
        set
        {
            if (_settings.Mode == value) return;
            _settings.Mode = value;
            if (value == OperatingMode.PcGame)
                _settings.Output = OutputTarget.PcOverlay;
            else if (value == OperatingMode.PlayStation && _settings.Output == OutputTarget.PcOverlay)
                _settings.Output = OutputTarget.SamsungTv;

            SaveAndNotify(nameof(Mode), nameof(IsPlayStationMode), nameof(IsPcGameMode), nameof(IsMediaStreamMode), nameof(Output));
        }
    }

    public bool IsPlayStationMode => Mode == OperatingMode.PlayStation;
    public bool IsPcGameMode => Mode == OperatingMode.PcGame;
    public bool IsMediaStreamMode => Mode == OperatingMode.MediaStream;

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
        }
    }

    public IReadOnlyList<string> SuggestedModels => SelectedProvider.SuggestedModels;
    public bool ShowEndpointBox => SelectedProvider.Kind is AiProviderKind.Ollama or AiProviderKind.LmStudio or AiProviderKind.CustomOpenAi;
    public bool ShowApiKeyBox => SelectedProvider.RequiresApiKey || SelectedProvider.Kind == AiProviderKind.CustomOpenAi;

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
            if (value is not null) _customScreenRect = null;
            OnPropertyChanged();
        }
    }

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
            SaveAndNotify();
        }
    }

    public SubtitleTextColor TextColor
    {
        get => _settings.Style.TextColor;
        set
        {
            _settings.Style = _settings.Style with { TextColor = value };
            _server.SendStyle(_settings.Style);
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

        if (SelectedWindow is null && AvailableWindows.Count > 0)
            SelectedWindow = AvailableWindows[0];
    }

    public async Task SelectScreenRegionAsync()
    {
        if (SelectRegionRequested is null) return;
        var res = await SelectRegionRequested.Invoke();
        if (res is null) return;

        _settings.ScanRegion = res.Value.Region.Clamp();
        _customScreenRect = (res.Value.X, res.Value.Y, res.Value.Width, res.Value.Height);
        SaveAndNotify(nameof(RegionSummary));
        AppendLog($"Ekran bölgesi güncellendi: {res.Value.Width}x{res.Value.Height} @ ({res.Value.X},{res.Value.Y})");
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
    }
}
