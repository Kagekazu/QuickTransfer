using FFXIVClientStructs.FFXIV.Client.Game;

namespace QuickTransfer;

internal static class QuickTransferConstants
{
    public const string CommandName = "/qt";
    public const int WideAddonSearchMaxIndex = 50;

    // Real UI heap pointers in a 64-bit process are typically well above 4GB.
    public const long MinLikelyPointer = 0x1_0000_0000;

    public const string FreeCompanyChestAddonName = "FreeCompanyChest";
    public const string InputNumericAddonName = "InputNumeric";
    public const string ContextMenuAddonName = "ContextMenu";
    public const string SelectYesnoAddonName = "SelectYesno";

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
        "RetainerSellList",
        "RetainerGrid",
        FreeCompanyChestAddonName
    ];

    public static InventoryType[] PlayerInventoryTypes => InventoryHelpers.GetPlayerInventoryTypes();
    public static InventoryType[] SaddlebagInventoryTypes => InventoryHelpers.GetSaddlebagInventoryTypes();
    public static InventoryType[] RetainerInventoryTypes => InventoryHelpers.GetRetainerInventoryTypes();
}
