using ECommons;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
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
    private static readonly Dictionary<uint, ChestSortParts> ItemSortPartsCache = [];
    private static Dictionary<uint, (uint BaseParam, byte Grade)>? MateriaSortLookup;
    private static readonly object MateriaLookupGate = new();

    private readonly record struct ChestSortParts(ushort Major, ushort Minor, uint MateriaBaseParam, byte MateriaGrade);

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
        => IsAddonVisibleAnyIndex("InventoryBuddy") ||
           IsAddonVisibleAnyIndex("InventoryBuddy2") ||
           IsAddonVisibleAnyIndex(QuickTransferConstants.AetherBagsSaddleBagAddonName);

    public static bool IsRetainerSellListOpen()
        => IsAddonVisibleAnyIndex(QuickTransferConstants.RetainerSellListAddonName);

    public static bool IsRetainerOpen()
        => IsAddonVisibleAnyIndex("RetainerGrid0") ||
           IsRetainerSellListOpen() ||
           IsAddonVisibleAnyIndex("RetainerGrid") ||
           IsAddonVisibleAnyIndex(QuickTransferConstants.AetherBagsRetainerAddonName) ||
           IsRetainerAgentActive();

    /// <summary>
    ///     True while the retainer agent is active. Covers third-party UIs (e.g. AetherBags)
    ///     that hide vanilla RetainerGrid* but keep the retainer session open.
    /// </summary>
    public static bool IsRetainerAgentActive()
    {
        try
        {
            var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
            return agent != null && agent->IsAgentActive();
        }
        catch
        {
            return false;
        }
    }

    public static bool IsRetainerMarketListingSourceType(InventoryType inventoryType)
        => IsPlayerInventoryType(inventoryType) ||
           IsArmouryType(inventoryType) ||
           IsPlayerCrystalsType(inventoryType) ||
           IsSaddlebagType(inventoryType) ||
           IsRetainerType(inventoryType);

    public static bool ShouldYieldQuickTransferForRetainerMarket(
        Configuration configuration,
        ContextMenuHandler.ModifierMode mode,
        InventoryType inventoryType)
    {
        if (!configuration.YieldQuickTransferOnRetainerSellList ||
            !configuration.EnableShiftQuickTransfer ||
            mode != ContextMenuHandler.ModifierMode.Shift ||
            !IsRetainerSellListOpen())
        {
            return false;
        }

        return IsRetainerMarketListingSourceType(inventoryType);
    }

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

    /// <summary>
    ///     Vanilla-ish sort key for FC chest organize.
    ///     Materia uses Materia sheet (BaseParam → grade) so all tiers group by stat.
    /// </summary>
    public static ChestSortKey GetChestSortKey(uint itemId, bool isHq)
    {
        var parts = GetItemSortParts(itemId);
        return new ChestSortKey(
            parts.Major,
            parts.Minor,
            parts.MateriaBaseParam,
            parts.MateriaGrade,
            itemId,
            isHq);
    }

    private static ChestSortParts GetItemSortParts(uint itemId)
    {
        if (itemId == 0)
        {
            return default;
        }

        try
        {
            lock (ItemSortPartsCache)
            {
                if (ItemSortPartsCache.TryGetValue(itemId, out var cached))
                {
                    return cached;
                }
            }

            ushort major = 0;
            ushort minor = 0;
            uint materiaBaseParam = 0;
            byte materiaGrade = 0;

            if (GenericHelpers.TryGetRow(itemId, out Item row) && row.RowId != 0)
            {
                try
                {
                    var catId = row.ItemUICategory.RowId;
                    if (catId != 0 &&
                        GenericHelpers.TryGetRow(catId, out ItemUICategory uiCat) &&
                        uiCat.RowId != 0)
                    {
                        major = uiCat.OrderMajor;
                        minor = uiCat.OrderMinor;
                    }
                }
                catch
                {
                    // ignore
                }

                // FilterGroup 13 = Materia (see EXDSchema Item.FilterGroup).
                try
                {
                    if (row.FilterGroup == 13 &&
                        TryGetMateriaSortParts(itemId, out var baseParam, out var grade))
                    {
                        materiaBaseParam = baseParam;
                        materiaGrade = grade;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            var result = new ChestSortParts(major, minor, materiaBaseParam, materiaGrade);
            lock (ItemSortPartsCache)
            {
                ItemSortPartsCache[itemId] = result;
            }

            return result;
        }
        catch
        {
            return default;
        }
    }

    private static bool TryGetMateriaSortParts(uint itemId, out uint baseParam, out byte grade)
    {
        baseParam = 0;
        grade = 0;

        var lookup = MateriaSortLookup;
        if (lookup == null)
        {
            lock (MateriaLookupGate)
            {
                lookup = MateriaSortLookup;
                if (lookup == null)
                {
                    lookup = BuildMateriaSortLookup();
                    MateriaSortLookup = lookup;
                }
            }
        }

        if (!lookup.TryGetValue(itemId, out var parts))
        {
            return false;
        }

        baseParam = parts.BaseParam;
        grade = parts.Grade;
        return true;
    }

    private static Dictionary<uint, (uint BaseParam, byte Grade)> BuildMateriaSortLookup()
    {
        var map = new Dictionary<uint, (uint BaseParam, byte Grade)>();
        try
        {
            var sheet = Svc.Data.GetExcelSheet<Materia>();
            if (sheet == null)
            {
                return map;
            }

            foreach (var row in sheet)
            {
                var baseParam = row.BaseParam.RowId;
                if (baseParam == 0)
                {
                    continue;
                }

                var count = row.Item.Count;
                for (var g = 0; g < count; g++)
                {
                    var id = row.Item[g].RowId;
                    if (id == 0)
                    {
                        continue;
                    }

                    // First mapping wins; grades are unique per item id.
                    map.TryAdd(id, (baseParam, (byte)g));
                }
            }
        }
        catch
        {
            // leave empty; callers fall back to ItemId-only ordering
        }

        return map;
    }
}
