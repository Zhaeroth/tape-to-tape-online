#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>Puts the scenes' patches in (every target is in hooks.txt, unfolded and non-generic) and says which couldn't go in.</summary>
internal static class ScenePatches
{
    /// <summary>Patches the one method <paramref name="type"/>.<paramref name="method"/> with a prefix and/or postfix from <paramref name="owner"/>.</summary>
    public static void Patch(HarmonyLib.Harmony harmony, List<string> failed, Type type, string method, Type owner, string? prefix, string? postfix)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(failed);
        string target = $"{type.Name.Replace("Il2Cpp", string.Empty, StringComparison.Ordinal)}.{method}";
        try
        {
            // One overload only: a name with several would patch the wrong one silently.
            List<MethodInfo> found = AccessTools.GetDeclaredMethods(type).Where(m => m.Name == method).ToList();
            if (found.Count != 1)
            {
                failed.Add($"{target} ({(found.Count == 0 ? "not found" : $"{found.Count} overloads")})");
                return;
            }

            harmony.Patch(
                found[0],
                prefix: prefix == null ? null : new HarmonyMethod(owner.GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)),
                postfix: postfix == null ? null : new HarmonyMethod(owner.GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)));
        }
        catch (Exception ex)
        {
            failed.Add($"{target} ({ModLog.SafeErrorText(ex)})");
        }
    }

    /// <summary>Patches one method with a prefix.</summary>
    public static void Prefix(HarmonyLib.Harmony harmony, List<string> failed, Type type, string method, Type owner, string prefix) =>
        Patch(harmony, failed, type, method, owner, prefix, null);

    /// <summary>Patches one method with a postfix.</summary>
    public static void Postfix(HarmonyLib.Harmony harmony, List<string> failed, Type type, string method, Type owner, string postfix) =>
        Patch(harmony, failed, type, method, owner, null, postfix);
}
#endif
