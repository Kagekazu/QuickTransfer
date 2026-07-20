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

/// <summary>
///     FC chest organize sort key.
///     Order: UI category OrderMajor → OrderMinor → materia BaseParam (0 if not materia)
///     → materia grade → ItemId → HQ.
///     Materia I–V item IDs are type-major while VI+ are grade-major; using the Materia
///     sheet keeps all grades sorted by stat then grade like vanilla category sort.
/// </summary>
internal readonly struct ChestSortKey(
    ushort orderMajor,
    ushort orderMinor,
    uint materiaBaseParam,
    byte materiaGrade,
    uint itemId,
    bool isHq) : IComparable<ChestSortKey>
{
    private readonly ushort orderMajor = orderMajor;
    private readonly ushort orderMinor = orderMinor;
    private readonly uint materiaBaseParam = materiaBaseParam;
    private readonly byte materiaGrade = materiaGrade;
    private readonly uint itemId = itemId;
    private readonly byte hq = (byte)(isHq ? 1 : 0);

    public int CompareTo(ChestSortKey other)
    {
        var c = orderMajor.CompareTo(other.orderMajor);
        if (c != 0)
        {
            return c;
        }

        c = orderMinor.CompareTo(other.orderMinor);
        if (c != 0)
        {
            return c;
        }

        c = materiaBaseParam.CompareTo(other.materiaBaseParam);
        if (c != 0)
        {
            return c;
        }

        c = materiaGrade.CompareTo(other.materiaGrade);
        if (c != 0)
        {
            return c;
        }

        c = itemId.CompareTo(other.itemId);
        return c != 0 ? c : hq.CompareTo(other.hq);
    }
}

#pragma warning restore CS0649
