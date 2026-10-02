using ECommons;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
namespace QuickTransfer.Framework;

internal static unsafe class InventoryHelpers
{
    public static readonly InventoryType[] PlayerInventoryTypes =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4
    ];

    public static readonly InventoryType[] SaddlebagInventoryTypes =
    [
        InventoryType.SaddleBag1,
        InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1,
        InventoryType.PremiumSaddleBag2
    ];

    public static readonly InventoryType[] RetainerInventoryTypes =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7
    ];

    public static readonly InventoryType[] ArmouryInventoryTypes =
    [
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryWaist,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
        InventoryType.ArmorySoulCrystal
    ];

    private static readonly HashSet<InventoryType> CompanyChestTypes =
    [
        .. Enum.GetValues<InventoryType>()
            .Where(t => Enum.GetName(t)?.StartsWith("FreeCompanyPage", StringComparison.OrdinalIgnoreCase) == true)
    ];

    private static readonly Dictionary<uint, uint> StackSizeCache = [];
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

    public static bool IsCompanyChestDepositSourceType(InventoryType inventoryType)
        => IsPlayerInventoryType(inventoryType) || IsArmouryType(inventoryType) || IsPlayerCrystalsType(inventoryType);

    public static bool IsCompanyChestDestinationType(InventoryType inventoryType)
        => IsCompanyChestType(inventoryType) || inventoryType == InventoryType.FreeCompanyCrystals;

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
        => CompanyChestTypes.Contains(inventoryType);

    public static bool IsSortableContainerType(InventoryType inventoryType)
        => IsPlayerInventoryType(inventoryType) ||
           IsArmouryType(inventoryType) ||
           IsSaddlebagType(inventoryType) ||
           IsRetainerType(inventoryType) ||
           IsCompanyChestType(inventoryType);

    private static AtkUnitManager* UnitManager
    {
        get
        {
            try
            {
                var stage = AtkStage.Instance();
                return stage == null ? null : &stage->RaptureAtkUnitManager->AtkUnitManager;
            }
            catch
            {
                return null;
            }
        }
    }

    public static AtkUnitBase* GetAddonById(uint id)
    {
        try
        {
            if (id is 0 or > ushort.MaxValue)
            {
                return null;
            }

            var mgr = UnitManager;
            if (mgr == null)
            {
                return null;
            }

            var addon = mgr->GetAddonById((ushort)id);
            return addon != null && addon->Id == id ? addon : null;
        }
        catch
        {
            return null;
        }
    }

    public static AtkUnitBase* GetAddonByName(string addonName, int index = 1)
    {
        try
        {
            if (string.IsNullOrEmpty(addonName) || index < 1)
            {
                return null;
            }

            var mgr = UnitManager;
            return mgr == null ? null : mgr->GetAddonByName(addonName, index);
        }
        catch
        {
            return null;
        }
    }

    public static bool TryGetVisibleAddon(string addonName, out AtkUnitBase* addon, int maxIndex = 6)
    {
        addon = null;
        if (string.IsNullOrEmpty(addonName))
        {
            return false;
        }

        var limit = Math.Max(1, maxIndex);
        for (var i = 1; i <= limit; i++)
        {
            var candidate = GetAddonByName(addonName, i);
            if (candidate == null || !candidate->IsVisible)
            {
                continue;
            }

            addon = candidate;
            return true;
        }

        return false;
    }

    public static bool IsAddonVisibleAnyIndex(string addonName, int maxIndex = 6)
    {
        for (var i = 1; i <= maxIndex; i++)
        {
            var addon = GetAddonByName(addonName, i);
            if (addon != null && addon->IsVisible)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsSaddlebagOpen()
        => IsAddonVisibleAnyIndex(QuickTransferConstants.SaddlebagAddonName) ||
           IsAddonVisibleAnyIndex(QuickTransferConstants.Saddlebag2AddonName) ||
           IsAddonVisibleAnyIndex(QuickTransferConstants.AetherBagsSaddleBagAddonName);

    public static bool IsRetainerSellListOpen()
        => IsAddonVisibleAnyIndex(QuickTransferConstants.RetainerSellListAddonName);

    public static bool IsRetainerOpen()
        => IsAddonVisibleAnyIndex(QuickTransferConstants.RetainerGrid0AddonName) ||
           IsRetainerSellListOpen() ||
           IsAddonVisibleAnyIndex(QuickTransferConstants.RetainerGridAddonName) ||
           IsAddonVisibleAnyIndex(QuickTransferConstants.AetherBagsRetainerAddonName) ||
           IsRetainerAgentActive();

    private static bool IsRetainerAgentActive()
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
        => IsAddonVisibleAnyIndex(QuickTransferConstants.FreeCompanyChestAddonName);

    public static bool IsTradeOpen()
        => IsAddonVisibleAnyIndex("Trade") || IsAddonVisibleAnyIndex("TradeWindow");

    public static bool IsVendorOpen()
        => IsAddonVisibleAnyIndex("Shop");

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

    public static bool TryFindFirstOccupiedSlot(
        ReadOnlySpan<InventoryType> containers,
        out InventoryType type,
        out int slot)
    {
        type = default;
        slot = -1;

        try
        {
            var inv = InventoryManager.Instance();
            if (inv == null)
            {
                return false;
            }

            foreach (var t in containers)
            {
                var c = inv->GetInventoryContainer(t);
                if (c == null || !c->IsLoaded || c->Size <= 0)
                {
                    continue;
                }

                for (var i = 0; i < c->Size; i++)
                {
                    var it = c->GetInventorySlot(i);
                    if (it != null && it->ItemId != 0)
                    {
                        type = t;
                        slot = i;
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

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
                }

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

                    map.TryAdd(id, (baseParam, (byte)g));
                }
            }
        }
        catch
        {
            // Leave empty; callers fall back to ItemId-only ordering.
        }

        return map;
    }
}
