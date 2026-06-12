using FFXIVClientStructs.FFXIV.Component.GUI;
namespace QuickTransfer;

/// <summary>
///     ClientStructs-based addon lookup (replaces IGameGui.GetAddonByName for native pointers).
/// </summary>
internal static unsafe class AddonHelpers
{
    private static AtkUnitManager* UnitManager
    {
        get
        {
            try
            {
                AtkStage* stage = AtkStage.Instance();
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
                return null;

            AtkUnitManager* mgr = UnitManager;
            if (mgr == null)
                return null;

            AtkUnitBase* addon = mgr->GetAddonById((ushort)id);
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
                return null;

            AtkUnitManager* mgr = UnitManager;
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
            return false;

        int limit = Math.Max(1, maxIndex);
        for(int i = 1; i <= limit; i++)
        {
            AtkUnitBase* candidate = GetAddonByName(addonName, i);
            if (candidate == null || !candidate->IsVisible)
                continue;

            addon = candidate;
            return true;
        }

        return false;
    }
}
