// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace EssvbSub.Capture.Desktop;

public interface ISpeechTranscriber
{
    bool IsReady { get; }
    Task<string?> TranscribeWavAsync(byte[] wavData, CancellationToken ct);
}

/// <summary>
/// Offline speech-to-text using local whisper.cpp / whisper-cli binary (zero cloud upload).
/// </summary>
public sealed class LocalWhisperTranscriber : ISpeechTranscriber
{
    private readonly string _binaryPath;
    private readonly string _modelPath;

    public LocalWhisperTranscriber(string binaryPath, string modelPath)
    {
        _binaryPath = binaryPath;
        _modelPath = modelPath;
    }

    public bool IsReady => File.Exists(_binaryPath) && File.Exists(_modelPath);

    public async Task<string?> TranscribeWavAsync(byte[] wavData, CancellationToken ct)
    {
        if (!IsReady) return null;
        string tmp = Path.Combine(Path.GetTempPath(), $"essvb-aud-{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(tmp, wavData, ct).ConfigureAwait(false);
            var psi = new ProcessStartInfo(_binaryPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(_modelPath);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("--no-timestamps");
            psi.ArgumentList.Add("-nt");

            using var proc = Process.Start(psi);
            if (proc is null) return null;
            string output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            string cleaned = output.Trim();
            return string.IsNullOrWhiteSpace(cleaned) || cleaned.StartsWith('[') ? null : cleaned;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}

/// <summary>
/// Cloud speech-to-text via OpenAI Whisper API or Gemini Multimodal Audio for DRM-protected MediaStream mode.
/// </summary>
public sealed class CloudAudioTranscriber : ISpeechTranscriber
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly bool _useGemini;
    private readonly string _apiKey;
    private readonly string _model;

    public CloudAudioTranscriber(bool useGemini, string apiKey, string? model = null)
    {
        _useGemini = useGemini;
        _apiKey = apiKey.Trim();
        _model = string.IsNullOrWhiteSpace(model)
            ? (useGemini ? "gemini-2.5-flash" : "whisper-1")
            : model.Trim();
    }

    public bool IsReady => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<string?> TranscribeWavAsync(byte[] wavData, CancellationToken ct)
    {
        if (!IsReady) return null;

        if (_useGemini)
        {
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(_model)}:generateContent";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("x-goog-api-key", _apiKey);
            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = "Transcribe the spoken English dialogue concisely. Reply ONLY with the spoken English words or empty if silence." },
                            new { inline_data = new { mime_type = "audio/wav", data = Convert.ToBase64String(wavData) } }
                        }
                    }
                }
            };
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var txt = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
            return string.IsNullOrWhiteSpace(txt) ? null : txt.Trim();
        }
        else
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(wavData), "file", "audio.wav" },
                { new StringContent(_model), "model" },
                { new StringContent("en"), "language" }
            };
            req.Content = form;
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;
        }
    }
}

/// <summary>
/// Cross-platform system loopback audio capture for DRM-protected apps (Netflix, Amazon Prime, Disney+).
/// Segments speech using RMS Voice Activity Detection and emits WAV chunks.
/// </summary>
public sealed class AudioLoopbackCapture : IDisposable
{
    public const int TargetSampleRate = 16000;
    private readonly MemoryStream _speechBuffer = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private bool _inSpeech;
    private long _silenceStartMs;
    private long _speechStartMs;
    private bool _disposed;

    public event Action<byte[]>? UtteranceCaptured;
    public event Action<string>? StateChanged;

    public void Start()
    {
        if (_disposed || _cts is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _worker = OperatingSystem.IsWindows()
            ? Task.Run(() => RunWindowsWasapiLoopback(ct), ct)
            : Task.Run(() => RunLinuxPipeWireLoopback(ct), ct);
        StateChanged?.Invoke("Ses Yakalama Aktif");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _speechBuffer.SetLength(0);
        _inSpeech = false;
        StateChanged?.Invoke("Ses Yakalama Durduruldu");
    }

    private void FeedPcm16Mono(ReadOnlySpan<short> samples)
    {
        if (samples.Length == 0) return;

        double sumSq = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double norm = samples[i] / 32768.0;
            sumSq += norm * norm;
        }
        double rms = Math.Sqrt(sumSq / samples.Length);
        long now = Environment.TickCount64;

        if (rms >= 0.015)
        {
            if (!_inSpeech)
            {
                _inSpeech = true;
                _speechStartMs = now;
                _speechBuffer.SetLength(0);
            }
            _silenceStartMs = 0;
            WriteSamples(samples);

            if ((now - _speechStartMs) >= 7000)
                FlushUtterance();
        }
        else if (_inSpeech)
        {
            WriteSamples(samples);
            if (_silenceStartMs == 0)
                _silenceStartMs = now;
            else if ((now - _silenceStartMs) >= 550)
                FlushUtterance();
        }
    }

