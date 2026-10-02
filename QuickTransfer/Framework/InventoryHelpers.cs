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
    private static readonly Lazy<Dictionary<uint, (uint BaseParam, byte Grade)>> MateriaSortLookup = new(BuildMateriaSortLookup);

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

    public static bool IsSaddlebagOpen()
        => IsAnyAddonVisible(QuickTransferConstants.SaddlebagAddonName, QuickTransferConstants.Saddlebag2AddonName, QuickTransferConstants.AetherBagsSaddleBagAddonName);

    public static bool IsRetainerSellListOpen()
        => IsAnyAddonVisible(QuickTransferConstants.RetainerSellListAddonName);

    public static bool IsRetainerOpen()
        => IsAnyAddonVisible(
               QuickTransferConstants.RetainerGrid0AddonName,
               QuickTransferConstants.RetainerSellListAddonName,
               QuickTransferConstants.RetainerGridAddonName,
               QuickTransferConstants.AetherBagsRetainerAddonName) ||
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

    // While the retainer market list is open, right-clicks on owned items are for listing, not transferring.
    public static bool ShouldYieldQuickTransferForRetainerMarket(
        Configuration configuration,
        ContextMenuHandler.ModifierMode mode,
        InventoryType inventoryType)
        => configuration is { YieldQuickTransferOnRetainerSellList: true, EnableShiftQuickTransfer: true } &&
           mode == ContextMenuHandler.ModifierMode.Shift &&
           IsRetainerSellListOpen() &&
           (IsPlayerInventoryType(inventoryType) ||
            IsArmouryType(inventoryType) ||
            IsPlayerCrystalsType(inventoryType) ||
            IsSaddlebagType(inventoryType) ||
            IsRetainerType(inventoryType));

    public static bool IsCompanyChestOpen()
        => IsAnyAddonVisible(QuickTransferConstants.FreeCompanyChestAddonName);

    public static bool IsTradeOpen()
        => IsAnyAddonVisible("Trade", "TradeWindow");

    public static bool IsVendorOpen()
        => IsAnyAddonVisible("Shop");

    private static bool IsAnyAddonVisible(params ReadOnlySpan<string> addonNames)
    {
        foreach (var name in addonNames)
        {
            if (TryGetVisibleAddon(name, out var _))
            {
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

    // Returns null when the container is missing or not loaded yet.
    public static InventoryContainer* GetLoadedContainer(InventoryType type)
    {
        try
        {
            var inv = InventoryManager.Instance();
            if (inv == null)
            {
                return null;
            }

            var c = inv->GetInventoryContainer(type);
            return c != null && c->IsLoaded && c->Size > 0 ? c : null;
        }
        catch
        {
            return null;
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
            foreach (var t in containers)
            {
                var c = GetLoadedContainer(t);
                if (c == null)
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

            var result = row.StackSize <= 0 ? 1U : row.StackSize;
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

    // Approximates the in-game ordering: UI category order, then materia stat and grade, then item id and HQ.
    public static ChestSortKey GetChestSortKey(uint itemId, bool isHq)
    {
        var parts = GetItemSortParts(itemId);
        return new(parts.Major, parts.Minor, parts.MateriaBaseParam, parts.MateriaGrade, itemId, isHq);
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
            (uint BaseParam, byte Grade) materia = default;

            if (GenericHelpers.TryGetRow(itemId, out Item row) && row.RowId != 0)
            {
                var catId = row.ItemUICategory.RowId;
                if (catId != 0 && GenericHelpers.TryGetRow(catId, out ItemUICategory uiCat) && uiCat.RowId != 0)
                {
                    major = uiCat.OrderMajor;
                    minor = uiCat.OrderMinor;
                }

                // FilterGroup 13 is materia.
                if (row.FilterGroup == 13)
                {
                    MateriaSortLookup.Value.TryGetValue(itemId, out materia);
                }
            }

            var result = new ChestSortParts(major, minor, materia.BaseParam, materia.Grade);
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

    private static Dictionary<uint, (uint BaseParam, byte Grade)> BuildMateriaSortLookup()
    {
        Dictionary<uint, (uint BaseParam, byte Grade)> map = [];
        try
        {
            foreach (var row in Svc.Data.GetExcelSheet<Materia>())
            {
                var baseParam = row.BaseParam.RowId;
                if (baseParam == 0)
                {
                    continue;
                }

                for (var g = 0; g < row.Item.Count; g++)
                {
                    var id = row.Item[g].RowId;
                    if (id != 0)
                    {
                        map.TryAdd(id, (baseParam, (byte)g));
                    }
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
