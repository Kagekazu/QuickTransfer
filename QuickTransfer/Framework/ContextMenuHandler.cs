using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace QuickTransfer.Framework;

internal static unsafe class ContextMenuHandler
{
    public enum AutoContextAction
    {
        AddAllToSaddlebag,
        RemoveAllFromSaddlebag,
        PlaceInArmouryChest,
        ReturnToInventory,
        EntrustToRetainer,
        RetrieveFromRetainer,
        RemoveFromCompanyChest,
        Split,
        Sort,
        Trade,
        Sell
    }

    public enum ModifierMode
    {
        Shift,
        Ctrl,
        Alt
    }

    // Each menu row is assigned to the first action it matches in this order; Trade rows are also checked against Sell.
    private static readonly AutoContextAction[] MatchOrder =
    [
        AutoContextAction.RemoveAllFromSaddlebag,
        AutoContextAction.RemoveFromCompanyChest,
        AutoContextAction.AddAllToSaddlebag,
        AutoContextAction.PlaceInArmouryChest,
        AutoContextAction.ReturnToInventory,
        AutoContextAction.EntrustToRetainer,
        AutoContextAction.RetrieveFromRetainer,
        AutoContextAction.Split,
        AutoContextAction.Trade,
        AutoContextAction.Sell
    ];

    public static bool ContextLabelMatches(AutoContextAction desiredAction, string menuText)
    {
        var t = menuText.Trim();
        static bool Has(string s, string needle) => s.Contains(needle, StringComparison.OrdinalIgnoreCase);

        return desiredAction switch
        {
            AutoContextAction.AddAllToSaddlebag =>
                t.Equals("Add All to Saddlebag", StringComparison.OrdinalIgnoreCase) ||
                Has(t, "Add All") && Has(t, "Saddlebag"),

            AutoContextAction.RemoveAllFromSaddlebag =>
                t.Equals("Remove All from Saddlebag", StringComparison.OrdinalIgnoreCase) ||
                Has(t, "Remove All") && Has(t, "Saddlebag") ||
                Has(t, "Remove") && Has(t, "Saddlebag") ||
                t.Equals("Remove All", StringComparison.OrdinalIgnoreCase) ||
                (Has(t, "Retrieve") || Has(t, "Take out")) && Has(t, "Saddlebag"),

            AutoContextAction.PlaceInArmouryChest =>
                t.Equals("Place in Armoury Chest", StringComparison.OrdinalIgnoreCase) ||
                Has(t, "Place") && (Has(t, "Armoury") || Has(t, "Armory")) && Has(t, "Chest"),

            AutoContextAction.ReturnToInventory =>
                t.Equals("Return to Inventory", StringComparison.OrdinalIgnoreCase) ||
                Has(t, "Return") && Has(t, "Inventory"),

            AutoContextAction.EntrustToRetainer =>
                t.Equals("Entrust to Retainer", StringComparison.OrdinalIgnoreCase) ||
                Has(t, "Entrust") && Has(t, "Retainer"),

            AutoContextAction.RetrieveFromRetainer =>
                t.Equals("Retrieve from Retainer", StringComparison.OrdinalIgnoreCase) ||
                Has(t, "Retrieve") && Has(t, "Retainer"),

            AutoContextAction.RemoveFromCompanyChest =>
                t.Equals("Remove", StringComparison.OrdinalIgnoreCase) ||
                Has(t, "Remove") && (Has(t, "Company") || Has(t, "Chest")) ||
                Has(t, "Withdraw") && (Has(t, "Company") || Has(t, "Chest")),

            AutoContextAction.Split =>
                t.StartsWith("Split", StringComparison.OrdinalIgnoreCase),

            AutoContextAction.Sort =>
                t.StartsWith("Sort", StringComparison.OrdinalIgnoreCase),

            AutoContextAction.Trade =>
                t.StartsWith("Trade", StringComparison.OrdinalIgnoreCase) ||
                Has(t, "Trade") && Has(t, "Item"),

            AutoContextAction.Sell =>
                t.StartsWith("Sell", StringComparison.OrdinalIgnoreCase) ||
                Has(t, "Sell") && Has(t, "Item"),

            var _ => false
        };
    }

