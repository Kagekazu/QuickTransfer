using QuickTransfer.Framework;
using Xunit;

namespace QuickTransfer.Tests;

public class CompanyChestPlannerTests
{
    private static readonly ChestSlot Empty = default;

    private static ChestSlot Item(uint id, int qty = 1, bool hq = false) => new(id, qty, hq);

    private static uint StackOf99(uint _) => 99;

    [Fact]
    public void Merge_MovesALaterPartialStackOntoTheFirstPartialStack()
    {
        ChestSlot[][] pages = [[Item(5, 10), Item(7, 3), Item(5, 20)]];

        Assert.Equal(new ChestMove(0, 2, 0, 0), CompanyChestPlanner.FindMergeMove(pages, StackOf99));
    }

    [Fact]
    public void Merge_NeverMixesHqAndNq()
    {
        ChestSlot[][] pages = [[Item(5, 10), Item(5, 20, hq: true)]];

        Assert.Null(CompanyChestPlanner.FindMergeMove(pages, StackOf99));
    }

    [Fact]
    public void Merge_SkipsUnstackableItems()
    {
        ChestSlot[][] gear = [[Item(9), Item(9)]];

        Assert.Null(CompanyChestPlanner.FindMergeMove(gear, _ => 1));
    }

    [Fact]
    public void Merge_NeverMovesAStackOntoAFullOneOrBackwards()
    {
        ChestSlot[][] pages = [[Item(5, 99), Item(5, 20)]];

        Assert.Null(CompanyChestPlanner.FindMergeMove(pages, StackOf99));
    }

    [Fact]
    public void Merge_WorksAcrossPages()
    {
        ChestSlot[][] pages = [[Item(5, 10)], [Item(5, 20)]];

        Assert.Equal(new ChestMove(1, 0, 0, 0), CompanyChestPlanner.FindMergeMove(pages, StackOf99));
    }

    [Fact]
    public void Compaction_FillsTheFirstGapWithTheNextItem()
    {
        ChestSlot[][] pages = [[Item(1), Empty, Empty, Item(2), Item(3)]];

        Assert.Equal(new ChestMove(0, 3, 0, 1), CompanyChestPlanner.FindCompactionMove(pages));
    }

    [Fact]
    public void Compaction_DoneWhenOnlyTrailingGapsRemain()
    {
        ChestSlot[][] pages = [[Item(1), Item(2), Empty, Empty]];

        Assert.Null(CompanyChestPlanner.FindCompactionMove(pages));
    }

    [Fact]
    public void Sort_SwapsTheSmallestKeyIntoTheFirstOutOfOrderSlot()
    {
        ChestSortKey[] keys = [Key(30), Key(10), Key(20)];

        Assert.Equal((1, 0), CompanyChestPlanner.FindSortMove(keys, [false, false, false]));
    }

    [Fact]
    public void Sort_RepeatedStepsEndSortedAndStop()
    {
        ChestSortKey[] keys = [Key(4), Key(2), Key(3), Key(1), Key(2)];
        var empty = new bool[keys.Length];

        var steps = 0;
        while (CompanyChestPlanner.FindSortMove(keys, empty) is { } move)
        {
            (keys[move.Src], keys[move.Dst]) = (keys[move.Dst], keys[move.Src]);
            Assert.True(++steps < 20, "sort did not converge");
        }

        Assert.Equal([.. keys.OrderBy(k => k)], keys);
    }

    [Fact]
    public void Sort_EqualKeysNeedNoMove()
        => Assert.Null(CompanyChestPlanner.FindSortMove([Key(5), Key(5)], [false, false]));

    [Fact]
    public void Sort_ClosesAGapBeforeOrdering()
        => Assert.Equal((2, 1), CompanyChestPlanner.FindSortMove([Key(2), default, Key(1)], [false, true, false]));

    [Fact]
    public void SortKey_OrdersByCategoryThenMateriaThenIdThenHq()
    {
        var nq = new ChestSortKey(1, 0, 0, 0, 100, false);
        var hq = new ChestSortKey(1, 0, 0, 0, 100, true);
        var laterCategory = new ChestSortKey(2, 0, 0, 0, 1, false);
        var lowerGrade = new ChestSortKey(1, 0, 7, 1, 999, false);
        var higherGrade = new ChestSortKey(1, 0, 7, 2, 1, false);

        Assert.True(nq.CompareTo(hq) < 0);
        Assert.True(hq.CompareTo(laterCategory) < 0);
        Assert.True(lowerGrade.CompareTo(higherGrade) < 0);
    }

    [Fact]
    public void FirstEmptySlot_LooksAcrossPagesInOrder()
    {
        ChestSlot[][] pages = [[Item(1), Item(2)], [Item(3), Empty]];

        Assert.Equal((1, 1), CompanyChestPlanner.FindFirstEmptySlot(pages));
    }

    [Fact]
    public void BestStackSlot_PicksTheStackWithMostRoom()
    {
        ChestSlot[][] pages = [[Item(5, 90), Item(5, 40), Item(5, 40, hq: true), Item(5, 99)]];

        Assert.Equal((0, 1), CompanyChestPlanner.FindBestStackSlot(pages, 5, false, 99));
        Assert.Null(CompanyChestPlanner.FindBestStackSlot([[Item(5, 99)]], 5, false, 99));
        Assert.Null(CompanyChestPlanner.FindBestStackSlot(pages, 5, false, 1));
    }

    [Theory]
    [InlineData("Another player is using the chest.", true)]
    [InlineData("unable to store item.", true)]
    [InlineData("Unable to complete company chest action.", true)]
    [InlineData("You obtain a Potion.", false)]
    [InlineData("", false)]
    public void BusyText_MatchesOnlyChestBusyLines(string text, bool expected)
        => Assert.Equal(expected, CompanyChestPlanner.IsBusyText(text));

    [Theory]
    [InlineData(true, true, 1, 99_999, false)]
    [InlineData(true, false, 1, 99_999, false)]
    [InlineData(false, true, 1000, 1001, true)]
    [InlineData(false, false, 1000, 2000, false)]
    [InlineData(false, false, 1000, 2500, true)]
    [InlineData(false, false, 0, 99_999, false)]
    public void MoveBuffers_AreNeverFreedUnderAnOpenPrompt(bool inputVisible, bool sawInput, long createdAt, long now, bool expected)
        => Assert.Equal(expected, CompanyChestPlanner.ShouldReleaseMoveBuffers(inputVisible, sawInput, createdAt, now));

    private static ChestSortKey Key(uint id) => new(0, 0, 0, 0, id, false);
}
