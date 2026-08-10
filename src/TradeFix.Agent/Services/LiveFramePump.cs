using System.Threading.Channels;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TradeFix.Agent.Services;

/// <summary>
/// Decouples "a frame arrived over the network" from "the UI has decoded and displayed it", one
/// pump per live-capture source. Mirrors <c>TradeFix.Network.Media.MediaHub</c>'s subscriber pump
/// on the Master side, for the same underlying reason: capture quality/resolution were raised to
/// (near-)maximum on request, and decoding a large JPEG is real CPU work. The previous design
/// decoded synchronously inside a blocking <c>Dispatcher.Invoke</c> call made directly from the
/// network-receive loop — every frame stalled both the UI thread (blocked on decode) and the
/// network thread (blocked waiting for the UI thread), compounding into the render-side lag the
/// user reported on top of the network-side lag already fixed in MediaHub.
///
/// Only the latest frame is ever queued (older ones are dropped, not backlogged) and decoding runs
/// off the UI thread; only the final, cheap property assignment is marshaled onto the dispatcher,
/// non-blockingly.
/// </summary>
public sealed class LiveFramePump : IDisposable
{
    private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpTask;

    public LiveFramePump(Dispatcher dispatcher, Func<byte[], BitmapSource> decode, Action<BitmapSource> applyOnUiThread)
    {
        _pumpTask = Task.Run(() => PumpAsync(dispatcher, decode, applyOnUiThread));
    }

    /// <summary>Call from any thread (this is what the network-receive loop calls) — never blocks,
    /// and replaces whatever frame was previously waiting to be decoded.</summary>
    public void Post(byte[] jpegBytes) => _channel.Writer.TryWrite(jpegBytes);

    public void Dispose()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
    }

    private async Task PumpAsync(Dispatcher dispatcher, Func<byte[], BitmapSource> decode, Action<BitmapSource> applyOnUiThread)
    {
        try
        {
            await foreach (var jpegBytes in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                BitmapSource decoded;
                try
                {
                    decoded = decode(jpegBytes);
                }
                catch
                {
                    continue; // a torn/partial frame — skip it, the next one will very likely decode fine
                }

                await dispatcher.InvokeAsync(() => applyOnUiThread(decoded));
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose() was called — normal shutdown, not an error.
        }
    }
}
