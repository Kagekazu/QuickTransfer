#pragma warning disable CS0649 // State bags are populated field-by-field at runtime.

using FFXIVClientStructs.FFXIV.Client.Game;
namespace QuickTransfer;

internal static class QuickTransferConstants
{
    public const string CommandName = "/qt";
    public const int WideAddonSearchMaxIndex = 50;
    public const long MinLikelyPointer = 0x1_0000_0000;

    public const string RetainerSellListAddonName = "RetainerSellList";
    public const string FreeCompanyChestAddonName = "FreeCompanyChest";
    public const string InputNumericAddonName = "InputNumeric";
    public const string ContextMenuAddonName = "ContextMenu";
    public const string SelectYesnoAddonName = "SelectYesno";
    public const string AetherBagsRetainerAddonName = "AetherBags_Retainer";
    public const string AetherBagsSaddleBagAddonName = "AetherBags_SaddleBag";

    public static readonly string[] ArmouryAddonNames =
    [
        "ArmouryBoard",
        "ArmoryBoard",
        "Armoury",
        "Armory",
        "ArmouryChest",
        "ArmoryChest"
    ];

    public static readonly string[] ReceiveEventAddonNames =
    [
        "Inventory",
        "InventoryBuddy",
        "InventoryBuddy2",
        ..ArmouryAddonNames,
        "RetainerGrid0",
        RetainerSellListAddonName,
        "RetainerGrid",
        FreeCompanyChestAddonName
    ];
}

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
