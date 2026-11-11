using System.Collections.Generic;

namespace T2TOnline.Core.Campaign;

/// <summary>Which of the procedure screen's popups a result's hit shakes.</summary>
public enum NurseResultShake
{
    /// <summary>No hit.</summary>
    None = 0,

    /// <summary>The injury popup (the surgery's result).</summary>
    InjuryPopup = 1,

    /// <summary>The swap popup (the transplant's and the clone's results).</summary>
    SwapPopup = 2,
}

/// <summary>
/// What the game's procedure screen does at a result only once its commit has gone through. A friend's
/// screen never commits, so its popups take their uncommitted branch and leave these out; the friend's glue runs the same game
/// calls itself, once the host's outcome has shown here. Read from the game's popups: <c>BuildTransplantPopup</c>,
/// <c>BuildSurgeryPopup</c>, <c>BuildInjuryPopup</c> with its frozen peas,
/// <c>BuildClonePopup</c>. The other six procedures' results run the same on both (swap, exchange, the
/// sacrifice, the two injury trades and heal all have no gated effect but the added talent's flash, which isn't per procedure).
/// </summary>
public sealed record NurseResultPlan
{
    /// <summary>The game's <c>NurseProcedureState.Procedure.Transplant</c>.</summary>
    public const int Transplant = 0;

    /// <summary>The game's <c>NurseProcedureState.Procedure.ReplaceTalent</c> (the surgery).</summary>
    public const int ReplaceTalent = 1;

    /// <summary>The game's <c>NurseProcedureState.Procedure.FrozenPeas</c>.</summary>
    public const int FrozenPeas = 8;

    /// <summary>The game's <c>NurseProcedureState.Procedure.Cloning</c>.</summary>
    public const int Cloning = 9;

    private static readonly NurseResultPlan Nothing = new();

    private static readonly NurseResultPlan TransplantPlan = new()
    {
        Hit = NurseResultShake.SwapPopup,

        // TransplantAutoCloseDelay (1) and the hit's 0.35, folded by the compiler (verified in the game's code).
        CloseAfterSeconds = 1.35f,
    };

    private static readonly NurseResultPlan SurgeryPlan = new()
    {
        Hit = NurseResultShake.InjuryPopup,
        SurgeryTooltips = true,
    };

    private static readonly NurseResultPlan FrozenPeasPlan = new()
    {
        ProcedureTitle = true,
        HealedIcons = true,
        HealSound = true,
        HealPunch = true,

        // FrozenPeasAutoCloseDelay (1) and 0.45, folded by the compiler (verified in the game's code).
        CloseAfterSeconds = 1.45f,
    };

    private static readonly NurseResultPlan ClonePlan = new()
    {
        Hit = NurseResultShake.SwapPopup,
        CloneCost = true,
        CostPunch = true,

        // The longest of its 0.30, 0.35 and 0.41, and 1 (verified in the game's code; the array's data is in the metadata).
        CloseAfterSeconds = 1.41f,
    };

    /// <summary>The popup the result's hit shakes (its sound, flash and shake: <c>PlayHit</c>).</summary>
    public NurseResultShake Hit { get; init; }

    /// <summary>The surgery's tooltips beside its panel, with every talent it granted (back at each build of its result).</summary>
    public bool SurgeryTooltips { get; init; }

    /// <summary>The popup's title names the procedure: uncommitted, the game's frozen peas popup says no talent was available (at each build).</summary>
    public bool ProcedureTitle { get; init; }

    /// <summary>The panel's injury icons show none: the commit healed them all, and this PC's run copy hasn't changed yet (at each build).</summary>
    public bool HealedIcons { get; init; }

    /// <summary>The heal sound (<c>_healSfx</c>).</summary>
    public bool HealSound { get; init; }

    /// <summary>The healed injury's icon punches (<c>PunchHealedInjuryIcon</c>).</summary>
    public bool HealPunch { get; init; }

    /// <summary>The clone's cost on its source's panel (<c>ShowCloneCost</c>; back at each build of its result).</summary>
    public bool CloneCost { get; init; }

    /// <summary>The source's injury icon punches (<c>PunchInjuryIcon</c>).</summary>
    public bool CostPunch { get; init; }

    /// <summary>Seconds after the result that the screen closes by itself (<c>CloseAfterResultAsync</c>), or 0 when it waits for a press.</summary>
    public float CloseAfterSeconds { get; init; }

    /// <summary>True when the result has an effect that only a commit runs.</summary>
    public bool Any => this != Nothing;

    /// <summary>The effects of <paramref name="procedure"/>'s result (the game's procedure number); none for any other.</summary>
    /// <param name="procedure">The procedure's number.</param>
    /// <returns>What its result does once committed.</returns>
    public static NurseResultPlan For(int procedure) => procedure switch
    {
        Transplant => TransplantPlan,
        ReplaceTalent => SurgeryPlan,
        FrozenPeas => FrozenPeasPlan,
        Cloning => ClonePlan,
        _ => Nothing,
    };

    /// <summary>The effects in words, for the log.</summary>
    /// <returns>For example "hit, closes by itself in 1.35 s".</returns>
    public override string ToString()
    {
        var parts = new List<string>(6);
        if (Hit != NurseResultShake.None)
        {
            parts.Add("hit");
        }

        if (SurgeryTooltips)
        {
            parts.Add("surgery tooltips");
        }

        if (ProcedureTitle)
        {
            parts.Add("the procedure's title");
        }

        if (HealedIcons)
        {
            parts.Add("injury icons healed");
        }

        if (HealSound)
        {
            parts.Add("heal sound");
        }

        if (HealPunch)
        {
            parts.Add("heal punch");
        }

        if (CloneCost)
        {
            parts.Add("the clone's cost");
        }

        if (CostPunch)
        {
            parts.Add("cost punch");
        }

        if (CloseAfterSeconds > 0)
        {
            parts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"closes by itself in {CloseAfterSeconds:0.00} s"));
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "nothing";
    }
}
