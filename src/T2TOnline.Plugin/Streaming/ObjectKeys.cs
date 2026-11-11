#if HAS_INTEROP
using System;
using Il2CppGoaltender;
using Il2CppSkaters;
using Il2CppTape2Tape;
using Il2CppUltimateReplay;
using UnityEngine;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// Stable keys for replay objects: the same string on every PC that loads the same match, so a recording (or a host)
/// can be mapped onto locally loaded objects.
/// ReplayIdentity ids are not stable: prefab clones get new ones in every match.
/// <list type="bullet">
/// <item>skaters: <c>F:H:</c> or <c>F:V:</c> and the forward's asset id (<c>SkaterData.OriginalId</c>; the side matters,
/// both teams can draw from the same pool);</item>
/// <item>referees: <c>R:</c> and the asset id;</item>
/// <item>goalies, by side of the rink (a team switching ends takes the other goalie): <c>G:N</c>, <c>G:S</c>;</item>
/// <item>the puck and its mesh: <c>P:body</c>, <c>P:mesh</c>;</item>
/// <item>anything else: its scene path (<see cref="ObjectRefs.PathOf"/>), stable for scene objects and objects created
/// in a fixed order. Pooled objects (sticks, VFX instances) need spawn records (M3).</item>
/// </list>
/// </summary>
internal static class ObjectKeys
{
    /// <summary>
    /// The object's stable key, or the empty string when it has none. <paramref name="roots"/> answers root ordinals for
    /// a pass over many objects (see <see cref="ObjectRefs.PathOf"/>).
    /// </summary>
    public static string KeyOf(ReplayObject replayObject, RootOrdinals? roots = null)
    {
        try
        {
            GameObject gameObject = replayObject.gameObject;
            if (gameObject.GetComponent<Forward>() is Forward forward && forward != null)
            {
                string id = forward._forwardData?.OriginalId ?? "?";
                if (gameObject.GetComponent<Referee>() != null)
                {
                    return $"R:{id}";
                }

                Team? team = forward._team;
                return $"F:{(team == null ? "?" : team.isHomeTeam ? "H" : "V")}:{id}";
            }

            if (gameObject.GetComponent<GoaltenderNorth>() != null)
            {
                return "G:N";
            }

            if (gameObject.GetComponent<GoaltenderSouth>() != null)
            {
                return "G:S";
            }

            if (gameObject.GetComponent<Puck>() != null)
            {
                return "P:body";
            }

            Transform parent = gameObject.transform.parent;
            if (parent != null && parent.GetComponent<Puck>() != null)
            {
                return "P:mesh";
            }

            return ObjectRefs.PathOf(gameObject.transform, roots);
        }
        catch (Exception)
        {
            // A half-destroyed object: no key, so it maps only within one match.
            return string.Empty;
        }
    }
}
#endif
