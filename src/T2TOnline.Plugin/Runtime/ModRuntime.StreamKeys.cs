#if HAS_INTEROP
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Runtime;

// The developer keys F3, F4 and F2 as methods: the keys call them (Update), and so does the replay check tour
// (IStreamKeys, NativeLobby/LobbyUiCheck.Replay.cs), which never sends a key press. While any check tour runs, F3 records into
// that tour's own folder and F2 looks there (LobbyUiCheck.TourStreamsFolder), where a tour's hosted matches go too when it records
// them (Online/MatchRelay.cs): F3 prunes the folder it records into (DevOutput.PruneStreams), and a tour writes nothing outside its own folder.
internal sealed partial class ModRuntime : IStreamKeys
{
    /// <summary>F2's outcome (<see cref="StreamKeysState.RehearsalOutcome"/>) when the loaded match is the recording's exactly.</summary>
    internal const string RehearsalExact = "the loaded match has exactly the recording's setup";

    private string? _rehearseOnly;
    private string? _rehearsalOutcome;

    /// <inheritdoc />
    public StreamKeysState State => new(_streaming.Recording, _streaming.Playing, _rehearsal != null, _streaming.FilePath,
        _streaming.Player.Totals.Frames, _streaming.Player.FailedStates, _streaming.Player.StopReason, _rehearsalOutcome);

    /// <summary>Where F3 records and F2 looks for recordings: a running check tour's own folder, else <c>dev\streams</c>.</summary>
    private string StreamDirectory => NativeLobby.LobbyUiCheck.TourStreamsFolder ?? _streamDirectory;

    /// <inheritdoc />
    public void PressF3()
    {
        // A developer key: never live without the developer tools, whoever calls it (the tours run only with them too).
        if (_devTools)
        {
            Safely("Stream recorder (F3)", ToggleRecording);
        }
    }

    /// <inheritdoc />
    public void PressF4()
    {
        if (!_devTools)
        {
            return;
        }

        // F4 and F11 both take over the match (physics mode, time scale): never both at once.
        if (_frozenReplay.Active)
        {
            ModLog.Warn("Stream player (F4): the F11 replay is running; stop it first (F11).");
        }
        else
        {
            Safely("Stream player (F4)", TogglePlayback);
        }
    }

    /// <inheritdoc />
    public void PressF2()
    {
        if (_devTools)
        {
            // A press that may start one: what it comes to is its own, not the last one's.
            if (_rehearsal == null && !_streaming.Playing)
            {
                _rehearsalOutcome = null;
            }

            Safely("Fresh-match rehearsal (F2)", ToggleRehearsal);
        }
    }

    /// <inheritdoc />
    public void PressF2On(string path)
    {
        _rehearseOnly = path;
        try
        {
            PressF2();
        }
        finally
        {
            _rehearseOnly = null;
        }
    }

    /// <summary>The recording at <paramref name="path"/>, loaded, when it has a match setup and component ids (F2 on one named recording).</summary>
    private static string? OneRecording(string path, out MatchRecording? stream, out string problem)
    {
        stream = null;
        if (!MatchRecording.TryLoad(path, out MatchRecording? loaded, out string why))
        {
            problem = $"{DevOutput.Label(path)} can't be read ({why}).";
            return null;
        }

        if (loaded.Setup.Count == 0 || !loaded.HasComponentIds)
        {
            problem = $"{DevOutput.Label(path)} has no match setup or component ids (made before v0.4).";
            return null;
        }

        stream = loaded;
        problem = string.Empty;
        return path;
    }
}
#endif
