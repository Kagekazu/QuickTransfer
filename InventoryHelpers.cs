using Dalamud.Game.NativeWrapper;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
namespace QuickTransfer;

/// <summary>
///     Static helper functions for inventory detection, type checking, and addon visibility.
/// </summary>
internal static unsafe class InventoryHelpers
{
    private static readonly InventoryType[] PlayerInventoryTypes =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4
    ];

    private static readonly InventoryType[] SaddlebagInventoryTypes =
    [
        InventoryType.SaddleBag1,
        InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1,
        InventoryType.PremiumSaddleBag2
    ];

    private static readonly InventoryType[] RetainerInventoryTypes =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7
    ];

    private static readonly Dictionary<uint, uint> StackSizeCache = new();
    private static readonly Dictionary<uint, uint> ItemUiCategoryCache = new();
    // Access services through Plugin's static properties
    private static IGameGui GameGui => Plugin.GameGui;
    private static IDataManager DataManager => Plugin.DataManager;

    public static bool IsPlayerInventoryType(InventoryType inventoryType)
        => inventoryType is
            InventoryType.Inventory1 or
            InventoryType.Inventory2 or
            InventoryType.Inventory3 or
            InventoryType.Inventory4;

    public static bool IsArmouryType(InventoryType inventoryType)
        => inventoryType is
            InventoryType.ArmoryMainHand or
            InventoryType.ArmoryOffHand or
            InventoryType.ArmoryHead or
            InventoryType.ArmoryBody or
            InventoryType.ArmoryHands or
            InventoryType.ArmoryWaist or
            InventoryType.ArmoryLegs or
            InventoryType.ArmoryFeets or
            InventoryType.ArmoryEar or
            InventoryType.ArmoryNeck or
            InventoryType.ArmoryWrist or
            InventoryType.ArmoryRings or
            InventoryType.ArmorySoulCrystal;

    public static bool IsSaddlebagType(InventoryType inventoryType)
        => inventoryType is
            InventoryType.SaddleBag1 or
            InventoryType.SaddleBag2 or
            InventoryType.PremiumSaddleBag1 or
            InventoryType.PremiumSaddleBag2;

    public static bool IsRetainerType(InventoryType inventoryType)
        => inventoryType is
            InventoryType.RetainerPage1 or
            InventoryType.RetainerPage2 or
            InventoryType.RetainerPage3 or
            InventoryType.RetainerPage4 or
            InventoryType.RetainerPage5 or
            InventoryType.RetainerPage6 or
            InventoryType.RetainerPage7;

    public static bool IsCompanyChestType(InventoryType inventoryType)
    {
        string? name = Enum.GetName(typeof(InventoryType), inventoryType);
        if (string.IsNullOrEmpty(name))
            return false;

        // We only want the *item compartments*, not crystals/gil/etc.
        // Observed names: FreeCompanyPage1..FreeCompanyPage5
        return name.StartsWith("FreeCompanyPage", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAddonVisible(string addonName, int index = 1)
    {
        AtkUnitBasePtr addon = GameGui.GetAddonByName(addonName, index);
        return addon is { IsNull: false, IsVisible: true };
    }

    public static bool IsAddonVisibleAnyIndex(string addonName, int maxIndex = 6)
    {
        for(int i = 1; i <= maxIndex; i++)
        {
            if (IsAddonVisible(addonName, i))
                return true;
        }

        return false;
    }

    public static bool IsAnyAddonVisible(IEnumerable<string> addonNames, int index = 1)
    {
        foreach(string name in addonNames)
        {
            if (IsAddonVisible(name, index))
                return true;
        }

        return false;
    }

    public static bool IsAnyAddonVisibleAnyIndex(IEnumerable<string> addonNames, int maxIndex = 6)
    {
        foreach(string name in addonNames)
        {
            if (IsAddonVisibleAnyIndex(name, maxIndex))
                return true;
        }

        return false;
    }

    public static bool IsInventoryAndSaddlebagOpen()
    {
        bool inventoryOpen = IsAddonVisibleAnyIndex("Inventory");
        bool saddlebagOpen = IsAddonVisibleAnyIndex("InventoryBuddy") || IsAddonVisibleAnyIndex("InventoryBuddy2");
        return inventoryOpen && saddlebagOpen;
    }

    public static bool IsSaddlebagOpen()
        => IsAddonVisibleAnyIndex("InventoryBuddy") || IsAddonVisibleAnyIndex("InventoryBuddy2");

    public static bool IsRetainerOpen() =>
        // Common retainer inventory addons.
        // (SimpleTweaks checks "RetainerGrid0" for retainer inventory visibility.)
        IsAddonVisibleAnyIndex("RetainerGrid0") ||
        IsAddonVisibleAnyIndex("RetainerSellList") ||
        IsAddonVisibleAnyIndex("RetainerGrid");

    public static bool IsCompanyChestOpen()
        => IsAddonVisibleAnyIndex("FreeCompanyChest");

    public static bool IsTradeOpen()
        => IsAddonVisibleAnyIndex("Trade") || IsAddonVisibleAnyIndex("TradeWindow");

    public static bool IsVendorOpen()
        => IsAddonVisibleAnyIndex("Shop");

    public static bool TryGetVisibleAddon(string addonName, out AtkUnitBase* addon, int maxIndex = 6)
    {
        addon = null;
        for(int i = 1; i <= maxIndex; i++)
        {
            AtkUnitBasePtr a = GameGui.GetAddonByName(addonName, i);
            if (a is { IsNull: false, IsVisible: true })
            {
                addon = (AtkUnitBase*)a.Address;
                return true;
            }
        }

        return false;
    }

    public static bool TryGetItemInfo(
        InventoryType type,
        int slot,
        out uint itemId,
        out bool isHq,
        out uint quantity)
    {
        itemId = 0;
        isHq = false;
        quantity = 0;

        InventoryManager* inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        InventoryItem* it = inv->GetInventorySlot(type, slot);
        if (it == null)
            return false;

        itemId = it->ItemId;
        isHq = it->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
        quantity = (uint)it->Quantity;
        return itemId != 0;
    }

    public static bool IsContainerLoaded(InventoryManager* inv, InventoryType type)
    {
        try
        {
            if (inv == null)
                return false;
            InventoryContainer* c = inv->GetInventoryContainer(type);
            return c != null && c->IsLoaded && c->Size > 0;
        }
        catch
        {
            return false;
        }
    }

    public static InventoryType[] GetPlayerInventoryTypes() => PlayerInventoryTypes;
    public static InventoryType[] GetSaddlebagInventoryTypes() => SaddlebagInventoryTypes;
    public static InventoryType[] GetRetainerInventoryTypes() => RetainerInventoryTypes;

    public static uint GetItemStackSize(uint itemId)
    {
        try
        {
            // If item isn't known/stackable, return 1.
            if (itemId == 0)
                return 1;

            lock(StackSizeCache)
            {
                if (StackSizeCache.TryGetValue(itemId, out uint cached))
                    return cached;
            }

            ExcelSheet<Item> sheet = DataManager.GetExcelSheet<Item>();

            // Item row IDs are base IDs; InventoryItem.ItemId is expected to already be base.
            Item row = sheet.GetRow(itemId);
            if (row.RowId == 0)
                return 999;

            // In modern Lumina sheets, Item.StackSize exists.
            uint s = row.StackSize;
            uint result = s <= 0 ? 1U : s;
            lock(StackSizeCache)
            {
                StackSizeCache[itemId] = result;
            }
            return result;
        }
        catch
        {
            // Fallback: most stackables are 999, and non-stackables will hit maxStack <= 1 cases anyway.
            return 999;
        }
    }

    public static uint GetItemUiCategory(uint itemId)
    {
        try
        {
            if (itemId == 0)
                return 0;

            lock(ItemUiCategoryCache)
            {
                if (ItemUiCategoryCache.TryGetValue(itemId, out uint cached))
                    return cached;
            }

            ExcelSheet<Item> sheet = DataManager.GetExcelSheet<Item>();

            Item row = sheet.GetRow(itemId);
            if (row.RowId == 0)
                return 0;

            // Prefer UI category; this tends to match how game sorts items visually.
            uint result;
            try
            {
                // Lumina RowRef usually exposes RowId.
                result = row.ItemUICategory.RowId;
            }
            catch
            {
                result = 0;
            }

            lock(ItemUiCategoryCache)
            {
                ItemUiCategoryCache[itemId] = result;
            }
            return result;
        }
        catch
        {
            return 0;
        }
    }
}
