#pragma warning disable CS0649 // State bags are populated field-by-field at runtime.

using FFXIVClientStructs.FFXIV.Client.Game;
namespace QuickTransfer;

internal enum PendingNumericKind
{
    None,
    Store,
    Remove,
    Move,
    Split,
    Trade,
    Sell
}

internal struct CompanyChestDepositState
{
    public bool Active;
    public InventoryType SourceType;
    public uint SourceSlot;
    public InventoryType DestPage;
    public uint ItemId;
    public bool IsHq;
    public long NextAttemptAtMs;
    public long ExpiresAtMs;
    public int Steps;
    public uint LastQty;
    public long WaitForQtyChangeUntilMs;
}

internal struct CompanyChestOrganizeState
{
    public bool Active;
    public uint OwnerAddonId;
    public long NextAttemptAtMs;
    public long ExpiresAtMs;
    public int Steps;
    public int Phase;
    public InventoryType[] Pages;

    public bool WaitingForApply;
    public InventoryType WaitSrcType;
    public uint WaitSrcSlot;
    public uint WaitSrcItemId;
    public int WaitSrcQty;
    public InventoryType WaitDstType;
    public uint WaitDstSlot;
    public uint WaitDstItemId;
    public int WaitDstQty;
    public long WaitUntilMs;
    public int WaitStuckCount;
    public long WaitObservedChangeAtMs;
}

internal readonly struct ChestSortKey(uint category, uint itemId, bool isHq) : IComparable<ChestSortKey>
{
    private readonly uint category = category;
    private readonly uint itemId = itemId;
    private readonly byte hq = (byte)(isHq ? 1 : 0);

    public int CompareTo(ChestSortKey other)
    {
        var c = category.CompareTo(other.category);
        if (c != 0)
        {
            return c;
        }
        c = itemId.CompareTo(other.itemId);
        return c != 0 ? c : hq.CompareTo(other.hq);
    }
}

#pragma warning restore CS0649