    public static bool TryAutoSelectFromAgent(
        AgentInventoryContext* agent,
        ModifierMode mode,
        Configuration configuration,
        out string chosenText,
        out int chosenIndex,
        ref long pendingCloseContextMenuAtMs)
    {
        chosenText = string.Empty;
        chosenIndex = -1;

        AtkUnitBase* addon = null;
        try
        {
            var agentAddonId = agent->AgentInterface.GetAddonId();
            if (agentAddonId != 0)
            {
                addon = InventoryHelpers.GetAddonById(agentAddonId);
            }
        }
        catch
        {
        }

        if (addon is null)
        {
            addon = InventoryHelpers.GetAddonByName(QuickTransferConstants.ContextMenuAddonName);
        }

        return addon is not null && TryAutoSelectAndClose(
            agent,
            addon,
            mode,
            configuration,
            out chosenText,
            out chosenIndex,
            ref pendingCloseContextMenuAtMs);
    }

    public static void TryCloseCurrentContextMenu(AgentInventoryContext* agent)
    {
        try
        {
            var agentAddonId = agent->AgentInterface.GetAddonId();
            if (agentAddonId != 0)
            {
                var addon = InventoryHelpers.GetAddonById(agentAddonId);
                if (addon is not null)
                {
                    CloseContextMenuAddon(agent, addon);
                    return;
                }
            }
        }
        catch
        {
        }

        try
        {
            var cm = InventoryHelpers.GetAddonByName(QuickTransferConstants.ContextMenuAddonName);
            if (cm is not null)
            {
                CloseContextMenuAddon(agent, cm);
            }
        }
        catch
        {
        }
    }

    public static void CloseContextMenuAddon(AgentInventoryContext* agent, AtkUnitBase* contextMenuAddon)
    {
        try { agent->AgentInterface.Hide(); }
        catch { }

        try { contextMenuAddon->Hide(false, true, 0); }
        catch { }
    }

    public static bool TryAutoSelectAndClose(
        AgentInventoryContext* agent,
        AtkUnitBase* contextMenuAddon,
        ModifierMode mode,
        Configuration configuration,
        out string chosenText,
        out int chosenIndex,
        ref long pendingCloseContextMenuAtMs)
    {
        chosenText = string.Empty;
        chosenIndex = -1;

        var items = ReadMenuItems(agent, 64);
        if (items.Count == 0)
        {
            return false;
        }

        Dictionary<AutoContextAction, (int Index, string Text)> found = [];
        foreach (var (index, text) in items)
        {
            foreach (var action in MatchOrder)
            {
                if (found.ContainsKey(action) || !ContextLabelMatches(action, text))
                {
                    continue;
                }

                found[action] = (index, text);
                if (action != AutoContextAction.Trade)
                {
                    break;
                }
            }
        }

        foreach (var action in GetActionPreference(mode, configuration, found))
        {
            if (!found.TryGetValue(action, out var hit))
            {
                continue;
            }

            AtkValueHelpers.GenerateCallback(contextMenuAddon, 0, hit.Index, 0U, 0, 0);

            // Split and Trade open a follow-up dialog; closing the menu immediately can cancel it.
            if (ContextLabelMatches(AutoContextAction.Split, hit.Text) ||
                ContextLabelMatches(AutoContextAction.Trade, hit.Text))
            {
                pendingCloseContextMenuAtMs = Environment.TickCount64 + 3000;
            }
            else
            {
                CloseContextMenuAddon(agent, contextMenuAddon);
            }

            chosenText = hit.Text;
            chosenIndex = hit.Index;
            return true;
        }

        return false;
    }

