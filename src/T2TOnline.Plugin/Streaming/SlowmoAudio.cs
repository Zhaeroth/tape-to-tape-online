#if HAS_INTEROP
using System;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// The slow-motion audio filter on a friend's PC: an FMOD snapshot (<c>SoundManager.StartSlowmoSnapshot</c>) that the
/// host's game starts next to its slow motion (the brawl finisher, a charged slapshot) in code client mode holds.
/// <list type="bullet">
/// <item>A host from this version on says whether its own snapshot is playing (the HUD value <c>audio.slowmo</c>), and
/// that's followed exactly.</item>
/// <item>Until the host has said so (an older host, or a recording), it's guessed from the stream's time scale: on once
/// the scale has held steady in slow motion (above 0, below 1) for a few snapshots, so a pause menu's fade or a tween
/// passing through doesn't set it off; off at once when the scale leaves slow motion. A pause (time scale 0) isn't slow
/// motion.</item>
/// </list>
/// </summary>
internal sealed class SlowmoAudio
{
    private const int SteadySnapshots = 3;
    private const float SteadyBy = 0.02f;

    private bool _on;
    private float _lastScale = 1f;
    private int _steady;

    /// <summary>Times it was started this playback.</summary>
    public int Started { get; private set; }

    /// <summary>True once the host has said whether its own filter plays (the guess is off from then on).</summary>
    public bool FollowsHost { get; private set; }

    /// <summary>Forgets the stream (a new playback).</summary>
    public void Reset()
    {
        _on = false;
        _lastScale = 1f;
        _steady = 0;
        Started = 0;
        FollowsHost = false;
    }

    /// <summary>The host's own flag: whether its slow-motion audio is playing.</summary>
    public void FromHost(bool playing)
    {
        FollowsHost = true;
        Set(playing);
    }

    /// <summary>A new snapshot interval with this time scale: the guess, until the host says.</summary>
    public void FromTimeScale(float scale)
    {
        if (FollowsHost)
        {
            return;
        }

        bool slow = scale is > 0.01f and < 0.99f;
        _steady = slow && Math.Abs(scale - _lastScale) <= SteadyBy ? _steady + 1 : 0;
        _lastScale = scale;
        if (!slow)
        {
            Set(false);
        }
        else if (_steady >= SteadySnapshots)
        {
            Set(true);
        }
    }

    /// <summary>Stops it (playback ending).</summary>
    public void Stop() => Set(false);

    /// <summary>For the stop log.</summary>
    public string Describe() =>
        $"slow-motion audio {Started} times ({(FollowsHost ? "following the host's own" : "guessed from the time scale; the host didn't say")})";

    private void Set(bool on)
    {
        if (on == _on)
        {
            return;
        }

        try
        {
            Il2Cpp.SoundManager? sound = Il2Cpp.SoundManager.Instance;
            if (sound == null)
            {
                return;
            }

            if (on)
            {
                sound.StartSlowmoSnapshot();
                Started++;
            }
            else
            {
                sound.StopSlowmoSnapshot();
            }

            _on = on;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Stream player: couldn't {(on ? "start" : "stop")} the slow-motion audio: {ModLog.SafeErrorText(ex)}");
            _on = false;
        }
    }
}
#endif
