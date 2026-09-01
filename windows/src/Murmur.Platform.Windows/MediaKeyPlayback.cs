using System.Diagnostics;
using System.Runtime.InteropServices;
using Murmur.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Murmur.Platform.Windows;

/// <summary>
/// Pauses whatever is playing while dictation runs, and starts it again afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists:</b> a laptop microphone hears the laptop speakers. Music playing during
/// dictation is transcribed along with the speech, and nothing downstream can tell a lyric
/// from a word the user meant.
/// </para>
/// <para>
/// <b>Why the media key and not an API call:</b> Windows has no supported way to tell an
/// arbitrary application to pause. The media transport key is the one signal every player
/// already listens for — Spotify, browsers, VLC, the lot — and it needs no permission and no
/// per-app integration.
/// </para>
/// <para>
/// <b>The danger, and the guard:</b> that key is a <i>toggle</i>. Sent blind, it starts music
/// on a machine where the user was enjoying silence, which is a far worse bug than failing to
/// pause. So it is only sent when the audio engine says something is actually rendering:
/// WASAPI is asked for the session state of every process playing on the default output, and
/// this app's own capture is excluded. Playback that leaves nothing rendering — a paused
/// video, a tab with no sound — reads as silence, and nothing is sent.
/// </para>
/// <para>
/// <b>ponytail: a toggle, not a transport.</b> If the user pauses their music by hand during
/// an utterance, the resume at key-up starts it again. Fixing that properly means
/// GlobalSystemMediaTransportControlsSessionManager, which has real Play/Pause commands and
/// per-session state — and which would drag this assembly onto a Windows-SDK target framework.
/// Worth doing if the toggle turns out to annoy in practice.
/// </para>
/// </remarks>
public sealed class MediaKeyPlayback : IMediaPlayback, IDisposable
{
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// Time for a player to act on the key before capture starts.
    /// </summary>
    /// <remarks>
    /// Spotify and browser media stacks act on the key asynchronously. Without a beat here the
    /// microphone opens while the last moment of audio is still coming out of the speakers,
    /// which is the exact thing this class exists to prevent. Short enough not to be felt as
    /// hotkey lag.
    /// </remarks>
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(120);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, IntPtr extraInfo);

    private readonly MMDeviceEnumerator _devices = new();

    /// <inheritdoc />
    public bool TryPause()
    {
        if (!IsAnythingPlaying()) return false;

        Send();

        // Deliberately blocking. This is called from the engine before it opens the
        // microphone, and the whole point is that the speakers are quiet by the time it does.
        Thread.Sleep(SettleTime);
        return true;
    }

    /// <inheritdoc />
    public void ResumePlayback() => Send();

    /// <summary>
    /// Whether any other process is currently rendering audio on the default output.
    /// </summary>
    /// <remarks>
    /// <c>AudioSessionState.Active</c> means the session is pushing samples right now, which
    /// is a stronger and more useful signal than "a player is open". A muted-but-playing
    /// session still counts: it is playing, the user just cannot hear it, and pausing then
    /// resuming leaves it exactly as it was.
    /// </remarks>
    private bool IsAnythingPlaying()
    {
        try
        {
            using var device = _devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            if (sessions is null) return false;

            var self = Environment.ProcessId;

            for (var i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if (session.State != AudioSessionState.AudioSessionStateActive) continue;

                // Our own process would otherwise count as playback the moment the app makes
                // any sound of its own.
                if (session.GetProcessID == self) continue;

                return true;
            }
        }
        catch (COMException)
        {
            // No output device, or one was pulled out mid-call. Silence is the safe answer:
            // it means nothing is sent, which is the failure that costs the user nothing.
        }

        return false;
    }

    private static void Send()
    {
        // Extended-key flag: the media keys live in the extended set, and players that read
        // the scan code rather than the virtual key ignore the event without it.
        keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_EXTENDEDKEY, IntPtr.Zero);
        keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, IntPtr.Zero);
    }

    /// <inheritdoc />
    public void Dispose() => _devices.Dispose();
}