    private void WriteSamples(ReadOnlySpan<short> samples)
    {
        Span<byte> bytes = stackalloc byte[2];
        for (int i = 0; i < samples.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes, samples[i]);
            _speechBuffer.Write(bytes);
        }
    }

    private void FlushUtterance()
    {
        _inSpeech = false;
        _silenceStartMs = 0;
        byte[] pcm = _speechBuffer.ToArray();
        _speechBuffer.SetLength(0);

        if (pcm.Length < TargetSampleRate) // < 0.5 seconds at 16kHz 16-bit
            return;

        UtteranceCaptured?.Invoke(WrapAsWav(pcm, TargetSampleRate, 1, 16));
    }

    public static byte[] WrapAsWav(byte[] pcmData, int sampleRate, short channels, short bitsPerSample)
    {
        using var ms = new MemoryStream(44 + pcmData.Length);
        using var bw = new BinaryWriter(ms);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcmData.Length);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * (bitsPerSample / 8));
        bw.Write((short)(channels * (bitsPerSample / 8)));
        bw.Write(bitsPerSample);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(pcmData.Length);
        bw.Write(pcmData);
        return ms.ToArray();
    }

    private void RunLinuxPipeWireLoopback(CancellationToken ct)
    {
        // CachyOS uses PipeWire (pw-record / parec) with monitor sink capture
        string[][] cmds =
        [
            ["pw-record", "--rate=16000 --channels=1 --format=s16 -"],
            ["parec", "--rate=16000 --channels=1 --format=s16le --raw"]
        ];

        foreach (var cmd in cmds)
        {
            try
            {
                var psi = new ProcessStartInfo(cmd[0], cmd[1])
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc is null) continue;

                var buf = new byte[3200]; // 100ms @ 16kHz 16-bit mono
                var shortBuf = new short[buf.Length / 2];
                while (!ct.IsCancellationRequested)
                {
                    int read = proc.StandardOutput.BaseStream.Read(buf, 0, buf.Length);
                    if (read <= 0) break;
                    int sampleCount = read / 2;
                    Buffer.BlockCopy(buf, 0, shortBuf, 0, sampleCount * 2);
                    FeedPcm16Mono(shortBuf.AsSpan(0, sampleCount));
                }

                try { if (!proc.HasExited) proc.Kill(); } catch { }
                return;
            }
            catch { }
        }

        StateChanged?.Invoke("PipeWire/PulseAudio loopback (pw-record / parec) bulunamadı.");
    }

    private void RunWindowsWasapiLoopback(CancellationToken ct)
    {
        try
        {
            WasapiCom.CaptureLoopback(ct, FeedPcm16Mono);
        }
        catch (Exception ex)
        {
            StateChanged?.Invoke($"WASAPI ses yakalama hatası: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _speechBuffer.Dispose();
    }

    private static unsafe class WasapiCom
    {
        private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IidImmDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
        private static readonly Guid IidIaudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid IidIaudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        private const uint ClsctxAll = 23;
        private const int ERender = 0;
        private const int EMedia = 1;
        private const uint AudclntStreamflagsLoopback = 0x00020000;

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        public static void CaptureLoopback(CancellationToken ct, Action<ReadOnlySpan<short>> onSamples)
        {
            CoInitializeEx(0, 0);
            nint enumerator = 0, device = 0, audioClient = 0, captureClient = 0, fmtPtr = 0;

            try
            {
                if (CoCreateInstance(ClsidMmDeviceEnumerator, 0, ClsctxAll, IidImmDeviceEnumerator, out enumerator) != 0)
                    return;

                // IMMDeviceEnumerator::GetDefaultAudioEndpoint (slot 4)
                var enumVt = *(nint**)enumerator;
                var getDefault = (delegate* unmanaged[Stdcall]<nint, int, int, out nint, int>)enumVt[4];
                if (getDefault(enumerator, ERender, EMedia, out device) != 0)
                    return;

                // IMMDevice::Activate (slot 3)
                var devVt = *(nint**)device;
                var activate = (delegate* unmanaged[Stdcall]<nint, in Guid, uint, nint, out nint, int>)devVt[3];
                if (activate(device, IidIaudioClient, ClsctxAll, 0, out audioClient) != 0)
                    return;

                var clientVt = *(nint**)audioClient;
                // IAudioClient::GetMixFormat (slot 8)
                var getMixFormat = (delegate* unmanaged[Stdcall]<nint, out nint, int>)clientVt[8];
                if (getMixFormat(audioClient, out fmtPtr) != 0 || fmtPtr == 0)
                    return;

                var fmt = Marshal.PtrToStructure<WaveFormatEx>(fmtPtr);
                // IAudioClient::Initialize (slot 3)
                var init = (delegate* unmanaged[Stdcall]<nint, int, uint, long, long, nint, nint, int>)clientVt[3];
                if (init(audioClient, 0, AudclntStreamflagsLoopback, 10_000_000, 0, fmtPtr, 0) != 0)
                    return;

                // IAudioClient::GetService (slot 14)
                var getService = (delegate* unmanaged[Stdcall]<nint, in Guid, out nint, int>)clientVt[14];
                if (getService(audioClient, IidIaudioCaptureClient, out captureClient) != 0)
                    return;

                // IAudioClient::Start (slot 10)
                var start = (delegate* unmanaged[Stdcall]<nint, int>)clientVt[10];
                start(audioClient);

                var capVt = *(nint**)captureClient;
                var getNextPacketSize = (delegate* unmanaged[Stdcall]<nint, out uint, int>)capVt[5];
                var getBuffer = (delegate* unmanaged[Stdcall]<nint, out nint, out uint, out uint, nint, nint, int>)capVt[3];
                var releaseBuffer = (delegate* unmanaged[Stdcall]<nint, uint, int>)capVt[4];

                int ratio = Math.Max(1, (int)(fmt.nSamplesPerSec / TargetSampleRate));
                bool isFloat = fmt.wBitsPerSample == 32;
                var outSamples = new short[4096];

                while (!ct.IsCancellationRequested)
                {
                    if (getNextPacketSize(captureClient, out uint packetFrames) != 0 || packetFrames == 0)
                    {
                        Thread.Sleep(25);
                        continue;
                    }

                    while (packetFrames > 0 && !ct.IsCancellationRequested)
                    {
                        if (getBuffer(captureClient, out nint dataPtr, out uint frames, out uint flags, 0, 0) != 0)
                            break;

                        int produced = 0;
                        if ((flags & 2) == 0 && dataPtr != 0) // not AUDCLNT_BUFFERFLAGS_SILENT
                        {
                            int ch = Math.Max(1, (int)fmt.nChannels);
                            for (int f = 0; f < (int)frames; f += ratio)
                            {
                                double mixed = 0;
                                if (isFloat)
                                {
                                    float* fp = (float*)dataPtr + (f * ch);
                                    for (int c = 0; c < ch; c++) mixed += fp[c];
                                    mixed /= ch;
                                    outSamples[produced++] = (short)Math.Clamp(mixed * 32767.0, -32768, 32767);
                                }
                                else
                                {
                                    short* sp = (short*)dataPtr + (f * ch);
                                    for (int c = 0; c < ch; c++) mixed += sp[c];
                                    outSamples[produced++] = (short)(mixed / ch);
                                }

                                if (produced == outSamples.Length)
                                {
                                    onSamples(outSamples.AsSpan(0, produced));
                                    produced = 0;
                                }
                            }
                            if (produced > 0)
                                onSamples(outSamples.AsSpan(0, produced));
                        }

                        releaseBuffer(captureClient, frames);
                        if (getNextPacketSize(captureClient, out packetFrames) != 0)
                            break;
                    }
                }

                // IAudioClient::Stop (slot 11)
                var stop = (delegate* unmanaged[Stdcall]<nint, int>)clientVt[11];
                stop(audioClient);
            }
            finally
            {
                if (fmtPtr != 0) CoTaskMemFree(fmtPtr);
                if (captureClient != 0) Marshal.Release(captureClient);
                if (audioClient != 0) Marshal.Release(audioClient);
                if (device != 0) Marshal.Release(device);
                if (enumerator != 0) Marshal.Release(enumerator);
                CoUninitialize();
            }
        }

        [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint coInit);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        [DllImport("ole32.dll")] private static extern void CoTaskMemFree(nint pv);
        [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid rclsid, nint outer, uint clsCtx, in Guid riid, out nint ppv);
    }
}
