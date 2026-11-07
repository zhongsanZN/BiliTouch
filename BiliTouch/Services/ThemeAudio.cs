using System.Media;
using System.Windows;
using BiliTouch.Helpers;

namespace BiliTouch.Services;

/// <summary>Short theme sounds use a separate, non-looping channel from video playback.</summary>
public static class ThemeAudio
{
    private static readonly Lazy<SoundPlayer> Like = new(() => new SoundPlayer(
        Application.GetResourceStream(new Uri("pack://application:,,,/BiliTouch;component/Assets/Audio/PhantomBladeZero/like.wav"))!.Stream));
    private static long _lastLike = long.MinValue / 2;

    static ThemeAudio()
    {
        SkinService.Instance.Changed += () => { if (!Enabled) Stop(); };
        if (Application.Current != null) Application.Current.Exit += (_, _) => Stop();
    }

    public static bool Enabled => SkinService.Instance.IsPhantomBlade && SkinService.Instance.EffectsEnabled && !App.MuteForThisRun;

    public static bool PlayLike()
    {
        if (!Enabled || Environment.TickCount64 - _lastLike < 800) return false;
        try
        {
            // The embedded PCM is attenuated to 18% amplitude (-14.9 dB).
            // SoundPlayer.Play is asynchronous and does not touch VLC's volume/track.
            Like.Value.Play();
            _lastLike = Environment.TickCount64;
            return true;
        }
        catch { return false; } // An unavailable sound device must never fail the action.
    }

    private static void Stop()
    {
        if (Like.IsValueCreated) try { Like.Value.Stop(); } catch { }
    }
}
