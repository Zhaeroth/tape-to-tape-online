namespace T2TOnline.Steam.Tests;

/// <summary>
/// Tests that use <see cref="SteamRuntime"/>'s process-wide probe (directly, or through the public Steam calls). They run
/// one at a time, after the parallel tests, because the probe tests swap the probe's export lookup.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedSteamRuntime
{
    public const string Name = "SteamRuntime";
}
