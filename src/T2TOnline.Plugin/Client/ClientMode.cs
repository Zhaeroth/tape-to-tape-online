#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Hooks;
using UnityEngine;

namespace T2TOnline.Plugin.Client;

/// <summary>
/// Switches the match's own gameplay off from outside, so a stream can drive it, as a friend's PC will in M3:
/// <list type="bullet">
/// <item>physics goes to Script mode and is never stepped, so no collision, trigger or force happens locally;</item>
/// <item>every [block] method in clientmode.txt returns early;</item>
/// <item>components whose methods are folded ([disable]) are switched off instead, on inactive objects too (the stream
/// may show them later), and on objects made for the stream (<see cref="DisableFoldedOn"/>);</item>
/// <item>save writes ([saves]) are refused by <see cref="SaveGuard"/>, so this PC's own saves are never touched.</item>
/// </list>
/// The leak audit: every call is counted. Held calls are expected (Unity still calls those methods every frame). A
/// leak is gameplay reaching an outlet anyway: a refused save write, or what the presentation channels report through
/// <see cref="CountLeak"/> (an event raised locally that presentation code doesn't raise, a local time-scale write).
/// [audit] methods run and are listed so they can be classified. A clean audit has no leaks.
/// </summary>
/// <remarks>
/// The patches go in the first time client mode starts, and stay for the rest of the game session: when client mode is
/// off they let every call through at once. They aren't taken out when an online session ends because HarmonyX can't
/// take an IL2CPP patch out: unpatching re-detours the method to a fresh copy with no patches (Il2CppInterop's
/// <c>Il2CppDetourMethodPatcher.DetourTo</c>), so calls still go through a managed wrapper, briefly run unhooked while
/// the detour is swapped, and leave a generated method and a managed detour behind each time. A PC that never enters
/// client mode (a host) never carries them. Nothing here throws into the game; a prefix that fails lets the original
/// run.
/// </remarks>
internal static class ClientMode
{
    private static readonly Dictionary<MethodBase, long> Calls = new();
    private static readonly Dictionary<(MethodBase Method, string Files), long> SavesRefusedBefore = new();
    private static readonly Dictionary<MethodBase, ClientModeAction> Actions = new();
    private static readonly Dictionary<string, long> NamedLeaks = new(StringComparer.Ordinal);
    private static readonly List<(Behaviour Component, bool WasEnabled)> Disabled = new();
    private static readonly List<Type> DisableTypes = new();
    private static HarmonyLib.Harmony? _harmony;

    // Null until the first Enter installs the patches; then empty, or why client mode can't be trusted (for good: the
    // patches that did go in stay, but a second Enter must not skip the check).
    private static string? _installProblem;
    private static SimulationMode _physicsMode;
    private static float _enteredAt;

    /// <summary>True while the match's gameplay is switched off.</summary>
    public static bool Active { get; private set; }

    /// <summary>Methods patched, from the last successful install.</summary>
    public static int PatchedMethods { get; private set; }

    /// <summary>Game calls held or watched this game session, all client-mode runs together (for a rate in reports).</summary>
    public static long HeldCalls { get; private set; }

    /// <summary>
    /// Switches gameplay off. False, with the reason, if client mode can't be trusted (the list didn't load or too few
    /// methods could be patched); then nothing is changed.
    /// </summary>
    public static bool Enter(out string problem)
    {
        problem = string.Empty;
        if (Active)
        {
            return true;
        }

        if (_installProblem == null)
        {
            Install(out string installProblem);
            _installProblem = installProblem;
        }

        if (_installProblem.Length > 0)
        {
            problem = _installProblem;
            return false;
        }

        if (!SaveGuard.Install(out string saveProblem))
        {
            problem = saveProblem;
            return false;
        }

        Calls.Clear();
        NamedLeaks.Clear();
        SavesRefusedBefore.Clear();
        foreach (((MethodBase, string) write, long count) in SaveGuard.RefusedByFilesSoFar())
        {
            SavesRefusedBefore[write] = count;
        }

        // The two engine calls first: if one fails, nothing is held that only Exit (which needs Active) would let go.
        _physicsMode = Physics.simulationMode;
        Physics.simulationMode = SimulationMode.Script;
        SaveGuard.HoldForClientMode(true);
        DisableFoldedComponents();
        Active = true;
        _enteredAt = Time.realtimeSinceStartup;
        ModLog.Info($"Client mode on: physics in Script mode, {PatchedMethods} game methods held, {Disabled.Count} folded components switched off, " +
            "save writes refused. The leak audit counts every call into gameplay from now on.");
        return true;
    }

