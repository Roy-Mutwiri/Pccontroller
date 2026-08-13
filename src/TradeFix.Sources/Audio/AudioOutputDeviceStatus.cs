using NAudio.CoreAudioApi;

namespace TradeFix.Sources.Audio;

/// <summary>
/// One-shot probe of the default audio OUTPUT device's state. WASAPI loopback capture (see
/// <see cref="AudioCaptureService"/>) records what the device actually renders — post
/// volume/mute — so a muted or near-zero-volume Master PC broadcasts genuine silence to every
/// node while looking perfectly healthy in every log. This is deliberately a deterministic
/// device-state check, not an amplitude heuristic: "the content happens to be quiet right now"
/// must not trigger a false "you're muted" warning mid-show.
/// </summary>
public static class AudioOutputDeviceStatus
{
    public enum State
    {
        Ok,
        Muted,
        VolumeVeryLow,
        NoDevice,
        Unknown
    }

    public static State Probe()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var volume = device.AudioEndpointVolume;
            if (volume.Mute)
            {
                return State.Muted;
            }

            return volume.MasterVolumeLevelScalar < 0.05f ? State.VolumeVeryLow : State.Ok;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return State.NoDevice; // no default render endpoint exists on this PC right now
        }
        catch
        {
            return State.Unknown;
        }
    }
}
