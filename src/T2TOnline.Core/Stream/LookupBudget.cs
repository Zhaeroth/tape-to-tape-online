using System;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Stream;

/// <summary>What a new lookup of an event argument's object costs a friend's PC (<see cref="LookupBudget"/>).</summary>
public enum LookupKind
{
    /// <summary>
    /// A <c>go:</c> path in a scene the scene manager lists: one look at that scene's roots and a walk down the named
    /// children. Cheap (tens of microseconds).
    /// </summary>
    ScenePath,

    /// <summary>
    /// A scan of every loaded object of a type: an <c>asset:</c> name, or a <c>go:</c> path in a scene the scene manager
    /// doesn't list (<c>DontDestroyOnLoad</c>, found through every transform). Milliseconds each.
    /// </summary>
    Scan,
}

/// <summary>
/// How many new object lookups a friend's PC runs for the host's event arguments, by cost. The texts come from the
/// network, so lookups are bounded; but a lookup held back is an argument the game's listener gets as null, which the
/// host's never was (a puck collision's hit object held back right after a goal makes
/// <c>CollisionVFX.Instantiate</c> throw, and the hit's effect is missing). So:
/// <list type="bullet">
/// <item>scene paths, cheap, get <see cref="ScenePathsPerSecond"/> a second and <see cref="ScenePathsPerFrame"/> a frame;</item>
/// <item>scans, expensive, keep the old <see cref="ScansPerSecond"/> a second;</item>
/// <item>an urgent lookup (one a listener dereferences at once, such as a collision's colliders and bodies) isn't held to
/// either kind's budget, only to <see cref="UrgentPerFrame"/> a frame, a bound a flood of made-up texts can't get
/// past.</item>
/// </list>
/// Pure logic, no clock of its own: the caller passes the frame's time and number. Never throws.
/// </summary>
public sealed class LookupBudget
{
    /// <summary>New scene-path lookups a second.</summary>
    public const int ScenePathsPerSecond = 64;

    /// <summary>New scene-path lookups a frame.</summary>
    public const int ScenePathsPerFrame = 16;

    /// <summary>New scans a second (as the single budget was before).</summary>
    public const int ScansPerSecond = 8;

    /// <summary>Urgent lookups a frame, whatever their kind.</summary>
    public const int UrgentPerFrame = 32;

    private const string ScenePrefix = "go:";
    private const string AssetPrefix = "asset:";

    private readonly Counter _paths = new(ScenePathsPerSecond, ScenePathsPerFrame);
    private readonly Counter _scans = new(ScansPerSecond, int.MaxValue);
    private int _urgentFrame = int.MinValue;
    private int _urgentInFrame;

    /// <summary>Scene-path lookups run.</summary>
    public long ScenePaths => _paths.Taken;

    /// <summary>Scene-path lookups held back by the budget.</summary>
    public long ScenePathsHeldBack => _paths.HeldBack;

    /// <summary>Scans run.</summary>
    public long Scans => _scans.Taken;

    /// <summary>Scans held back by the budget.</summary>
    public long ScansHeldBack => _scans.HeldBack;

    /// <summary>Urgent lookups run outside their kind's budget (they count in its totals too).</summary>
    public long Urgent { get; private set; }

    /// <summary>Urgent lookups held back by <see cref="UrgentPerFrame"/>.</summary>
    public long UrgentHeldBack { get; private set; }

    /// <summary>Every lookup held back, of any kind.</summary>
    public long HeldBack => _paths.HeldBack + _scans.HeldBack + UrgentHeldBack;

    /// <summary>
    /// The kind of lookup <paramref name="text"/> (a reference's text) needs: <see cref="LookupKind.ScenePath"/> for a
    /// <c>go:</c> path in a scene <paramref name="sceneListed"/> says the scene manager lists, else <see cref="LookupKind.Scan"/>.
    /// <paramref name="sceneListed"/> is asked only for a <c>go:</c> path, with its scene's name.
    /// </summary>
    public static LookupKind KindOf(string? text, Func<string, bool> sceneListed)
    {
        ArgumentNullException.ThrowIfNull(sceneListed);
        if (text == null || !text.StartsWith(ScenePrefix, StringComparison.Ordinal))
        {
            return LookupKind.Scan;
        }

        int slash = text.IndexOf('/', ScenePrefix.Length);
        string scene = slash < 0 ? text[ScenePrefix.Length..] : text[ScenePrefix.Length..slash];
        return scene.Length > 0 && sceneListed(scene) ? LookupKind.ScenePath : LookupKind.Scan;
    }

