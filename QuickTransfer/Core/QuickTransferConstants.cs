namespace QuickTransfer;

internal static class QuickTransferConstants
{
    public const string CommandName = "/qt";
    public const int WideAddonSearchMaxIndex = 50;
    public const long MinLikelyPointer = 0x1_0000_0000;

    public const string InventoryAddonName = "Inventory";
    public const string SaddlebagAddonName = "InventoryBuddy";
    public const string Saddlebag2AddonName = "InventoryBuddy2";
    public const string RetainerGrid0AddonName = "RetainerGrid0";
    public const string RetainerGridAddonName = "RetainerGrid";
    public const string RetainerSellListAddonName = "RetainerSellList";
    public const string FreeCompanyChestAddonName = "FreeCompanyChest";
    public const string ArmouryBoardAddonName = "ArmouryBoard";
    public const string InputNumericAddonName = "InputNumeric";
    public const string ContextMenuAddonName = "ContextMenu";
    public const string SelectYesnoAddonName = "SelectYesno";
    public const string AetherBagsRetainerAddonName = "AetherBags_Retainer";
    public const string AetherBagsSaddleBagAddonName = "AetherBags_SaddleBag";

    public static readonly string[] ArmouryAddonNames =
    [
        ArmouryBoardAddonName,
        "ArmoryBoard",
        "Armoury",
        "Armory",
        "ArmouryChest",
        "ArmoryChest"
    ];

    // Item grids whose hover/drag-drop events are captured for middle-click targeting.
    public static readonly string[] ContainerAddonNames =
    [
        InventoryAddonName,
        SaddlebagAddonName,
        Saddlebag2AddonName,
        RetainerGrid0AddonName,
        RetainerGridAddonName,
        RetainerSellListAddonName,
        FreeCompanyChestAddonName
    ];

    public static readonly string[] AllContainerAddonNames =
    [
        ..ContainerAddonNames,
        ..ArmouryAddonNames
    ];
}
