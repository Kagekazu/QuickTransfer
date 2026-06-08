using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace QuickTransfer;

/// <summary>
///     Handles context menu selection and matching logic.
/// </summary>
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

    // Access services through Plugin's static properties

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
                (Has(t, "Retrieve") || Has(t, "Take out") || Has(t, "Take Out")) && Has(t, "Saddlebag"),

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
                t.Equals("Split", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("Split", StringComparison.OrdinalIgnoreCase),

            AutoContextAction.Sort =>
                t.Equals("Sort", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("Sort", StringComparison.OrdinalIgnoreCase),

            AutoContextAction.Trade =>
                t.Equals("Trade", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("Trade", StringComparison.OrdinalIgnoreCase) ||
                Has(t, "Trade") && Has(t, "Item"),

            AutoContextAction.Sell =>
                t.Equals("Sell", StringComparison.OrdinalIgnoreCase) ||
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
                addon = AddonHelpers.GetAddonById(agentAddonId);
        }
        catch
        {
            /* ignore */
        }

        if (addon == null)
            addon = AddonHelpers.GetAddonByName(QuickTransferConstants.ContextMenuAddonName);
        return addon != null && TryAutoSelectAndClose(
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
                var addon = AddonHelpers.GetAddonById(agentAddonId);
                if (addon != null)
                {
                    CloseContextMenuAddon(agent, addon);
                    return;
                }
            }
        }
        catch
        {
            /* ignore */
        }

        try
        {
            var cm = AddonHelpers.GetAddonByName(QuickTransferConstants.ContextMenuAddonName);
            if (cm != null)
                CloseContextMenuAddon(agent, cm);
        }
        catch
        {
            /* ignore */
        }
    }

    public static void CloseContextMenuAddon(AgentInventoryContext* agent, AtkUnitBase* contextMenuAddon)
    {
        try { agent->AgentInterface.Hide(); }
        catch
        {
            /* ignore */
        }
        try { contextMenuAddon->Hide(false, true, 0); }
        catch
        {
            /* ignore */
        }
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

        // Single-pass: decode each label once, record first match per action.
        var foundAny = false;

        int removeIdx = -1, addIdx = -1, placeIdx = -1, returnIdx = -1, entrustIdx = -1, retrieveIdx = -1, companyRemoveIdx = -1, splitIdx = -1, tradeIdx = -1, sellIdx = -1;
        string? removeTxt = null, addTxt = null, placeTxt = null, returnTxt = null, entrustTxt = null, retrieveTxt = null, companyRemoveTxt = null, splitTxt = null, tradeTxt = null, sellTxt = null;

        var max = Math.Min(agent->ContextItemCount, 64);
        for (var i = 0; i < max; i++)
        {
            var param = agent->EventParams[agent->ContexItemStartIndex + i];
            if (param.Type is not (AtkValueType.String or AtkValueType.ManagedString))
                continue;

            var text = AtkValueHelpers.ReadAtkValueString(param);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            foundAny = true;

            // Priority matters: we want the first matching index for each action.
            if (removeIdx < 0 && ContextLabelMatches(AutoContextAction.RemoveAllFromSaddlebag, text))
            {
                removeIdx = i;
                removeTxt = text;
                continue;
            }

            if (companyRemoveIdx < 0 && ContextLabelMatches(AutoContextAction.RemoveFromCompanyChest, text))
            {
                companyRemoveIdx = i;
                companyRemoveTxt = text;
                continue;
            }

            if (addIdx < 0 && ContextLabelMatches(AutoContextAction.AddAllToSaddlebag, text))
            {
                addIdx = i;
                addTxt = text;
                continue;
            }

            if (placeIdx < 0 && ContextLabelMatches(AutoContextAction.PlaceInArmouryChest, text))
            {
                placeIdx = i;
                placeTxt = text;
                continue;
            }

            if (returnIdx < 0 && ContextLabelMatches(AutoContextAction.ReturnToInventory, text))
            {
                returnIdx = i;
                returnTxt = text;
                continue;
            }

            if (entrustIdx < 0 && ContextLabelMatches(AutoContextAction.EntrustToRetainer, text))
            {
                entrustIdx = i;
                entrustTxt = text;
                continue;
            }

            if (retrieveIdx < 0 && ContextLabelMatches(AutoContextAction.RetrieveFromRetainer, text))
            {
                retrieveIdx = i;
                retrieveTxt = text;
                continue;
            }

            if (splitIdx < 0 && ContextLabelMatches(AutoContextAction.Split, text))
            {
                splitIdx = i;
                splitTxt = text;
                continue;
            }

            if (tradeIdx < 0 && ContextLabelMatches(AutoContextAction.Trade, text))
            {
                tradeIdx = i;
                tradeTxt = text;
            }

            if (sellIdx < 0 && ContextLabelMatches(AutoContextAction.Sell, text))
            {
                sellIdx = i;
                sellTxt = text;
            }
        }

        if (!foundAny)
            return false;

        var saddlebagOpen = InventoryHelpers.IsSaddlebagOpen();
        var retainerOpen = InventoryHelpers.IsRetainerOpen();
        var companyChestOpen = InventoryHelpers.IsCompanyChestOpen();
        var tradeOpen = InventoryHelpers.IsTradeOpen();
        var vendorOpen = InventoryHelpers.IsVendorOpen();

        // Choose the best action that exists in the menu.
        (int idx, string? txt) chosen;
        if (mode == ModifierMode.Alt)
        {
            chosen = splitIdx >= 0 ? (splitIdx, splitTxt) : (-1, null);
        }
        else if (mode == ModifierMode.Shift && vendorOpen && configuration.EnableVendorQuickSell)
        {
            // Vendor shop: prioritize Sell action when vendor is open
            chosen = sellIdx >= 0 ? (sellIdx, sellTxt) : (-1, null);
        }
        else if (mode == ModifierMode.Shift && tradeOpen)
        {
            // Trade window: prioritize Trade action when Trade window is open
            chosen = tradeIdx >= 0 ? (tradeIdx, tradeTxt) : (-1, null);
        }
        else if (mode == ModifierMode.Shift && companyChestOpen && configuration.EnableCompanyChest)
        {
            chosen = companyRemoveIdx >= 0 ? (companyRemoveIdx, companyRemoveTxt) : (-1, null);
        }
        else if (mode == ModifierMode.Ctrl)
        {
            chosen = returnIdx >= 0 ? (returnIdx, returnTxt) :
                placeIdx >= 0 ? (placeIdx, placeTxt) :
                (-1, null);
        }
        else if (retainerOpen)
        {
            if (saddlebagOpen)
            {
                // Retainer <-> Saddlebag:
                // - Retainer item: Add All to Saddlebag
                // - Saddlebag item: Entrust to Retainer
                chosen = addIdx >= 0 ? (addIdx, addTxt) :
                    entrustIdx >= 0 ? (entrustIdx, entrustTxt) :
                    // last-resort fallback
                    removeIdx >= 0 ? (removeIdx, removeTxt) :
                    (-1, null);
            }
            else
            {
                // Retainer <-> Player (Inventory/Armoury):
                // - Retainer item: Retrieve from Retainer
                // - Player item: Entrust to Retainer
                chosen = retrieveIdx >= 0 ? (retrieveIdx, retrieveTxt) :
                    entrustIdx >= 0 ? (entrustIdx, entrustTxt) :
                    (-1, null);
            }
        }
        else if (saddlebagOpen)
        {
            chosen = removeIdx >= 0 ? (removeIdx, removeTxt) :
                addIdx >= 0 ? (addIdx, addTxt) :
                (-1, null);
        }
        else
        {
            chosen = placeIdx >= 0 ? (placeIdx, placeTxt) :
                returnIdx >= 0 ? (returnIdx, returnTxt) :
                (-1, null);
        }

        if (chosen.idx < 0 || string.IsNullOrWhiteSpace(chosen.txt))
            return false;

        AtkValueHelpers.GenerateCallback(contextMenuAddon, 0, chosen.idx, 0U, 0, 0);

        // Some actions (notably Split and Trade) can be cancelled if we close the menu immediately.
        // Delay the close slightly to allow the follow-up UI (InputNumeric) to spawn.
        if (chosen.txt != null && (ContextLabelMatches(AutoContextAction.Split, chosen.txt) || ContextLabelMatches(AutoContextAction.Trade, chosen.txt)))
        {
            // Don't close immediately: on some setups this cancels the action before InputNumeric opens.
            // We'll keep the menu invisible (via suppression) and close it later as a cleanup.
            pendingCloseContextMenuAtMs = Environment.TickCount64 + 3000;
        }
        else
        {
            CloseContextMenuAddon(agent, contextMenuAddon);
        }

        chosenText = chosen.txt!;
        chosenIndex = chosen.idx;
        return true;
    }

    public static bool TrySelectSortAndClose(AgentInventoryContext* agent, AtkUnitBase* contextMenuAddon, out string chosenText, out int chosenIndex)
    {
        chosenText = string.Empty;
        chosenIndex = -1;

        var undoSortIdx = -1;

        var max = Math.Min(agent->ContextItemCount, 64);
        for (var i = 0; i < max; i++)
        {
            var param = agent->EventParams[agent->ContexItemStartIndex + i];
            if (param.Type is not (AtkValueType.String or AtkValueType.ManagedString))
                continue;

            var text = AtkValueHelpers.ReadAtkValueString(param);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            // If Sort isn't present (because the container is already sorted), the menu often contains "Undo Sort" instead.
            // We treat that as "already sorted" and do nothing (closing the menu).
            if (undoSortIdx < 0 && text.Trim().Equals("Undo Sort", StringComparison.OrdinalIgnoreCase))
            {
                undoSortIdx = i;
            }

            if (!ContextLabelMatches(AutoContextAction.Sort, text))
                continue;

            AtkValueHelpers.GenerateCallback(contextMenuAddon, 0, i, 0U, 0, 0);
            CloseContextMenuAddon(agent, contextMenuAddon);
            chosenText = text;
            chosenIndex = i;
            return true;
        }

        // No "Sort" entry. If "Undo Sort" exists, we're already sorted; close the menu without changing state.
        if (undoSortIdx >= 0)
        {
            try { CloseContextMenuAddon(agent, contextMenuAddon); }
            catch
            {
                /* ignore */
            }
            chosenText = "Already sorted";
            chosenIndex = -1;
            return true;
        }

        return false;
    }

    public static bool ContainsString(AtkUnitBase* ctxAddon, string needle, bool debugMode)
    {
        try
        {
            if (ctxAddon == null || ctxAddon->AtkValues == null || ctxAddon->AtkValuesCount <= 0)
                return false;

            var count = Math.Min((int)ctxAddon->AtkValuesCount, 128);
            if (debugMode)
                Svc.Log.Information($"[QuickTransfer] ContextMenu AtkValuesCount={ctxAddon->AtkValuesCount} (scanning {count}).");
            for (var i = 0; i < count; i++)
            {
                var v = ctxAddon->AtkValues[i];
                if (v.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString))
                    continue;

                var s = AtkValueHelpers.ReadAtkValueString(v);
                if (string.IsNullOrWhiteSpace(s))
                    continue;

                if (debugMode)
                    Svc.Log.Information($"[QuickTransfer] ContextMenu AtkValue[{i}] = '{s}'");

                if (s.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    if (debugMode)
                        Svc.Log.Information($"[QuickTransfer] ContextMenu contains '{needle}' (found '{s}' at AtkValue[{i}]).");
                    return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    public static void DebugDumpContextMenu(AgentInventoryContext* agent, int maxItems)
    {
        try
        {
            var max = Math.Min(Math.Min(agent->ContextItemCount, 64), maxItems);
            for (var i = 0; i < max; i++)
            {
                var param = agent->EventParams[agent->ContexItemStartIndex + i];
                if (param.Type is not (AtkValueType.String or AtkValueType.ManagedString))
                    continue;

                var text = AtkValueHelpers.ReadAtkValueString(param);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                Svc.Log.Information($"[QuickTransfer] Menu idx={i}: '{text}'");
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[QuickTransfer] Failed to dump context menu.");
        }
    }
}
