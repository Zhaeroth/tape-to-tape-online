using System.Linq;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests;

public class SteamNetworkingTests
{
    [Fact]
    public void Fake_conditions_map_every_field_to_its_config_value_and_sdk_type()
    {
        var conditions = new SteamFakeNetworkConditions
        {
            LagSendMs = 1,
            LagRecvMs = 2,
            LossSendPercent = 3,
            LossRecvPercent = 4,
            JitterSendAvgMs = 5,
            JitterSendMaxMs = 6,
            JitterSendPercent = 7,
            JitterRecvAvgMs = 8,
            JitterRecvMaxMs = 9,
            JitterRecvPercent = 10,
            ReorderSendPercent = 11,
            ReorderRecvPercent = 12,
            ReorderTimeMs = 13,
            DuplicateSendPercent = 14,
            DuplicateRecvPercent = 15,
            DuplicateTimeMaxMs = 16,
        };

        SteamNetworking.ConfigSetting[] settings = SteamNetworking.ToSettings(conditions);

        (SteamNetworkingConfigValue Value, bool IsInt, float Expected)[] expected =
        [
            (SteamNetworkingConfigValue.FakePacketLagSend, true, 1),
            (SteamNetworkingConfigValue.FakePacketLagRecv, true, 2),
            (SteamNetworkingConfigValue.FakePacketLossSend, false, 3),
            (SteamNetworkingConfigValue.FakePacketLossRecv, false, 4),
            (SteamNetworkingConfigValue.FakePacketJitterSendAvg, false, 5),
            (SteamNetworkingConfigValue.FakePacketJitterSendMax, false, 6),
            (SteamNetworkingConfigValue.FakePacketJitterSendPct, false, 7),
            (SteamNetworkingConfigValue.FakePacketJitterRecvAvg, false, 8),
            (SteamNetworkingConfigValue.FakePacketJitterRecvMax, false, 9),
            (SteamNetworkingConfigValue.FakePacketJitterRecvPct, false, 10),
            (SteamNetworkingConfigValue.FakePacketReorderSend, false, 11),
            (SteamNetworkingConfigValue.FakePacketReorderRecv, false, 12),
            (SteamNetworkingConfigValue.FakePacketReorderTime, true, 13),
            (SteamNetworkingConfigValue.FakePacketDupSend, false, 14),
            (SteamNetworkingConfigValue.FakePacketDupRecv, false, 15),
            (SteamNetworkingConfigValue.FakePacketDupTimeMax, true, 16),
        ];

        Assert.Equal(expected.Length, settings.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            SteamNetworking.ConfigSetting setting = settings[i];
            Assert.Equal(expected[i].Value, setting.Value);
            if (expected[i].IsInt)
            {
                Assert.Equal(SteamNetworkingConfigDataType.Int32, setting.DataType);
                Assert.Equal((int)expected[i].Expected, setting.IntValue);
            }
            else
            {
                Assert.Equal(SteamNetworkingConfigDataType.Float, setting.DataType);
                Assert.Equal(expected[i].Expected, setting.FloatValue);
            }
        }
    }

    [Fact]
    public void Send_lag_loss_and_jitter_reach_steam_as_given_when_in_range()
    {
        // What the one-PC simulated network sets: 120 ms lag, 2% loss, ~10 ms jitter (max 40) on every packet sent.
        SteamNetworking.ConfigSetting[] settings = SteamNetworking.ToSettings(new SteamFakeNetworkConditions
        {
            LagSendMs = 120,
            LossSendPercent = 2,
            JitterSendAvgMs = 10,
            JitterSendMaxMs = 40,
            JitterSendPercent = 100,
        });

        Assert.Equal(120, Find(settings, SteamNetworkingConfigValue.FakePacketLagSend).IntValue);
        Assert.Equal(2f, Find(settings, SteamNetworkingConfigValue.FakePacketLossSend).FloatValue);
        Assert.Equal(10f, Find(settings, SteamNetworkingConfigValue.FakePacketJitterSendAvg).FloatValue);
        Assert.Equal(40f, Find(settings, SteamNetworkingConfigValue.FakePacketJitterSendMax).FloatValue);
        Assert.Equal(100f, Find(settings, SteamNetworkingConfigValue.FakePacketJitterSendPct).FloatValue);
    }

    [Fact]
    public void Out_of_range_conditions_are_clamped()
    {
        SteamNetworking.ConfigSetting[] settings = SteamNetworking.ToSettings(new SteamFakeNetworkConditions
        {
            LagSendMs = -50,
            LagRecvMs = 1_000_000,
            LossSendPercent = 250,
            LossRecvPercent = float.NaN,
            JitterSendAvgMs = -1,
            JitterSendMaxMs = float.PositiveInfinity,
            JitterSendPercent = -3,
            ReorderTimeMs = int.MinValue,
            DuplicateTimeMaxMs = int.MaxValue,
        });

        Assert.Equal(0, Find(settings, SteamNetworkingConfigValue.FakePacketLagSend).IntValue);
        Assert.Equal(SteamFakeNetworkConditions.MaxDelayMs, Find(settings, SteamNetworkingConfigValue.FakePacketLagRecv).IntValue);
        Assert.Equal(100f, Find(settings, SteamNetworkingConfigValue.FakePacketLossSend).FloatValue);
        Assert.Equal(0f, Find(settings, SteamNetworkingConfigValue.FakePacketLossRecv).FloatValue);
        Assert.Equal(0f, Find(settings, SteamNetworkingConfigValue.FakePacketJitterSendAvg).FloatValue);
        Assert.Equal(SteamFakeNetworkConditions.MaxDelayMs, Find(settings, SteamNetworkingConfigValue.FakePacketJitterSendMax).FloatValue);
        Assert.Equal(0f, Find(settings, SteamNetworkingConfigValue.FakePacketJitterSendPct).FloatValue);
        Assert.Equal(0, Find(settings, SteamNetworkingConfigValue.FakePacketReorderTime).IntValue);
        Assert.Equal(SteamFakeNetworkConditions.MaxDelayMs, Find(settings, SteamNetworkingConfigValue.FakePacketDupTimeMax).IntValue);
        Assert.All(settings, s => Assert.False(float.IsNaN(s.FloatValue) || float.IsInfinity(s.FloatValue)));
    }

    [Fact]
    public void Default_conditions_turn_everything_off()
    {
        SteamNetworking.ConfigSetting[] settings = SteamNetworking.ToSettings(default);

        Assert.All(settings, s => Assert.True(s.IntValue == 0 && s.FloatValue == 0));
        Assert.Equal(settings.Length, settings.Select(s => s.Value).Distinct().Count());
    }

    private static SteamNetworking.ConfigSetting Find(SteamNetworking.ConfigSetting[] settings, SteamNetworkingConfigValue value) =>
        settings.Single(s => s.Value == value);
}
