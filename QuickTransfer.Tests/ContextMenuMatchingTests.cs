using QuickTransfer.Framework;
using Xunit;
using Action = QuickTransfer.Framework.ContextMenuHandler.AutoContextAction;

namespace QuickTransfer.Tests;

public class ContextMenuMatchingTests
{
    private static Dictionary<Action, (int Index, string Text)> Classify(params string[] rows)
        => ContextMenuHandler.ClassifyMenuItems(rows.Select((t, i) => (i, t)));

    [Fact]
    public void RemoveAllFromSaddlebag_IsNotTakenForFcChestRemove()
    {
        var found = Classify("Remove All from Saddlebag");

        Assert.True(found.ContainsKey(Action.RemoveAllFromSaddlebag));
        Assert.False(found.ContainsKey(Action.RemoveFromCompanyChest));
    }

    [Fact]
    public void PlainRemove_IsFcChestRemove()
    {
        var found = Classify("Remove");

        Assert.Equal(0, found[Action.RemoveFromCompanyChest].Index);
        Assert.False(found.ContainsKey(Action.RemoveAllFromSaddlebag));
    }

    [Fact]
    public void TypicalInventoryMenu_MapsEachRowToItsAction()
    {
        var found = Classify("Discard", "Split", "Place in Armoury Chest", "Add All to Saddlebag", "Entrust to Retainer", "Trade");

        Assert.Equal(1, found[Action.Split].Index);
        Assert.Equal(2, found[Action.PlaceInArmouryChest].Index);
        Assert.Equal(3, found[Action.AddAllToSaddlebag].Index);
        Assert.Equal(4, found[Action.EntrustToRetainer].Index);
        Assert.Equal(5, found[Action.Trade].Index);
        Assert.DoesNotContain(found, kv => kv.Value.Index == 0);
    }

    [Fact]
    public void FirstMatchingRowWins()
    {
        var found = Classify("Split", "Split Stack");

        Assert.Equal(0, found[Action.Split].Index);
    }

    [Theory]
    [InlineData("Sort", true)]
    [InlineData("Undo Sort", false)]
    public void SortMatchesSortButNotUndoSort(string label, bool expected)
        => Assert.Equal(expected, ContextMenuHandler.ContextLabelMatches(Action.Sort, label));

    [Fact]
    public void UndoSortIsRecognised()
        => Assert.True(ContextMenuHandler.IsUndoSortLabel("Undo Sort"));

    [Fact]
    public void SoftHyphenAndPayloadBytesAreIgnored()
    {
        Assert.True(ContextMenuHandler.IsKnownLabel(Action.Split, "Sp­lit"));
        Assert.True(ContextMenuHandler.IsKnownLabel(Action.Trade, "\u0002\u0010\u0001\u0003Trade"));
    }

    [Theory]
    [InlineData(nameof(Action.Sell), "Trade")]
    [InlineData(nameof(Action.Trade), "Sell")]
    [InlineData(nameof(Action.RemoveFromCompanyChest), "Return to Inventory")]
    [InlineData(nameof(Action.PlaceInArmouryChest), "Return to Inventory")]
    public void UnrelatedLabelsDoNotMatch(string action, string label)
        => Assert.False(ContextMenuHandler.ContextLabelMatches(Enum.Parse<Action>(action), label));
}