    /// <summary>Switches gameplay back on and writes the leak audit.</summary>
    public static void Exit(string reason)
    {
        if (!Active)
        {
            return;
        }

        Active = false;
        SaveGuard.HoldForClientMode(false);
        foreach ((Behaviour component, bool wasEnabled) in Disabled)
        {
            try
            {
                if (component != null)
                {
                    component.enabled = wasEnabled;
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Client mode: could not re-enable a component: {ModLog.SafeErrorText(ex)}");
            }
        }

        Disabled.Clear();
        Physics.simulationMode = _physicsMode;
        Physics.SyncTransforms();
        ReportAudit(reason);
    }

    /// <summary>
    /// Switches off the [disable] components on <paramref name="root"/> and its children, inactive ones included (an object
    /// made for the stream after client mode started), and puts them back at <see cref="Exit"/>. Never throws.
    /// </summary>
    public static void DisableFoldedOn(GameObject root)
    {
        if (!Active || root == null)
        {
            return;
        }

        foreach (Type type in DisableTypes)
        {
            try
            {
                foreach (Component found in root.GetComponentsInChildren(Il2CppType.From(type), true))
                {
                    Disable(found);
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Client mode: could not switch off {type.Name} components on {LogNames.ObjectName(root)}: {ModLog.SafeErrorText(ex)}");
            }
        }
    }

    /// <summary>Counts a leak found outside the patched methods (a channel's report). Call before <see cref="Exit"/>.</summary>
    public static void CountLeak(string what, long count)
    {
        if (Active && count > 0)
        {
            NamedLeaks[what] = NamedLeaks.GetValueOrDefault(what) + count;
        }
    }

    private static bool Install(out string problem)
    {
        IReadOnlyList<ClientModeRule> rules;
        try
        {
            rules = ClientModeList.LoadEmbedded();
        }
        catch (Exception ex)
        {
            problem = $"clientmode.txt didn't load: {ModLog.SafeErrorText(ex)}";
            return false;
        }

        _harmony ??= new HarmonyLib.Harmony("t2tonline.clientmode");
        var failures = new List<string>();
        int patched = 0;
        foreach (ClientModeRule rule in rules)
        {
            Type? type = Il2CppTypeLocator.Find(rule.Entry.TypeName);
            if (type == null)
            {
                failures.Add($"{rule.Entry} (type not found)");
                continue;
            }

            // Save writes are SaveGuard's (it refuses them outside client mode too).
            if (rule.Action is ClientModeAction.Presentation or ClientModeAction.Save)
            {
                continue;
            }

            if (rule.Action == ClientModeAction.Disable)
            {
                if (!DisableTypes.Contains(type))
                {
                    DisableTypes.Add(type);
                }

                continue;
            }

            List<MethodInfo> methods = AccessTools.GetDeclaredMethods(type).Where(m => m.Name == rule.Entry.MemberName).ToList();
            if (methods.Count == 0)
            {
                failures.Add($"{rule.Entry} (method not found)");
                continue;
            }

            foreach (MethodInfo method in methods)
            {
                try
                {
                    _harmony.Patch(method, prefix: new HarmonyMethod(PrefixFor(rule.Action)));
                    Actions[method] = rule.Action;
                    patched++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{rule.Entry} ({ModLog.SafeErrorText(ex)})");
                }
            }
        }

        PatchedMethods = patched;
        if (failures.Count > 0)
        {
            ModLog.Warn($"Client mode: {failures.Count} entries couldn't be held, so those methods keep running (the audit can't count them): " +
                string.Join("; ", failures.Take(20)) + (failures.Count > 20 ? "; ..." : string.Empty));
        }

        // A client mode missing a large part of its list would let gameplay run against the stream: refuse it.
        int wanted = rules.Count(r => r.Action is ClientModeAction.Block or ClientModeAction.Audit);
        if (patched < wanted * 9 / 10)
        {
            problem = $"only {patched} of {wanted} methods could be held";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private static MethodInfo PrefixFor(ClientModeAction action) => action switch
    {
        ClientModeAction.Audit => typeof(ClientMode).GetMethod(nameof(AuditPrefix), BindingFlags.NonPublic | BindingFlags.Static)!,
        _ => typeof(ClientMode).GetMethod(nameof(BlockPrefix), BindingFlags.NonPublic | BindingFlags.Static)!,
    };

    private static bool BlockPrefix(MethodBase __originalMethod)
    {
        if (!Active)
        {
            return true;
        }

        Calls[__originalMethod] = Calls.GetValueOrDefault(__originalMethod) + 1;
        HeldCalls++;
        return false;
    }

    private static void AuditPrefix(MethodBase __originalMethod)
    {
        if (Active)
        {
            Calls[__originalMethod] = Calls.GetValueOrDefault(__originalMethod) + 1;
            HeldCalls++;
        }
    }

    private static void DisableFoldedComponents()
    {
        foreach (Type type in DisableTypes)
        {
            try
            {
                // Inactive objects too: one the stream shows later would run its folded per-frame method from then on.
                foreach (UnityEngine.Object found in UnityEngine.Object.FindObjectsByType(Il2CppType.From(type), FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    Disable(found);
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Client mode: could not switch off {type.Name} components: {ModLog.SafeErrorText(ex)}");
            }
        }
    }

    /// <summary>Switches one component off, remembering how it was (once: a component already switched off here stays as listed).</summary>
    private static void Disable(UnityEngine.Object found)
    {
        Behaviour? behaviour = found.TryCast<Behaviour>();
        if (behaviour != null && !Disabled.Exists(d => d.Component != null && d.Component.Pointer == behaviour.Pointer))
        {
            Disabled.Add((behaviour, behaviour.enabled));
            behaviour.enabled = false;
        }
    }

    private static void ReportAudit(string reason)
    {
        float seconds = Time.realtimeSinceStartup - _enteredAt;
        var blocked = Calls.Where(c => Actions.GetValueOrDefault(c.Key) == ClientModeAction.Block).OrderByDescending(c => c.Value).ToList();
        var audited = Calls.Where(c => Actions.GetValueOrDefault(c.Key) == ClientModeAction.Audit).OrderByDescending(c => c.Value).ToList();
        // Refused writes of the game's settings, profile and stats are the guard's job (the match's load writes its settings,
        // a reward menu shown as a view writes the profile); a run file's or another file's are leaks.
        var saves = SaveGuard.RefusedByFilesSoFar()
            .Select(c => (c.Key.Method, c.Key.Files, Count: c.Value - SavesRefusedBefore.GetValueOrDefault(c.Key), Kind: SaveRefusals.Classify(c.Key.Files.Split(", "))))
            .Where(c => c.Count > 0)
            .OrderByDescending(c => c.Count)
            .ToList();
        long expected = saves.Where(c => c.Kind == RefusalKind.Expected).Sum(c => c.Count);
        long leaks = saves.Where(c => c.Kind != RefusalKind.Expected).Sum(c => c.Count) + NamedLeaks.Values.Sum();
        string expectedFiles = string.Join(", ", saves.Where(c => c.Kind == RefusalKind.Expected).Select(c => c.Files).Distinct(StringComparer.OrdinalIgnoreCase));
        ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Client mode off ({reason}) after {seconds:0.0} s. Leak audit: {(leaks == 0 ? "clean, no gameplay got past client mode" : $"{leaks} LEAKS")}") +
            $"{(expected > 0 ? $" ({expected} save write(s) of {expectedFiles} refused, as expected)" : string.Empty)}. " +
            $"Held as expected: {blocked.Count} gameplay methods ({blocked.Sum(c => c.Value)} calls). Watched (to classify): {audited.Count} ran.");
        foreach ((MethodBase method, string files, long count, RefusalKind kind) in saves)
        {
            string what = files.Length > 0 ? files : "no file named";
            if (kind == RefusalKind.Expected)
            {
                ModLog.Info($"  refused {Name(method)}: {count} save write(s) of {what} (the save guard's job: nothing was written)");
            }
            else
            {
                ModLog.Warn($"  LEAK   {Name(method)}: {count} save write(s) of {what} refused{(kind == RefusalKind.Run ? " (run code ran in client mode)" : string.Empty)}");
            }
        }

        foreach ((string what, long count) in NamedLeaks.OrderByDescending(c => c.Value))
        {
            ModLog.Warn($"  LEAK   {what}: {count}");
        }

        foreach ((MethodBase method, long count) in audited)
        {
            ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"  ran    {Name(method)}: {count} calls ({count / Math.Max(seconds, 0.001):0.0}/s)"));
        }

        foreach ((MethodBase method, long count) in blocked.Take(20))
        {
            ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"  held   {Name(method)}: {count} calls ({count / Math.Max(seconds, 0.001):0.0}/s)"));
        }
    }

    private static string Name(MethodBase method) => $"{method.DeclaringType?.FullName?.Replace("Il2Cpp", string.Empty, StringComparison.Ordinal)}::{method.Name}";
}
#endif