    /// <summary>True for a value that names an object on the other PC (a replay object's id, or a reference's text).</summary>
    public static bool IsReference(NetValue value) =>
        value.Kind == NetValueKind.Reference ||
        (value.Kind == NetValueKind.Text && value.Text is string text &&
            (text.StartsWith(ScenePrefix, StringComparison.Ordinal) || text.StartsWith(AssetPrefix, StringComparison.Ordinal) ||
             text.StartsWith("player:", StringComparison.Ordinal)));

    /// <summary>
    /// Whether one new lookup of <paramref name="kind"/> may run now (<paramref name="nowSeconds"/>, frame
    /// <paramref name="frame"/>); counted either way. <paramref name="urgent"/>: a listener needs the object at once (see
    /// the class remarks).
    /// </summary>
    public bool TryTake(LookupKind kind, double nowSeconds, int frame, bool urgent)
    {
        Counter counter = kind == LookupKind.ScenePath ? _paths : _scans;
        if (!urgent)
        {
            return counter.TryTake(nowSeconds, frame);
        }

        if (frame != _urgentFrame)
        {
            _urgentFrame = frame;
            _urgentInFrame = 0;
        }

        if (_urgentInFrame >= UrgentPerFrame)
        {
            UrgentHeldBack++;
            return false;
        }

        _urgentInFrame++;
        Urgent++;
        counter.Spend(nowSeconds, frame);
        return true;
    }

    /// <summary>"scene paths 41 (held back 0), scans 3 (held back 0), urgent 12" for a playback's summary.</summary>
    public string Describe() =>
        $"scene paths {ScenePaths} (held back {ScenePathsHeldBack}), scans {Scans} (held back {ScansHeldBack})" +
        (Urgent > 0 || UrgentHeldBack > 0 ? $", {Urgent} urgent outside the budget{(UrgentHeldBack > 0 ? $" ({UrgentHeldBack} held back at {UrgentPerFrame} a frame)" : string.Empty)}" : string.Empty);

    /// <summary>Forgets everything (a new playback).</summary>
    public void Clear()
    {
        _paths.Clear();
        _scans.Clear();
        _urgentFrame = int.MinValue;
        _urgentInFrame = 0;
        Urgent = UrgentHeldBack = 0;
    }

    /// <summary>One kind's budget: a one-second window and a per-frame cap.</summary>
    private sealed class Counter
    {
        private readonly int _perSecond;
        private readonly int _perFrame;
        private double _windowStart = double.NegativeInfinity;
        private int _inWindow;
        private int _frame = int.MinValue;
        private int _inFrame;

        public Counter(int perSecond, int perFrame)
        {
            _perSecond = perSecond;
            _perFrame = perFrame;
        }

        public long Taken { get; private set; }

        public long HeldBack { get; private set; }

        public bool TryTake(double now, int frame)
        {
            Roll(now, frame);
            if (_inWindow >= _perSecond || _inFrame >= _perFrame)
            {
                HeldBack++;
                return false;
            }

            _inWindow++;
            _inFrame++;
            Taken++;
            return true;
        }

        /// <summary>An urgent lookup of this kind: counted against the window (later ordinary ones wait for it), never refused.</summary>
        public void Spend(double now, int frame)
        {
            Roll(now, frame);
            _inWindow++;
            _inFrame++;
            Taken++;
        }

        public void Clear()
        {
            _windowStart = double.NegativeInfinity;
            _inWindow = _inFrame = 0;
            _frame = int.MinValue;
            Taken = HeldBack = 0;
        }

        private void Roll(double now, int frame)
        {
            if (!(now - _windowStart < 1.0))
            {
                _windowStart = now;
                _inWindow = 0;
            }

            if (frame != _frame)
            {
                _frame = frame;
                _inFrame = 0;
            }
        }
    }
}
