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
    private Thread? _pumpThread;

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

    public bool IsRunning => _pumpThread is { IsAlive: true };

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
            BufferDuration = TimeSpan.FromSeconds(2),
            // CRITICAL — measured, not theoretical: BufferedWaveProvider's default ReadFully=true
            // ZERO-PADS every read to the requested length, so the pump's "read until the buffer
            // runs dry" loop never saw a short read and emitted its full 10-chunk drain every
            // tick: 1 chunk of real audio + 9 chunks of silence, 10x real-time, with timestamps
            // racing 10x ahead of the true timeline (probe: 300 chunks / 29.9s of claimed audio
            // in 3 real seconds). Downstream that was the entire family of field audio bugs —
            // desync, standing lag, shredded/no sound. false = a read returns only genuinely
            // captured audio, which is what the pacing loop was written to expect.
            ReadFully = false
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
        // A dedicated above-normal-priority thread, NOT Task.Run: on a Master whose CPU is
        // saturated by video encoding, thread-pool scheduling delays the 100ms pump ticks —
        // chunks then leave in late catch-up bursts, which downstream becomes audible audio
        // delay on every node ("the voice has lags"). Audio is a trivial fraction of the CPU;
        // letting it preempt video work keeps the voice on time even when the encoder is
        // drowning (the encoder process itself also runs BelowNormal — see H264VideoEncoder).
        // The loop is fully synchronous on purpose: any await would resume on the thread pool,
        // silently abandoning this thread and its priority.
        _pumpThread = new Thread(() => Pump(_cts.Token))
        {
            IsBackground = true,
            Name = "tfx-audio-pump",
            Priority = ThreadPriority.AboveNormal
        };
        _pumpThread.Start();
    }

    private void Pump(CancellationToken cancellationToken)
    {
        // ~100ms chunks: small enough for low latency, large enough not to flood the network
        // with tiny messages. Paced against a Stopwatch to absolute tick targets so a late tick
        // doesn't shift the whole schedule — the next tick simply comes sooner.
        const int chunkMs = 100;
        var chunkBytes = Math.Max(64, _targetFormat.AverageBytesPerSecond / 10);
        var buffer = new byte[chunkBytes];
        var bytesPerMillisecond = Math.Max(1, _targetFormat.AverageBytesPerSecond / 1000.0);
        var cumulativeBytesCaptured = 0L;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        long nextTickMs = chunkMs;

        while (!cancellationToken.IsCancellationRequested)
        {
            var waitMs = nextTickMs - clock.ElapsedMilliseconds;
            if (waitMs > 0 && cancellationToken.WaitHandle.WaitOne((int)waitMs))
            {
                break; // cancelled while waiting for the next tick
            }

            nextTickMs += chunkMs;
            if (clock.ElapsedMilliseconds > nextTickMs + 1000)
            {
                // Fell absurdly far behind (system sleep, debugger) — rebase rather than
                // rapid-firing a tick per missed interval.
                nextTickMs = clock.ElapsedMilliseconds + chunkMs;
            }

            // Read every full chunk currently buffered, not just one per tick: a late tick means
            // more than one chunk of audio is waiting, and a one-chunk-per-tick pump that ever
            // falls behind stays behind forever — audio arrives late by the accumulated backlog.
            // Draining per tick means a hiccup adds latency once and the next tick catches back
            // up to live.
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
                        // Handlers post to MediaHub subscriber queues, which is non-blocking —
                        // waiting the returned task here keeps chunk order deterministic without
                        // ever parking this thread on real I/O.
                        ChunkCaptured.Invoke(chunk, timestampMs).GetAwaiter().GetResult();
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
