using T2TOnline.Plugin.Startup;

namespace T2TOnline.Core.Tests.Startup;

public class RipRelativeLoadTests
{
    // A one-line engine getter on a singleton: mov rax, [rip + 0x1000]; movzx eax, byte [rax + 9]; ret.
    private static readonly byte[] CaptureGetter = { 0x48, 0x8B, 0x05, 0x00, 0x10, 0x00, 0x00, 0x0F, 0xB6, 0x40, 0x09, 0xC3 };

    [Fact]
    public void A_load_into_rax_gives_the_global_after_the_instruction()
    {
        Assert.True(RipRelativeLoad.TryTarget(CaptureGetter, 0x2000, out long target));
        Assert.Equal(0x2000 + 7 + 0x1000, target);
    }

    // The two getters as the Nov 1 build's UnityPlayer.dll (Unity 6000.0.79f1) has them, each at its own address (RVA): both
    // load the same global, the crash reporter's, so either one is enough to find it.
    [Fact]
    public void The_engines_two_getters_load_the_same_global()
    {
        byte[] capture = { 0x48, 0x8B, 0x05, 0xC1, 0x72, 0xE6, 0x01, 0x0F, 0xB6, 0x40, 0x09, 0xC3 };
        byte[] buffer = { 0x48, 0x8B, 0x05, 0x81, 0x72, 0xE6, 0x01, 0x8B, 0x80, 0xD8, 0x00, 0x00, 0x00, 0xC3 };
        Assert.True(RipRelativeLoad.TryTarget(capture, 0x1790A0, out long fromCapture));
        Assert.True(RipRelativeLoad.TryTarget(buffer, 0x1790E0, out long fromBuffer));
        Assert.Equal(fromCapture, fromBuffer);
    }

    [Fact]
    public void A_load_into_rcx_is_read_the_same_way()
    {
        byte[] code = { 0x48, 0x8B, 0x0D, 0x34, 0x12, 0x00, 0x00 };
        Assert.True(RipRelativeLoad.TryTarget(code, 0x2000, out long target));
        Assert.Equal(0x2000 + 7 + 0x1234, target);
    }

    [Fact]
    public void A_negative_displacement_points_back()
    {
        byte[] code = { 0x48, 0x8B, 0x05, 0xF0, 0xFF, 0xFF, 0xFF };
        Assert.True(RipRelativeLoad.TryTarget(code, 0x1000, out long target));
        Assert.Equal(0x1000 + 7 - 0x10, target);
    }

    [Theory]
    [InlineData(new byte[] { 0x44, 0x0F, 0xB6, 0xC1, 0x48, 0x8B, 0x0D })] // movzx r8d, cl first: another shape
    [InlineData(new byte[] { 0x48, 0x8B, 0x15, 0x00, 0x10, 0x00, 0x00 })] // a load into rdx
    [InlineData(new byte[] { 0x48, 0x89, 0x05, 0x00, 0x10, 0x00, 0x00 })] // a store, not a load
    [InlineData(new byte[] { 0x48, 0x8B, 0x05, 0x00, 0x10, 0x00 })] // cut short
    [InlineData(new byte[0])]
    public void Anything_else_is_not_decoded(byte[] code)
    {
        Assert.False(RipRelativeLoad.TryTarget(code, 0x2000, out long target));
        Assert.Equal(0, target);
    }
}
