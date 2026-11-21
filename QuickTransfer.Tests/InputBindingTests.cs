using Dalamud.Game.ClientState.Keys;
using QuickTransfer.Framework;
using Xunit;

namespace QuickTransfer.Tests;

public class InputBindingTests
{
    private static Configuration Config(bool m = true, bool x1 = false, bool x2 = false)
        => new() { MiddleClickUseMButton = m, MiddleClickUseXButton1 = x1, MiddleClickUseXButton2 = x2 };

    [Theory]
    [InlineData(true, false, 0, 1000, true)]
    [InlineData(true, true, 900, 1000, true)]
    [InlineData(true, true, 500, 1000, false)]
    [InlineData(false, false, 0, 1000, false)]
    public void FreshPress_CountsOnlyShortlyAfterThePress(bool down, bool wasDown, long pressedAt, long now, bool expected)
        => Assert.Equal(expected, ModifierBindings.IsFreshPress(down, wasDown, pressedAt, now));

    [Fact]
    public void HeldMouse4_PushToTalk_DoesNotTurnALeftClickIntoASort()
    {
        var config = Config(m: false, x1: true);

        Assert.False(ModifierBindings.IsMiddleClickPressed(config, 0x01, 0, middleFreshPress: false, x1FreshPress: false, x2FreshPress: false));
        Assert.True(ModifierBindings.IsMiddleClickPressed(config, 0x01, 0, middleFreshPress: false, x1FreshPress: true, x2FreshPress: false));
    }

    [Fact]
    public void DisabledMiddleButton_IsIgnoredEvenWhenTheEventSaysMiddle()
    {
        var config = Config(m: false, x1: true);

        Assert.False(ModifierBindings.IsMiddleClickPressed(config, 0x04, 0x04, middleFreshPress: true, x1FreshPress: false, x2FreshPress: false));
    }

    [Fact]
    public void MiddleButtonEventMask_CountsWhenEnabled()
        => Assert.True(ModifierBindings.IsMiddleClickPressed(Config(), 0x04, 0, false, false, false));

    [Fact]
    public void MiddleClickEdge_OnlyOnPressOfAnEnabledButton()
    {
        var config = Config(m: true, x2: false);

        Assert.True(ModifierBindings.IsMiddleClickEdge(true, false, false, false, false, false, config));
        Assert.False(ModifierBindings.IsMiddleClickEdge(true, true, false, false, false, false, config));
        Assert.False(ModifierBindings.IsMiddleClickEdge(false, false, false, false, true, false, config));
    }

    [Fact]
    public void ModifierConflict_IsReportedOnlyForEnabledActions()
    {
        var config = new Configuration { CtrlActionModifier = VirtualKey.SHIFT };
        Assert.True(ModifierBindings.HasModifierConflict(config, out var conflict));
        Assert.Contains("Shift", conflict);

        config.EnableCtrlArmoury = false;
        Assert.False(ModifierBindings.HasModifierConflict(config, out _));
    }

    [Fact]
    public void Sanitize_FixesUnknownModifiersAndClampsTimings()
    {
        var config = new Configuration
        {
            AltActionModifier = VirtualKey.F1,
            ModifierLatchMs = 9000
        };

        config.SanitizeKeybindings();

        Assert.Equal(VirtualKey.SHIFT, config.AltActionModifier);
        Assert.Equal(500, config.ModifierLatchMs);
    }

    [Fact]
    public void NewConfiguration_IsAtCurrentVersion()
        => Assert.Equal(Configuration.CurrentVersion, new Configuration().Version);
}
