using System.Diagnostics;
using NAudio.Dsp;
using NAudio.Wave;

namespace TaskbarLyrics;

/// <summary>
/// Captures the default render device's output via WASAPI loopback and turns it
/// into a smoothed, log-spaced magnitude spectrum the visualizer can draw.
///
/// Capture runs on NAudio's own thread and only appends mono samples to a ring
/// buffer; the FFT is pulled on the render thread once per frame via
/// <see cref="Update"/>, decoupling analysis rate from device rate. Start/Stop
/// fully release the audio device so a disabled visualizer costs nothing.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int Bands = 64;
    private const int FftSize = 2048;              // ~43ms window @ 48kHz
    private const int FftM = 11;                   // log2(2048)
    private const int RingSize = FftSize * 4;

    private readonly object _lock = new();
    private readonly float[] _ring = new float[RingSize];
    private int _writePos;
    private int _sampleRate = 48000;

    private WasapiLoopbackCapture? _capture;
    private volatile bool _running;
    private long _lastDataTick;

    // Analysis state (render thread only).
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _scratch = new float[FftSize];
    private readonly float[] _bands = new float[Bands];
    private readonly int[] _bandLo = new int[Bands];
    private readonly int[] _bandHi = new int[Bands];
    private bool _bandsMapped;

    /// <summary>Per-band magnitudes, 0..~1, smoothed with fast attack / slow decay.</summary>
    public float[] Spectrum => _bands;

    /// <summary>Overall loudness 0..~1 (broadband RMS), smoothed.</summary>
    public float Level { get; private set; }

    /// <summary>Low-end energy 0..~1 (kick/bass), smoothed — good for pulse presets.</summary>
    public float Bass { get; private set; }

    /// <summary>True when the device is delivering audio (not silent / not stopped).</summary>
    public bool IsActive => _running &&
        (Stopwatch.GetTimestamp() - _lastDataTick) / (double)Stopwatch.Frequency < 1.5;

    public AudioEngine()
    {
        for (int i = 0; i < FftSize; i++)                 // Hann window
            _window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1))));
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        TryOpen();
    }

    private void TryOpen()
    {
        try
        {
            var capture = new WasapiLoopbackCapture();   // default render device
            _sampleRate = capture.WaveFormat.SampleRate;
            _bandsMapped = false;
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            _capture = capture;
            capture.StartRecording();
            Log.Write($"viz-audio: capturing {_sampleRate}Hz {capture.WaveFormat.Channels}ch");
        }
        catch (Exception ex)
        {
            Log.Write($"viz-audio: open failed: {ex.Message}");
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = ((WasapiLoopbackCapture)sender!).WaveFormat;
        int ch = Math.Max(1, fmt.Channels);
        int bytesPerSample = fmt.BitsPerSample / 8;
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat;
        int frameBytes = bytesPerSample * ch;
        if (frameBytes == 0) return;

        var buf = e.Buffer;
        int frames = e.BytesRecorded / frameBytes;
        lock (_lock)
        {
            for (int f = 0; f < frames; f++)
            {
                int baseIdx = f * frameBytes;
                float mono = 0;
                for (int c = 0; c < ch; c++)
                {
                    int o = baseIdx + c * bytesPerSample;
                    mono += isFloat
                        ? BitConverter.ToSingle(buf, o)
                        : BitConverter.ToInt16(buf, o) / 32768f;
                }
                _ring[_writePos] = mono / ch;
                _writePos = (_writePos + 1) % RingSize;
            }
        }
        if (frames > 0) _lastDataTick = Stopwatch.GetTimestamp();
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) Log.Write($"viz-audio: stopped: {e.Exception.Message}");
        DisposeCapture();
        // Device switched / unplugged while enabled — reopen shortly.
        if (_running) _ = Task.Delay(1200).ContinueWith(_ => { if (_running) TryOpen(); });
    }

    /// <summary>
    /// Pull the latest samples, run the FFT, and advance the smoothed band/level
    /// state by <paramref name="dtMs"/>. Call once per rendered frame.
    /// </summary>
    public void Update(double dtMs)
    {
        // Snapshot the newest FftSize samples in chronological order.
        lock (_lock)
        {
            int start = (_writePos - FftSize + RingSize) % RingSize;
            for (int i = 0; i < FftSize; i++)
                _scratch[i] = _ring[(start + i) % RingSize];
        }

        double rms = 0;
        for (int i = 0; i < FftSize; i++)
        {
            rms += _scratch[i] * _scratch[i];
            _fft[i].X = _scratch[i] * _window[i];
            _fft[i].Y = 0;
        }
        rms = Math.Sqrt(rms / FftSize);

        FastFourierTransform.FFT(true, FftM, _fft);
        EnsureBandMap();

        // Fast attack, slow decay makes bars snap up and glide down.
        float decay = (float)Math.Exp(-dtMs / 220.0);
        float bandGain = 9f;
        for (int b = 0; b < Bands; b++)
        {
            double sum = 0; int n = 0;
            for (int k = _bandLo[b]; k <= _bandHi[b]; k++, n++)
            {
                double re = _fft[k].X, im = _fft[k].Y;
                sum += Math.Sqrt(re * re + im * im);
            }
            float mag = n > 0 ? (float)(sum / n) : 0;
            // Perceptual-ish compression + gentle high-frequency lift.
            float v = (float)Math.Sqrt(mag) * bandGain * (0.6f + 1.4f * b / Bands);
            v = Math.Clamp(v, 0, 1.4f);
            _bands[b] = v > _bands[b] ? v : _bands[b] * decay + v * (1 - decay);
        }

        float bassRaw = 0;
        for (int b = 0; b < 6; b++) bassRaw += _bands[b];
        bassRaw /= 6;
        Bass = bassRaw > Bass ? bassRaw : Bass * decay + bassRaw * (1 - decay);

        float lvl = Math.Clamp((float)rms * 6f, 0, 1.4f);
        Level = lvl > Level ? lvl : Level * decay + lvl * (1 - decay);
    }

    private void EnsureBandMap()
    {
        if (_bandsMapped) return;
        // Log-spaced bands from ~30Hz to ~16kHz across the FFT bins.
        double nyquist = _sampleRate / 2.0;
        double loHz = 30, hiHz = Math.Min(16000, nyquist);
        double binHz = _sampleRate / (double)FftSize;
        int prevBin = Math.Max(1, (int)(loHz / binHz));
        for (int b = 0; b < Bands; b++)
        {
            double frac = (b + 1) / (double)Bands;
            double hz = loHz * Math.Pow(hiHz / loHz, frac);
            int bin = Math.Clamp((int)(hz / binHz), prevBin, FftSize / 2 - 1);
            _bandLo[b] = Math.Min(prevBin, bin);
            _bandHi[b] = Math.Max(prevBin, bin);
            prevBin = bin + 1;
        }
        _bandsMapped = true;
    }

    public void Stop()
    {
        _running = false;
        DisposeCapture();
        lock (_lock) Array.Clear(_ring);
        Array.Clear(_bands);
        Level = Bass = 0;
    }

    private void DisposeCapture()
    {
        var c = _capture;
        _capture = null;
        if (c == null) return;
        try { c.DataAvailable -= OnData; c.RecordingStopped -= OnStopped; c.Dispose(); } catch { }
    }

    public void Dispose() => Stop();
}