    private static AutoContextAction[] GetActionPreference(
        ModifierMode mode,
        Configuration configuration,
        Dictionary<AutoContextAction, (int Index, string Text)> found)
    {
        if (mode == ModifierMode.Alt)
        {
            return [AutoContextAction.Split];
        }

        if (mode == ModifierMode.Shift && configuration.EnableVendorQuickSell && InventoryHelpers.IsVendorOpen())
        {
            return [AutoContextAction.Sell];
        }

        if (mode == ModifierMode.Shift && InventoryHelpers.IsTradeOpen())
        {
            return [AutoContextAction.Trade];
        }

        if (mode == ModifierMode.Shift && configuration.EnableCompanyChest && InventoryHelpers.IsCompanyChestOpen())
        {
            return [AutoContextAction.RemoveFromCompanyChest];
        }

        if (mode == ModifierMode.Ctrl)
        {
            return [AutoContextAction.ReturnToInventory, AutoContextAction.PlaceInArmouryChest];
        }

        var saddlebagOpen = InventoryHelpers.IsSaddlebagOpen();
        if (InventoryHelpers.IsRetainerOpen() ||
            found.ContainsKey(AutoContextAction.EntrustToRetainer) ||
            found.ContainsKey(AutoContextAction.RetrieveFromRetainer))
        {
            return saddlebagOpen
                ? [AutoContextAction.AddAllToSaddlebag, AutoContextAction.EntrustToRetainer, AutoContextAction.RemoveAllFromSaddlebag]
                : [AutoContextAction.RetrieveFromRetainer, AutoContextAction.EntrustToRetainer];
        }

        return saddlebagOpen
            ? [AutoContextAction.RemoveAllFromSaddlebag, AutoContextAction.AddAllToSaddlebag]
            : [AutoContextAction.PlaceInArmouryChest, AutoContextAction.ReturnToInventory];
    }

    public static bool TrySelectSortAndClose(AgentInventoryContext* agent, AtkUnitBase* contextMenuAddon, out string chosenText, out int chosenIndex)
    {
        chosenText = string.Empty;
        chosenIndex = -1;

        var hasUndoSort = false;
        foreach (var (index, text) in ReadMenuItems(agent, 64))
        {
            if (text.Trim().Equals("Undo Sort", StringComparison.OrdinalIgnoreCase))
            {
                hasUndoSort = true;
            }

            if (!ContextLabelMatches(AutoContextAction.Sort, text))
            {
                continue;
            }

            AtkValueHelpers.GenerateCallback(contextMenuAddon, 0, index, 0U, 0, 0);
            CloseContextMenuAddon(agent, contextMenuAddon);
            chosenText = text;
            chosenIndex = index;
            return true;
        }

        // Only "Undo Sort" is offered when the container is already sorted.
        if (hasUndoSort)
        {
            CloseContextMenuAddon(agent, contextMenuAddon);
            chosenText = "Already sorted";
            return true;
        }

        return false;
    }

    public static void DebugDumpContextMenu(AgentInventoryContext* agent, int maxItems)
    {
        try
        {
            foreach (var (index, text) in ReadMenuItems(agent, maxItems))
            {
                Svc.Log.Information($"[QuickTransfer] Menu idx={index}: '{text}'");
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[QuickTransfer] Failed to dump context menu.");
        }
    }

    private static List<(int Index, string Text)> ReadMenuItems(AgentInventoryContext* agent, int maxItems)
    {
        List<(int Index, string Text)> items = [];
        var max = Math.Min(Math.Min(agent->ContextItemCount, 64), maxItems);
        for (var i = 0; i < max; i++)
        {
            var param = agent->EventParams[agent->ContexItemStartIndex + i];
            if (param.Type is not (AtkValueType.String or AtkValueType.ManagedString))
            {
                continue;
            }

            var text = AtkValueHelpers.ReadAtkValueString(param);
            if (!string.IsNullOrWhiteSpace(text))
            {
                items.Add((i, text));
            }
        }

        return items;
    }
}
