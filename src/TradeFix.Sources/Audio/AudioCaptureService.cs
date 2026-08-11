using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TradeFix.Sources.Audio;

/// <summary>
/// Captures this PC's desktop audio output (WASAPI loopback of the default render device) and
/// delivers it as fixed-format 16-bit PCM chunks, downmixed/resampled to a consistent target
/// format regardless of the system's actual output device configuration.
///
/// This is system-wide audio, not audio isolated to one specific application. Windows does have
/// a newer per-process loopback API (<c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c>) that
/// could capture just one app's sound, but it requires hand-written COM activation interop
/// (IActivateAudioInterfaceCompletionHandler) with the same verification risk that led to
/// choosing GDI over Windows.Graphics.Capture for video (see ScreenCaptureService remarks).
/// NAudio's WASAPI loopback is mature and widely used — the reliable choice for a first working
/// version. See docs/KNOWN_LIMITATIONS.md.
///
/// Resampling uses NAudio's pure-managed <see cref="WdlResamplingSampleProvider"/>, not
/// <c>MediaFoundationResampler</c>. The MF-based resampler was tried first and measured (via a
/// real playing tone, not just "doesn't throw") to output near-silence for roughly the first
/// second of every capture session regardless of quality setting, before "waking up" and working
/// correctly — a COM/Media-Foundation-transform priming quirk. The pure-managed chain produced
/// correct, non-silent output from the very first read in the same test. See PROGRESS.md for the
/// full before/after measurements that led to this choice.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    private readonly WaveFormat _targetFormat;
    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _sourceBuffer;
    private IWaveProvider? _outputProvider;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;

    /// <summary>Fires with a chunk of raw 16-bit PCM (little-endian, mono unless
    /// <paramref name="channels"/> &gt; 1) roughly every 100ms, plus the cumulative captured-audio
    /// timeline position (in milliseconds of audio, not wall-clock) at which this chunk begins —
    /// derived from bytes captured so far, not a timer read, so it stays correct even if a pump
    /// tick is late. Used downstream to detect and fill gaps left by dropped chunks so playback
    /// stays anchored to real elapsed audio time instead of silently drifting ahead. See
    /// AudioSyncGapFiller in TradeFix.Agent.</summary>
    public event Func<byte[], long, Task>? ChunkCaptured;

    public AudioCaptureService(int sampleRate = 24000, int channels = 1)
    {
        _targetFormat = new WaveFormat(sampleRate, 16, channels);
    }

    public WaveFormat Format => _targetFormat;

    public bool IsRunning => _pumpTask is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _capture = new WasapiLoopbackCapture();
        _sourceBuffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2)
        };

        ISampleProvider sampleProvider = _sourceBuffer.ToSampleProvider();
        if (sampleProvider.WaveFormat.Channels > 1 && _targetFormat.Channels == 1)
        {
            sampleProvider = new StereoToMonoSampleProvider(sampleProvider) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }

        var resampled = sampleProvider.WaveFormat.SampleRate == _targetFormat.SampleRate
            ? sampleProvider
            : new WdlResamplingSampleProvider(sampleProvider, _targetFormat.SampleRate);

        _outputProvider = resampled.ToWaveProvider16();

        _capture.DataAvailable += (_, e) => _sourceBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        _capture.StartRecording();

        _cts = new CancellationTokenSource();
        _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        // ~100ms chunks: small enough for low latency, large enough not to flood the network
        // with tiny messages. Paced to roughly real-time so each read has genuine newly-captured
        // audio to consume rather than racing ahead of the capture callback.
        var chunkDuration = TimeSpan.FromMilliseconds(100);
        var chunkBytes = Math.Max(64, _targetFormat.AverageBytesPerSecond / 10);
        var buffer = new byte[chunkBytes];
        var bytesPerMillisecond = Math.Max(1, _targetFormat.AverageBytesPerSecond / 1000.0);
        var cumulativeBytesCaptured = 0L;

        using var timer = new PeriodicTimer(chunkDuration);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Read every full chunk currently buffered, not just one per tick: PeriodicTimer
            // coalesces missed ticks, so a one-chunk-per-tick pump that ever falls behind
            // (UI stall, scheduler hiccup) stays behind forever — audio arrives late by the
            // accumulated backlog. Draining per tick means a hiccup adds latency once and the
            // next tick catches back up to live.
            for (var drained = 0; drained < 10; drained++)
            {
                int read;
                try
                {
                    read = _outputProvider!.Read(buffer, 0, buffer.Length);
                }
                catch
                {
                    return; // capture device went away (e.g. output device changed) — stop cleanly
                }

                if (read <= 0)
                {
                    break;
                }

                if (ChunkCaptured is not null)
                {
                    var chunk = new byte[read];
                    Array.Copy(buffer, chunk, read);
                    var timestampMs = (long)(cumulativeBytesCaptured / bytesPerMillisecond);
                    cumulativeBytesCaptured += read;
                    try
                    {
                        await ChunkCaptured.Invoke(chunk, timestampMs);
                    }
                    catch
                    {
                        // a single failed delivery (e.g. broadcast to a dropped subscriber)
                        // shouldn't kill the capture pump
                    }
                }

                if (read < buffer.Length)
                {
                    break; // consumed everything currently available — back to the timer
                }
            }
        }
    }

    public void Stop() => _cts?.Cancel();

    public void Dispose()
    {
        Stop();
        try
        {
            _capture?.StopRecording();
        }
        catch
        {
            // best-effort
        }

        _capture?.Dispose();
    }
}
