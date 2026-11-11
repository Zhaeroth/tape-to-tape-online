using System;
using System.Runtime.InteropServices;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>
/// <c>ISteamNetworkingUtils</c>: relay network access, Steam's local clock and configuration (ICE, fake network
/// conditions). None of these throw; they return false, 0 or <see cref="SteamNetworkingAvailability.Unknown"/> when
/// Steam isn't available.
/// <para>
/// The mod sets ICE once at start-up through <see cref="SetIce"/> and reads it back with <see cref="TryGetIce"/> (relay
/// only unless the player allows direct connections, D133). Not called by the mod (kept for later tuning), so never run
/// against the real DLL, only tested without Steam: the general <c>SetConfig</c> overloads and <see cref="EnableIce"/>.
/// </para>
/// </summary>
public static class SteamNetworking
{
    /// <summary>
    /// Starts relay network access (fetching the network config, pinging relays) now rather than on the first
    /// connection, so ping locations are ready sooner. Cheap to call more than once.
    /// </summary>
    public static bool InitRelayNetworkAccess()
    {
        nint self = SteamInterfaces.NetworkingUtils();
        if (self == 0)
        {
            return false;
        }

        try
        {
            SteamNative.InitRelayNetworkAccess(self);
            return true;
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>The relay network's overall availability, with details in <paramref name="status"/> (zeroed on failure).</summary>
    public static SteamNetworkingAvailability GetRelayNetworkStatus(out SteamRelayNetworkStatus status)
    {
        status = default;
        nint self = SteamInterfaces.NetworkingUtils();
        if (self == 0)
        {
            return SteamNetworkingAvailability.Unknown;
        }

        try
        {
            return SteamNative.GetRelayNetworkStatus(self, out status);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            status = default;
            return SteamNetworkingAvailability.Unknown;
        }
    }

    /// <summary>
    /// Steam's local monotonic clock in microseconds, the clock of <see cref="SteamNetworkingMessage.TimeReceived"/>;
    /// 0 when Steam isn't available.
    /// </summary>
    public static long GetLocalTimestamp()
    {
        nint self = SteamInterfaces.NetworkingUtils();
        if (self == 0)
        {
            return 0;
        }

        try
        {
            return SteamNative.GetLocalTimestamp(self);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return 0;
        }
    }

    /// <summary>
    /// Shares the given ICE candidate types (<see cref="SteamIceCandidates"/>) on new P2P sessions, so Steam can pick a
    /// direct route when it beats the relays. Set before the first session; existing sessions keep their setting.
    /// </summary>
    public static bool EnableIce(int candidateTypes = SteamIceCandidates.All) => SetIce(candidateTypes);

    /// <summary>
    /// Sets the ICE candidate types new P2P sessions may use (<see cref="SteamIceCandidates"/>): <see cref="SteamIceCandidates.Disable"/>
    /// is relay only (no ICE work, no address shared with the peer). A global value: every connection made afterwards,
    /// including the ones <c>ISteamNetworkingMessages</c> opens for its sessions, starts with it; existing ones keep theirs.
    /// False when Steam refused it or isn't available.
    /// </summary>
    public static bool SetIce(int candidateTypes) =>
        SetGlobalConfig(SteamNetworkingConfigValue.P2PTransportIceEnable, candidateTypes);

    /// <summary>The ICE candidate types in effect at global scope (<see cref="SteamIceCandidates.Default"/>: the player's own Steam setting). False when Steam won't say.</summary>
    public static bool TryGetIce(out int candidateTypes) =>
        TryGetGlobalConfig(SteamNetworkingConfigValue.P2PTransportIceEnable, out candidateTypes);

    /// <summary>Sets an int32 config value at global scope (for connection values: the default for new connections).</summary>
    public static bool SetGlobalConfig(SteamNetworkingConfigValue value, int data)
    {
        nint self = SteamInterfaces.NetworkingUtils();
        if (self == 0)
        {
            return false;
        }

        try
        {
            return SteamNative.SetGlobalConfigValueInt32(self, value, data);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>Sets a float config value at global scope.</summary>
    public static bool SetGlobalConfig(SteamNetworkingConfigValue value, float data)
    {
        nint self = SteamInterfaces.NetworkingUtils();
        if (self == 0)
        {
            return false;
        }

        try
        {
            return SteamNative.SetGlobalConfigValueFloat(self, value, data);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>Sets an int32 config value at any scope through the general <c>SetConfigValue</c>.</summary>
    public static bool SetConfig(SteamNetworkingConfigValue value, SteamNetworkingConfigScope scope, nint scopeObject, int data)
    {
        nint self = SteamInterfaces.NetworkingUtils(nameof(SteamNative.SetConfigValueInt32));
        if (self == 0)
        {
            return false;
        }

        try
        {
            return SteamNative.SetConfigValueInt32(self, value, scope, scopeObject, SteamNetworkingConfigDataType.Int32, in data);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>Sets a float config value at any scope through the general <c>SetConfigValue</c>.</summary>
    public static bool SetConfig(SteamNetworkingConfigValue value, SteamNetworkingConfigScope scope, nint scopeObject, float data)
    {
        nint self = SteamInterfaces.NetworkingUtils(nameof(SteamNative.SetConfigValueFloat));
        if (self == 0)
        {
            return false;
        }

        try
        {
            return SteamNative.SetConfigValueFloat(self, value, scope, scopeObject, SteamNetworkingConfigDataType.Float, in data);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>Reads an int32 config value at global scope, so logs can show what's really in effect.</summary>
    public static bool TryGetGlobalConfig(SteamNetworkingConfigValue value, out int data)
    {
        bool ok = TryGetGlobalConfig(value, SteamNetworkingConfigDataType.Int32, out uint bits);
        data = unchecked((int)bits);
        return ok;
    }

    /// <summary>Reads a float config value at global scope.</summary>
    public static bool TryGetGlobalConfig(SteamNetworkingConfigValue value, out float data)
    {
        bool ok = TryGetGlobalConfig(value, SteamNetworkingConfigDataType.Float, out uint bits);
        data = BitConverter.Int32BitsToSingle(unchecked((int)bits));
        return ok;
    }

    /// <summary>
    /// Applies every field of <paramref name="conditions"/>, clamped to sane ranges (see
    /// <see cref="SteamFakeNetworkConditions"/>; use <c>default</c> to turn the simulation off). Returns how many settings
    /// Steam rejected: 0 means all applied.
    /// </summary>
    public static int ApplyFakeConditions(in SteamFakeNetworkConditions conditions)
    {
        int failures = 0;
        foreach (ConfigSetting setting in ToSettings(conditions))
        {
            bool ok = setting.DataType == SteamNetworkingConfigDataType.Int32
                ? SetGlobalConfig(setting.Value, setting.IntValue)
                : SetGlobalConfig(setting.Value, setting.FloatValue);
            if (!ok)
            {
                failures++;
            }
        }

        return failures;
    }

    /// <summary>
    /// The global config values behind <paramref name="conditions"/>, clamped, with the data type the SDK documents for
    /// each.
    /// </summary>
    internal static ConfigSetting[] ToSettings(in SteamFakeNetworkConditions conditions) =>
    [
        ConfigSetting.Int(SteamNetworkingConfigValue.FakePacketLagSend, Delay(conditions.LagSendMs)),
        ConfigSetting.Int(SteamNetworkingConfigValue.FakePacketLagRecv, Delay(conditions.LagRecvMs)),
        ConfigSetting.Float(SteamNetworkingConfigValue.FakePacketLossSend, Percent(conditions.LossSendPercent)),
        ConfigSetting.Float(SteamNetworkingConfigValue.FakePacketLossRecv, Percent(conditions.LossRecvPercent)),
        ConfigSetting.Float(SteamNetworkingConfigValue.FakePacketJitterSendAvg, Delay(conditions.JitterSendAvgMs)),
        ConfigSetting.Float(SteamNetworkingConfigValue.FakePacketJitterSendMax, Delay(conditions.JitterSendMaxMs)),
        ConfigSetting.Float(SteamNetworkingConfigValue.FakePacketJitterSendPct, Percent(conditions.JitterSendPercent)),
        ConfigSetting.Float(SteamNetworkingConfigValue.FakePacketJitterRecvAvg, Delay(conditions.JitterRecvAvgMs)),
        ConfigSetting.Float(SteamNetworkingConfigValue.FakePacketJitterRecvMax, Delay(conditions.JitterRecvMaxMs)),
        ConfigSetting.Float(SteamNetworkingConfigValue.FakePacketJitterRecvPct, Percent(conditions.JitterRecvPercent)),
        ConfigSetting.Float(SteamNetworkingConfigValue.FakePacketReorderSend, Percent(conditions.ReorderSendPercent)),
        ConfigSetting.Float(SteamNetworkingConfigValue.FakePacketReorderRecv, Percent(conditions.ReorderRecvPercent)),
        ConfigSetting.Int(SteamNetworkingConfigValue.FakePacketReorderTime, Delay(conditions.ReorderTimeMs)),
        ConfigSetting.Float(SteamNetworkingConfigValue.FakePacketDupSend, Percent(conditions.DuplicateSendPercent)),
        ConfigSetting.Float(SteamNetworkingConfigValue.FakePacketDupRecv, Percent(conditions.DuplicateRecvPercent)),
        ConfigSetting.Int(SteamNetworkingConfigValue.FakePacketDupTimeMax, Delay(conditions.DuplicateTimeMaxMs)),
    ];

    private static int Delay(int ms) => Math.Clamp(ms, 0, SteamFakeNetworkConditions.MaxDelayMs);

    private static float Delay(float ms) => float.IsNaN(ms) ? 0 : Math.Clamp(ms, 0, SteamFakeNetworkConditions.MaxDelayMs);

    private static float Percent(float percent) => float.IsNaN(percent) ? 0 : Math.Clamp(percent, 0, 100);

    private static bool TryGetGlobalConfig(SteamNetworkingConfigValue value, SteamNetworkingConfigDataType expected, out uint bits)
    {
        bits = 0;
        nint self = SteamInterfaces.NetworkingUtils();
        if (self == 0)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[8];
        buffer.Clear();
        nuint size = (nuint)buffer.Length;
        try
        {
            SteamNetworkingGetConfigValueResult result = SteamNative.GetConfigValue(
                self, value, SteamNetworkingConfigScope.Global, 0, out SteamNetworkingConfigDataType type, ref MemoryMarshal.GetReference(buffer), ref size);
            if (result is not (SteamNetworkingGetConfigValueResult.OK or SteamNetworkingGetConfigValueResult.OKInherited) ||
                type != expected || size != 4)
            {
                return false;
            }

            bits = MemoryMarshal.Read<uint>(buffer);
            return true;
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>One global config value to set.</summary>
    internal readonly record struct ConfigSetting(SteamNetworkingConfigValue Value, SteamNetworkingConfigDataType DataType, int IntValue, float FloatValue)
    {
        public static ConfigSetting Int(SteamNetworkingConfigValue value, int data) => new(value, SteamNetworkingConfigDataType.Int32, data, 0);

        public static ConfigSetting Float(SteamNetworkingConfigValue value, float data) => new(value, SteamNetworkingConfigDataType.Float, 0, data);
    }
}
