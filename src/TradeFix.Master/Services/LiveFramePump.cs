using System.Threading.Channels;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TradeFix.Master.Services;

/// <summary>
/// Mirrors <c>TradeFix.Agent.Services.LiveFramePump</c> — see that class for the full rationale.
/// Master needs the identical fix for its own live self-preview: <c>MasterHost.LocalCaptureFrame</c>
/// used to be handled with a synchronous, blocking <c>Dispatcher.Invoke</c> that decoded the JPEG
/// directly on the UI thread. Since that handler runs from inside the same capture-loop callback
/// that also triggers the network broadcast, blocking it didn't just freeze Master's own preview —
/// it delayed how quickly the next captured frame could even be broadcast to PC2/PC3, compounding
/// the lag fixed in MediaHub and the Agent's own LiveFramePump.
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

    /// <summary>Call from any thread — never blocks, and replaces whatever frame was previously
    /// waiting to be decoded.</summary>
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
