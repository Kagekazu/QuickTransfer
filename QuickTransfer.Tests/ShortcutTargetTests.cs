using QuickTransfer.Framework;
using Xunit;
using Action = QuickTransfer.Framework.ContextMenuHandler.AutoContextAction;
using Mode = QuickTransfer.Framework.ContextMenuHandler.ModifierMode;

namespace QuickTransfer.Tests;

public class ShortcutTargetTests
{
    private static readonly Configuration Defaults = new();

    private static OpenWindows Open(
        bool vendor = false,
        bool trade = false,
        bool fc = false,
        bool retainer = false,
        bool sellList = false,
        bool saddlebag = false)
        => new(vendor, trade, fc, retainer, sellList, saddlebag);

    private static Action First(Mode mode, OpenWindows open, Configuration? config = null)
        => ContextMenuHandler.GetActionPreference(mode, config ?? Defaults, open)[0];

    [Fact]
    public void Shift_PrefersVendorThenTradeThenFcChest()
    {
        Assert.Equal(Action.Sell, First(Mode.Shift, Open(vendor: true, trade: true, fc: true)));
        Assert.Equal(Action.Trade, First(Mode.Shift, Open(trade: true, fc: true)));
        Assert.Equal(Action.RemoveFromCompanyChest, First(Mode.Shift, Open(fc: true, saddlebag: true)));
    }

    [Fact]
    public void Shift_SkipsVendorAndFcChestWhenTurnedOff()
    {
        var config = new Configuration { EnableVendorQuickSell = false, EnableCompanyChest = false };

        Assert.Equal(Action.PlaceInArmouryChest, First(Mode.Shift, Open(vendor: true, fc: true), config));
    }

    [Theory]
    [InlineData(false, false, nameof(Action.PlaceInArmouryChest))]
    [InlineData(false, true, nameof(Action.RemoveAllFromSaddlebag))]
    [InlineData(true, false, nameof(Action.RetrieveFromRetainer))]
    [InlineData(true, true, nameof(Action.AddAllToSaddlebag))]
    public void Shift_PicksTheSaddlebagOrRetainerMove(bool retainer, bool saddlebag, string expected)
        => Assert.Equal(Enum.Parse<Action>(expected), First(Mode.Shift, Open(retainer: retainer, saddlebag: saddlebag)));

    [Fact]
    public void RetainerRowsInTheMenuCountAsRetainerOpen()
        => Assert.Equal(Action.RetrieveFromRetainer, ContextMenuHandler.GetActionPreference(Mode.Shift, Defaults, Open(), menuHasRetainerRows: true)[0]);

    [Fact]
    public void CtrlAndAlt_IgnoreOpenWindows()
    {
        Assert.Equal(Action.ReturnToInventory, First(Mode.Ctrl, Open(vendor: true, trade: true)));
        Assert.Equal(Action.Split, First(Mode.Alt, Open(vendor: true)));
    }

    [Fact]
    public void LiveLine_FollowsTheSamePriority()
    {
        Assert.Equal("Sells the item to the vendor", ContextMenuHandler.DescribeQuickTransfer(Defaults, Open(vendor: true, saddlebag: true)));
        Assert.Equal("Between inventory and saddlebag", ContextMenuHandler.DescribeQuickTransfer(Defaults, Open(saddlebag: true)));
        Assert.Equal("Between inventory and Armoury Chest", ContextMenuHandler.DescribeQuickTransfer(Defaults, Open()));
    }

    [Fact]
    public void LiveLine_SaysPausedOnTheRetainerMarketList()
    {
        Assert.StartsWith("Paused", ContextMenuHandler.DescribeQuickTransfer(Defaults, Open(retainer: true, sellList: true)));

        var config = new Configuration { YieldQuickTransferOnRetainerSellList = false };
        Assert.Equal("Between inventory and retainer", ContextMenuHandler.DescribeQuickTransfer(config, Open(retainer: true, sellList: true)));
    }

    [Fact]
    public void ArmouryLine_OffUntilAStorageWindowIsOpen()
    {
        Assert.StartsWith("Off", ContextMenuHandler.DescribeArmoury(Open(vendor: true, trade: true)));
        Assert.Equal("Between inventory and Armoury Chest", ContextMenuHandler.DescribeArmoury(Open(fc: true)));
    }
}
