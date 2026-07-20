using FFXIVClientStructs.FFXIV.Client.Game;
namespace QuickTransfer;

internal static class QuickTransferConstants
{
    public const string CommandName = "/qt";
    public const int WideAddonSearchMaxIndex = 50;

    // Real UI heap pointers in a 64-bit process are typically well above 4GB.
    public const long MinLikelyPointer = 0x1_0000_0000;

    public const string RetainerSellListAddonName = "RetainerSellList";
    public const string FreeCompanyChestAddonName = "FreeCompanyChest";
    public const string InputNumericAddonName = "InputNumeric";
    public const string ContextMenuAddonName = "ContextMenu";
    public const string SelectYesnoAddonName = "SelectYesno";

    // AetherBags replaces vanilla inventory UIs with custom KamiToolKit addons.
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

    public static InventoryType[] PlayerInventoryTypes => InventoryHelpers.GetPlayerInventoryTypes();
    public static InventoryType[] SaddlebagInventoryTypes => InventoryHelpers.GetSaddlebagInventoryTypes();
    public static InventoryType[] RetainerInventoryTypes => InventoryHelpers.GetRetainerInventoryTypes();
}
