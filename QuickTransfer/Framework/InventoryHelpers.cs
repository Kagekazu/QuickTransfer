using ECommons;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
namespace QuickTransfer.Framework;

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

    private static readonly Dictionary<uint, uint> StackSizeCache = [];
    private static readonly Dictionary<uint, uint> ItemUiCategoryCache = [];

    public static bool IsPlayerInventoryType(InventoryType inventoryType)
        => inventoryType is
            InventoryType.Inventory1 or
            InventoryType.Inventory2 or
            InventoryType.Inventory3 or
            InventoryType.Inventory4;

    public static bool IsPlayerCrystalsType(InventoryType inventoryType)
        => inventoryType == InventoryType.Crystals;

    public static bool IsCompanyChestCrystalsType(InventoryType inventoryType)
        => inventoryType == InventoryType.FreeCompanyCrystals;

    public static bool IsCompanyChestDepositSourceType(InventoryType inventoryType)
        => IsPlayerInventoryType(inventoryType) || IsArmouryType(inventoryType) || IsPlayerCrystalsType(inventoryType);

    public static bool IsCompanyChestDestinationType(InventoryType inventoryType)
        => IsCompanyChestType(inventoryType) || IsCompanyChestCrystalsType(inventoryType);

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
        var name = Enum.GetName(inventoryType);
        return !string.IsNullOrEmpty(name) && name.StartsWith("FreeCompanyPage", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAddonVisible(string addonName, int index = 1)
    {
        var addon = AddonHelpers.GetAddonByName(addonName, index);
        return addon != null && addon->IsVisible;
    }

    public static bool IsAddonVisibleAnyIndex(string addonName, int maxIndex = 6)
    {
        for (var i = 1; i <= maxIndex; i++)
        {
            if (IsAddonVisible(addonName, i))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsSaddlebagOpen()
        => IsAddonVisibleAnyIndex("InventoryBuddy") || IsAddonVisibleAnyIndex("InventoryBuddy2");

    public static bool IsRetainerOpen()
        => IsAddonVisibleAnyIndex("RetainerGrid0") ||
           IsAddonVisibleAnyIndex("RetainerSellList") ||
           IsAddonVisibleAnyIndex("RetainerGrid");

    public static bool IsCompanyChestOpen()
        => IsAddonVisibleAnyIndex("FreeCompanyChest");

    public static bool IsTradeOpen()
        => IsAddonVisibleAnyIndex("Trade") || IsAddonVisibleAnyIndex("TradeWindow");

    public static bool IsVendorOpen()
        => IsAddonVisibleAnyIndex("Shop");

    public static bool TryGetVisibleAddon(string addonName, out AtkUnitBase* addon, int maxIndex = 6)
        => AddonHelpers.TryGetVisibleAddon(addonName, out addon, maxIndex);

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

        var inv = InventoryManager.Instance();
        if (inv == null)
        {
            return false;
        }

        var it = inv->GetInventorySlot(type, slot);
        if (it == null)
        {
            return false;
        }

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
            {
                return false;
            }
            var c = inv->GetInventoryContainer(type);
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
            if (itemId == 0)
            {
                return 1;
            }

            lock (StackSizeCache)
            {
                if (StackSizeCache.TryGetValue(itemId, out var cached))
                {
                    return cached;
                }
            }

            if (!GenericHelpers.TryGetRow(itemId, out Item row) || row.RowId == 0)
            {
                return 999;
            }

            var s = row.StackSize;
            var result = s <= 0 ? 1U : s;
            lock (StackSizeCache)
            {
                StackSizeCache[itemId] = result;
            }
            return result;
        }
        catch
        {
            return 999;
        }
    }

    public static uint GetItemUiCategory(uint itemId)
    {
        try
        {
            if (itemId == 0)
            {
                return 0;
            }

            lock (ItemUiCategoryCache)
            {
                if (ItemUiCategoryCache.TryGetValue(itemId, out var cached))
                {
                    return cached;
                }
            }

            if (!GenericHelpers.TryGetRow(itemId, out Item row) || row.RowId == 0)
            {
                return 0;
            }

            uint result;
            try
            {
                result = row.ItemUICategory.RowId;
            }
            catch
            {
                result = 0;
            }

            lock (ItemUiCategoryCache)
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
