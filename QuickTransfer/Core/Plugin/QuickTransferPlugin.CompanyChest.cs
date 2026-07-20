using Dalamud.Game.Chat;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Runtime.InteropServices;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace QuickTransfer;

public sealed unsafe partial class QuickTransferPlugin
{
    private static void TryGetSlotSnapshot(InventoryManager* inv,
        InventoryType type,
        uint slot,
        out uint itemId,
        out int qty)
    {
        itemId = 0;
        qty = 0;
        try
        {
            if (inv == null)
            {
                return;
            }
            var it = inv->GetInventorySlot(type, (int)slot);
            if (it == null)
            {
                return;
            }
            itemId = it->ItemId;
            qty = it->Quantity;
        }
        catch
        {
            // ignored
        }
    }
    private InventoryType[] GetCompanyChestInventoryTypes()
    {
        // Don't hardcode enum names; discover them by name at runtime so we don't break across patches/structs.
        // Limit to the configured number of item compartments (default 3; can be upgraded to 5).
        var max = Math.Clamp(Configuration.CompanyChestCompartments, 3, 5);
        return
        [
            .. Enum.GetValues<InventoryType>()
                .Where(InventoryHelpers.IsCompanyChestType)
                .OrderBy(v => (int)v)
                .Take(max)
        ];
    }

    private static InventoryType[] GetAllCompanyChestItemPages()
        =>
        [
            .. Enum.GetValues<InventoryType>()
                .Where(InventoryHelpers.IsCompanyChestType)
                .OrderBy(v => (int)v)
                .Take(5)
        ];

    private bool TryResolveCompanyChestPageFromAddon(AtkUnitBase* addon, out InventoryType page)
    {
        page = default;
        try
        {
            if (addon == null)
            {
                return false;
            }

            // Scan component nodes for any DragDrop/List that yields a FreeCompanyPageX payload.
            var nodeCount = addon->UldManager.NodeListCount;
            if (nodeCount <= 0)
            {
                return false;
            }

            var maxNodes = Math.Min((int)nodeCount, 2000);
            InventoryType bestPage = default;
            var bestHits = 0;

            // Track the most frequently observed FreeCompanyPageX among *visible* nodes.
            // Rationale: the FC chest addon often keeps nodes for other tabs alive but hidden; a "first match wins"
            // scan can return the wrong tab (observed off-by-one behavior).
            Dictionary<InventoryType, int> hitsByPage = [];
            for (var i = 0; i < maxNodes; i++)
            {
                var n = addon->UldManager.NodeList[i];
                if (n == null)
                {
                    continue;
                }

                // Skip hidden nodes (inactive tabs commonly force alpha to 0).
                try
                {
                    if (n->Alpha_2 == 0 || n->Color.A == 0)
                    {
                        continue;
                    }
                }
                catch
                {
                    // ignore; continue scanning
                }

                AtkComponentNode* compNode;
                try { compNode = n->GetAsAtkComponentNode(); }
                catch { continue; }
                if (compNode == null || compNode->Component == null)
                {
                    continue;
                }

                var component = compNode->Component;
                var ct = component->GetComponentType();
                if (ct == ComponentType.List)
                {
                    var list = (AtkComponentList*)component;
                    // Try a few indices; FC chest lists usually expose items here.
                    var observed = 0;
                    for (var li = 0; li < 30; li++)
                    {
                        var ddi = DragDropHelpers.TryGetDdiFromListIndex(list, li);
                        if (ddi == null || (nint)ddi < QuickTransferConstants.MinLikelyPointer)
                        {
                            continue;
                        }

                        if (DragDropHelpers.TryGetSlotFromDragDropInterface(ddi, out var invType, out var _))
                        {
                            if (InventoryHelpers.IsCompanyChestDestinationType(invType))
                            {
                                hitsByPage.TryGetValue(invType, out var cur);
                                cur++;
                                hitsByPage[invType] = cur;
                                if (cur > bestHits)
                                {
                                    bestHits = cur;
                                    bestPage = invType;
                                }

                                // Don't over-scan; we just need enough evidence to pick the visible page.
                                observed++;
                                if (observed >= 6)
                                {
                                    break;
                                }
                            }
                        }
                    }
                }
                else
                {
                    var ddi = DragDropHelpers.TryGetDdiFromComponent(component);
                    if (ddi == null || (nint)ddi < QuickTransferConstants.MinLikelyPointer)
                    {
                        continue;
                    }

                    if (DragDropHelpers.TryGetSlotFromDragDropInterface(ddi, out var invType, out var _))
                    {
                        if (InventoryHelpers.IsCompanyChestDestinationType(invType))
                        {
                            hitsByPage.TryGetValue(invType, out var cur);
                            cur++;
                            hitsByPage[invType] = cur;
                            if (cur > bestHits)
                            {
                                bestHits = cur;
                                bestPage = invType;
                            }
                        }
                    }
                }
            }

            if (bestHits > 0 && InventoryHelpers.IsCompanyChestDestinationType(bestPage))
            {
                page = bestPage;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private void ObserveCompanyChestTabFromAtkValues(AtkUnitBase* addon, InventoryType selectedPage)
    {
        try
        {
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount <= 0)
            {
                return;
            }

            var values = addon->AtkValues;
            int count = addon->AtkValuesCount;
            var max = Math.Min(count, 80);

            for (var i = 0; i < max; i++)
            {
                if (!AtkValueHelpers.TryGetAtkValueInt(values, max, i, out var n))
                {
                    continue;
                }

                // Only small integers are plausible "tab indices".
                if (n is < 0 or > 10)
                {
                    continue;
                }

                if (!companyChestSelectedTabCandidates.TryGetValue(i, out var map))
                {
                    map = [];
                    companyChestSelectedTabCandidates[i] = map;
                }

                // If we see conflicting mappings for the same (index,value), drop this candidate index.
                if (map.TryGetValue(n, out var existing) && existing != selectedPage)
                {
                    companyChestSelectedTabCandidates.Remove(i);
                    continue;
                }

                map[n] = selectedPage;
            }

            // Pick the best candidate index (most distinct pages mapped).
            var bestIdx = -1;
            var bestDistinct = 0;
            foreach (var kv in companyChestSelectedTabCandidates)
            {
                var distinct = kv.Value.Values.Distinct().Count();
                if (distinct > bestDistinct)
                {
                    bestDistinct = distinct;
                    bestIdx = kv.Key;
                }
            }

            if (bestIdx >= 0 && bestDistinct >= 2)
            {
                if (companyChestSelectedTabAtkValueIndex != bestIdx)
                {
                    companyChestSelectedTabAtkValueIndex = bestIdx;
                    if (Configuration.DebugMode)
                    {
                        Svc.Log.Information($"[QuickTransfer] FC Chest AtkValues selected-tab index inferred: idx={bestIdx} (mappedPages={bestDistinct}).");
                    }
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private bool TryResolveCompanyChestSelectedPageFromAtkValues(uint addonId, out InventoryType page)
    {
        page = default;
        try
        {
            if (companyChestSelectedTabAtkValueIndex < 0)
            {
                return false;
            }

            if (!InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out var addon, QuickTransferConstants.WideAddonSearchMaxIndex) || addon == null || addon->Id != addonId)
            {
                return false;
            }

            if (addon->AtkValues == null || addon->AtkValuesCount <= 0)
            {
                return false;
            }

            if (!companyChestSelectedTabCandidates.TryGetValue(companyChestSelectedTabAtkValueIndex, out var map) || map.Count == 0)
            {
                return false;
            }

            if (!AtkValueHelpers.TryGetAtkValueInt(addon->AtkValues, addon->AtkValuesCount, companyChestSelectedTabAtkValueIndex, out var n))
            {
                return false;
            }

            if (!map.TryGetValue(n, out var p))
            {
                return false;
            }

            if (!InventoryHelpers.IsCompanyChestDestinationType(p))
            {
                return false;
            }

            page = p;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryResolveCompanyChestActivePage(long now, out InventoryType page)
    {
        page = default;
        try
        {
            if (!InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out var fcc, QuickTransferConstants.WideAddonSearchMaxIndex) || fcc == null)
            {
                return false;
            }

            uint addonId = fcc->Id;
            const long companyChestTabMaxAgeMs = 180000; // 3 minutes

            // Prefer the page currently displayed in the addon (visible drag-drop payloads).
            if (TryResolveCompanyChestPageFromAddon(fcc, out var curPage) && InventoryHelpers.IsCompanyChestDestinationType(curPage))
            {
                page = curPage;
                return true;
            }

            var lp = lastHoverCompanyChestPage;
            if (lp != null && lp.Value.AddonId == addonId && now - lp.Value.SeenAtMs <= companyChestTabMaxAgeMs && InventoryHelpers.IsCompanyChestDestinationType(lp.Value.Page))
            {
                page = lp.Value.Page;
                return true;
            }

            var sp = lastSelectedCompanyChestPage;
            if (sp != null && sp.Value.AddonId == addonId && now - sp.Value.SeenAtMs <= companyChestTabMaxAgeMs && InventoryHelpers.IsCompanyChestDestinationType(sp.Value.Page))
            {
                page = sp.Value.Page;
                return true;
            }

            if (TryResolveCompanyChestSelectedPageFromAtkValues(addonId, out var atkPage) && InventoryHelpers.IsCompanyChestDestinationType(atkPage))
            {
                page = atkPage;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }
    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            if (!Configuration.EnableCompanyChest)
            {
                return;
            }

            // Only care while FC chest features are active; avoid doing extra work on every chat line.
            if (!companyChestOrganize.Active && !companyChestDeposit.Active)
            {
                return;
            }

            var text = message.Sender.TextValue;
            if (text.Length == 0)
            {
                return;
            }

            // These strings appear as system error toasts and (typically) also in the log/chat.
            // If we see them, stop the state machine and back off for a few seconds.
            if (text.Contains("Another player is using the chest", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Unable to store item", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Unable to complete company chest action", StringComparison.OrdinalIgnoreCase))
            {
                var now = Environment.TickCount64;
                companyChestBusyHits = Math.Min(companyChestBusyHits + 1, 10);
                long backoffMs = Math.Min(60000, 5000 * (1 << Math.Min(companyChestBusyHits - 1, 4))); // 5s,10s,20s,40s,60s cap
                companyChestBusyUntilMs = Math.Max(companyChestBusyUntilMs, now + backoffMs);

                // If the chest is busy repeatedly, stop the run and let the user try later.
                if (companyChestOrganize.Active && companyChestBusyHits >= 3)
                {
                    companyChestOrganize.Active = false;
                    if (Configuration.DebugMode)
                    {
                        Svc.Log.Information($"[QuickTransfer] (MMB) FC Chest busy hit {companyChestBusyHits}; stopping organize run. msg='{text}'");
                    }
                }
                else if (companyChestOrganize.Active)
                {
                    // Pause and retry later.
                    companyChestOrganize.WaitingForApply = false;
                    companyChestOrganize.WaitObservedChangeAtMs = 0;
                    companyChestOrganize.NextAttemptAtMs = Math.Max(companyChestOrganize.NextAttemptAtMs, companyChestBusyUntilMs + 750);
                    companyChestOrganize.ExpiresAtMs = Math.Max(companyChestOrganize.ExpiresAtMs, companyChestBusyUntilMs + 20000);
                    companyChestOrganize.WaitStuckCount = 0;
                }

                // Deposit is interactive; stop it outright on busy.
                companyChestDeposit.Active = false;
                pendingCompanyChestNumericConfirmUntilMs = 0;
                pendingCompanyChestNumericArmed = false;
                pendingNumericKind = PendingNumericKind.None;
                pendingCompanyChestNumericDesired = 0;
                pendingCompanyChestNumericHalf = false;
                pendingCompanyChestNumericValueSet = false;
                pendingCompanyChestNumericValueSetAtMs = 0;

                if (Configuration.DebugMode)
                {
                    Svc.Log.Information($"[QuickTransfer] (MMB) FC Chest busy detected from chat; backoff={backoffMs}ms (hit {companyChestBusyHits}). msg='{text}'");
                }
            }
        }
        catch
        {
            // ignore
        }
    }
    private bool StartCompanyChestDeposit(InventoryType sourceType, uint sourceSlot)
    {
        try
        {
            if (!Configuration.EnableCompanyChest)
            {
                return false;
            }
            if (RaptureAtkModule.Instance() == null)
            {
                return false;
            }
            if (!InventoryHelpers.IsCompanyChestOpen())
            {
                return false;
            }
            if (!InventoryHelpers.IsCompanyChestDepositSourceType(sourceType))
            {
                return false;
            }

            if (!InventoryHelpers.TryGetItemInfo(sourceType, (int)sourceSlot, out var itemId, out var isHq, out var qty))
            {
                return false;
            }

            var now = Environment.TickCount64;
            if (!TryResolveCompanyChestActivePage(now, out var destPage))
            {
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information("[QuickTransfer] (Shift+RClick) Company Chest deposit skipped: could not determine active tab.");
                }
                return false;
            }

            companyChestDeposit = new()
            {
                Active = true,
                SourceType = sourceType,
                SourceSlot = sourceSlot,
                ItemId = itemId,
                IsHq = isHq,
                DestPage = destPage,
                NextAttemptAtMs = now,
                ExpiresAtMs = now + 12000,
                Steps = 0,
                LastQty = qty,
                WaitForQtyChangeUntilMs = 0
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ProcessCompanyChestDeposit(long now)
    {
        if (!companyChestDeposit.Active)
        {
            return;
        }

        // Stop if conditions no longer apply.
        if (!Configuration.EnableCompanyChest || RaptureAtkModule.Instance() == null || !InventoryHelpers.IsCompanyChestOpen())
        {
            companyChestDeposit.Active = false;
            return;
        }

        if (now >= companyChestDeposit.ExpiresAtMs || companyChestDeposit.Steps >= 40)
        {
            companyChestDeposit.Active = false;
            return;
        }

        // If we just issued a move, wait for the source stack quantity to change (or for the dialog to appear).
        // This prevents spamming the same move over and over when the game hasn't applied it yet.
        if (companyChestDeposit.WaitForQtyChangeUntilMs > 0 && now <= companyChestDeposit.WaitForQtyChangeUntilMs)
        {
            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out var _))
            {
                return;
            }

            if (InventoryHelpers.TryGetItemInfo(companyChestDeposit.SourceType, (int)companyChestDeposit.SourceSlot, out var _, out var _, out var qNow) &&
                qNow != companyChestDeposit.LastQty)
            {
                companyChestDeposit.LastQty = qNow;
                companyChestDeposit.WaitForQtyChangeUntilMs = 0;
            }
            else
            {
                return;
            }
        }

        // Don't issue a new move while the quantity dialog is open.
        if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out var _))
        {
            return;
        }

        if (now < companyChestDeposit.NextAttemptAtMs)
        {
            return;
        }

        if (!InventoryHelpers.TryGetItemInfo(companyChestDeposit.SourceType, (int)companyChestDeposit.SourceSlot, out var itemId, out var isHq, out var qty) ||
            itemId == 0 ||
            qty == 0)
        {
            companyChestDeposit.Active = false;
            return;
        }

        // If the slot changed (user moved/split), stop to avoid moving the wrong thing.
        if (itemId != companyChestDeposit.ItemId || isHq != companyChestDeposit.IsHq)
        {
            companyChestDeposit.Active = false;
            return;
        }

        InventoryType[] pages = companyChestDeposit.DestPage != default && InventoryHelpers.IsCompanyChestDestinationType(companyChestDeposit.DestPage)
            ? [companyChestDeposit.DestPage]
            : TryResolveCompanyChestActivePage(now, out var activePage) && InventoryHelpers.IsCompanyChestDestinationType(activePage)
                ? [companyChestDeposit.DestPage = activePage]
                : [];
        if (pages.Length == 0)
        {
            companyChestDeposit.Active = false;
            return;
        }

        var maxStack = InventoryHelpers.GetItemStackSize(itemId);
        var needsQuantityConfirm = qty > 1 && maxStack > 1;

        InventoryType destType;
        uint destSlot;
        if (pages[0] == InventoryType.FreeCompanyCrystals)
        {
            if (!TryResolveCompanyChestCrystalDepositDestination(
                companyChestDeposit.SourceType,
                companyChestDeposit.SourceSlot,
                itemId,
                isHq,
                maxStack,
                out destSlot))
            {
                companyChestDeposit.Active = false;
                return;
            }

            destType = InventoryType.FreeCompanyCrystals;
        }
        else if (!TryResolveCompanyChestDepositDestination(pages, itemId, isHq, maxStack, out destType, out destSlot))
        {
            companyChestDeposit.Active = false;
            return;
        }

        if (!TryCompanyChestMoveItem(companyChestDeposit.SourceType, companyChestDeposit.SourceSlot, destType, destSlot, needsQuantityConfirm))
        {
            companyChestDeposit.Active = false;
            return;
        }

        companyChestDeposit.Steps++;
        companyChestDeposit.NextAttemptAtMs = now + (needsQuantityConfirm ? 600 : 350);
        companyChestDeposit.LastQty = qty;
        companyChestDeposit.WaitForQtyChangeUntilMs = now + (needsQuantityConfirm ? 2000 : 1200);

        if (Configuration.AutoConfirmCompanyChestQuantity && needsQuantityConfirm)
        {
            pendingCompanyChestNumericConfirmUntilMs = now + 1500;
            pendingCompanyChestNumericConfirmAttempts = 0;
            pendingCompanyChestNumericArmed = true;
            pendingNumericKind = PendingNumericKind.Store;
            pendingCompanyChestNumericValueSet = false;
            pendingCompanyChestNumericValueSetAtMs = 0;
            pendingCompanyChestNumericDesired = 0;
            pendingCompanyChestNumericHalf = false;
        }

        if (Configuration.DebugMode)
        {
            Svc.Log.Information($"[QuickTransfer] (Shift+RClick) Company Chest deposit step {companyChestDeposit.Steps}: {companyChestDeposit.SourceType} slot={companyChestDeposit.SourceSlot} -> {destType} slot={destSlot} (page={companyChestDeposit.DestPage}, qty={qty}, stackMax={maxStack}).");
        }
    }

    private void StartCompanyChestOrganize(long now)
    {
        if (!Configuration.EnableCompanyChest || !InventoryHelpers.IsCompanyChestOpen() || RaptureAtkModule.Instance() == null)
        {
            return;
        }

        if (now <= companyChestBusyUntilMs)
        {
            return;
        }

        if (companyChestOrganize.Active && now < companyChestOrganize.ExpiresAtMs)
        {
            // Already running; don't reset progress on repeated MMB presses.
            companyChestOrganize.ExpiresAtMs = Math.Max(companyChestOrganize.ExpiresAtMs, now + 20000);
            if (Configuration.DebugMode)
            {
                Svc.Log.Information("[QuickTransfer] (MMB) Company Chest organize already running; ignoring restart.");
            }
            return;
        }

        companyChestBusyHits = 0;

        var ownerAddonId = 0u;
        try
        {
            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out var fcc, QuickTransferConstants.WideAddonSearchMaxIndex) && fcc != null)
            {
                ownerAddonId = fcc->Id;
            }
        }
        catch
        {
            // ignore
        }

        var pages = GetCompanyChestInventoryTypes();
        if (pages.Length == 0)
        {
            return;
        }

        companyChestOrganize = new()
        {
            Active = true,
            OwnerAddonId = ownerAddonId,
            NextAttemptAtMs = now,
            ExpiresAtMs = now + 60000,
            Steps = 0,
            Phase = 0, // Stack merge -> compact -> sort
            Pages = pages,
            WaitingForApply = false,
            WaitUntilMs = 0,
            WaitStuckCount = 0,
            WaitObservedChangeAtMs = 0
        };

        if (Configuration.DebugMode)
        {
            Svc.Log.Information($"[QuickTransfer] (MMB) Company Chest organize started (pages=[{string.Join(", ", pages)}]).");
        }
    }

    private void StartCompanyChestOrganize(long now, InventoryType selectedPage)
    {
        if (!InventoryHelpers.IsCompanyChestType(selectedPage))
        {
            StartCompanyChestOrganize(now);
            return;
        }

        if (!Configuration.EnableCompanyChest || !InventoryHelpers.IsCompanyChestOpen() || RaptureAtkModule.Instance() == null)
        {
            return;
        }

        if (now <= companyChestBusyUntilMs)
        {
            return;
        }

        if (companyChestOrganize.Active && now < companyChestOrganize.ExpiresAtMs)
        {
            // If a different tab is requested, stop the old run and restart on the new tab.
            if (companyChestOrganize.Pages is { Length: 1 } && companyChestOrganize.Pages[0] != selectedPage)
            {
                companyChestOrganize.Active = false;
            }
            else
            {
                // Same tab: extend expiry but don't reset progress.
                companyChestOrganize.ExpiresAtMs = Math.Max(companyChestOrganize.ExpiresAtMs, now + 20000);
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information("[QuickTransfer] (MMB) Company Chest organize already running; ignoring restart.");
                }
                return;
            }
        }

        companyChestBusyHits = 0;

        var ownerAddonId = 0u;
        try
        {
            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out var fcc, QuickTransferConstants.WideAddonSearchMaxIndex) && fcc != null)
            {
                ownerAddonId = fcc->Id;
            }
        }
        catch
        {
            // ignore
        }

        companyChestOrganize = new()
        {
            Active = true,
            OwnerAddonId = ownerAddonId,
            NextAttemptAtMs = now,
            ExpiresAtMs = now + 60000,
            Steps = 0,
            Phase = 0, // Stack merge -> compact -> sort
            Pages = [selectedPage],
            WaitingForApply = false,
            WaitUntilMs = 0,
            WaitStuckCount = 0,
            WaitObservedChangeAtMs = 0
        };

        if (Configuration.DebugMode)
        {
            Svc.Log.Information($"[QuickTransfer] (MMB) Company Chest organize started (selectedPage={selectedPage}).");
        }
    }

    private void ProcessCompanyChestOrganize(long now)
    {
        void LogSkip(string reason)
        {
            if (!Configuration.DebugMode)
            {
                return;
            }

            // Rate-limit skip logs; only log when the reason changes or every 2s.
            if (!string.Equals(lastCompanyChestOrganizeSkipReason, reason, StringComparison.Ordinal) ||
                now - lastCompanyChestOrganizeSkipLogMs >= 2000)
            {
                lastCompanyChestOrganizeSkipReason = reason;
                lastCompanyChestOrganizeSkipLogMs = now;
                Svc.Log.Information($"[QuickTransfer] (MMB) Company Chest organize waiting: {reason}");
            }
        }

        (int stepDelayMs, int stabilizeMs, int applyTimeoutMs, int noApplyBackoffMs, int pageRetryMs, int numericStepDelayMs, int numericApplyTimeoutMs) GetTimings()
        {
            // Start fast, but if the server begins rejecting actions (busyHits>0), automatically slow down.
            var tier = Math.Clamp(companyChestBusyHits, 0, 2);
            return tier switch
            {
                0 => (750, 300, 1300, 650, 350, 1500, 3200),
                1 => (1000, 450, 1800, 900, 500, 2200, 4500),
                var _ => (1300, 650, 2500, 1200, 750, 3000, 6000)
            };
        }

        if (!companyChestOrganize.Active)
        {
            return;
        }

        if (now <= companyChestBusyUntilMs)
        {
            LogSkip("busy backoff");
            return;
        }

        if (!Configuration.EnableCompanyChest || RaptureAtkModule.Instance() == null || !InventoryHelpers.IsCompanyChestOpen())
        {
            companyChestOrganize.Active = false;
            return;
        }

        if (now >= companyChestOrganize.ExpiresAtMs || companyChestOrganize.Steps >= 140)
        {
            companyChestOrganize.Active = false;
            return;
        }

        if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out var _))
        {
            LogSkip("InputNumeric visible");
            return;
        }

        // If the selected page isn't loaded yet (loading spinner), wait.
        try
        {
            var pages0 = companyChestOrganize.Pages;
            var inv0 = InventoryManager.Instance();
            if (inv0 != null && pages0.Length > 0)
            {
                var allLoaded = true;
                foreach (var p in pages0)
                {
                    if (!InventoryHelpers.IsContainerLoaded(inv0, p))
                    {
                        allLoaded = false;
                        break;
                    }

                    // Extra readiness guard: even if the container reports loaded, slot pointers can be null for a bit.
                    // If we treat that as "no moves", the organizer will instantly finish without doing anything.
                    if (inv0->GetInventorySlot(p, 0) == null)
                    {
                        allLoaded = false;
                        break;
                    }
                }

                if (!allLoaded)
                {
                    var t = GetTimings();
                    companyChestOrganize.NextAttemptAtMs = now + t.pageRetryMs;
                    LogSkip($"pages not ready yet; waiting. pages=[{string.Join(", ", pages0)}]");
                    return;
                }
            }
        }
        catch
        {
            // ignore
        }

        // Wait for the previous move to apply (Company Chest actions can lag and will fail/spam errors if we spam moves).
        if (companyChestOrganize.WaitingForApply)
        {
            try
            {
                var inv = InventoryManager.Instance();
                if (inv != null)
                {
                    var s = inv->GetInventorySlot(companyChestOrganize.WaitSrcType, (int)companyChestOrganize.WaitSrcSlot);
                    var d = inv->GetInventorySlot(companyChestOrganize.WaitDstType, (int)companyChestOrganize.WaitDstSlot);

                    var sId = s != null ? s->ItemId : 0u;
                    var sQty = s != null ? s->Quantity : 0;
                    var dId = d != null ? d->ItemId : 0u;
                    var dQty = d != null ? d->Quantity : 0;

                    var applied =
                        sId != companyChestOrganize.WaitSrcItemId ||
                        sQty != companyChestOrganize.WaitSrcQty ||
                        dId != companyChestOrganize.WaitDstItemId ||
                        dQty != companyChestOrganize.WaitDstQty;

                    if (applied)
                    {
                        var t = GetTimings();
                        // We saw a change; wait a short stabilization window in case the server rejects and rolls back.
                        if (companyChestOrganize.WaitObservedChangeAtMs == 0)
                        {
                            companyChestOrganize.WaitObservedChangeAtMs = now;
                            LogSkip("waiting for apply (stabilize)");
                            return;
                        }

                        if (now - companyChestOrganize.WaitObservedChangeAtMs < t.stabilizeMs)
                        {
                            LogSkip("waiting for apply (stabilize)");
                            return;
                        }

                        // Stable: allow next move.
                        companyChestOrganize.WaitingForApply = false;
                        companyChestOrganize.WaitUntilMs = 0;
                        companyChestOrganize.WaitStuckCount = 0;
                        companyChestOrganize.WaitObservedChangeAtMs = 0;
                    }
                    else if (companyChestOrganize.WaitObservedChangeAtMs != 0)
                    {
                        // We previously saw a change, but now we're back to the pre-snapshot: likely a server rejection rollback.
                        companyChestBusyHits = Math.Min(companyChestBusyHits + 1, 10);
                        long backoffMs = Math.Min(60000, 5000 * (1 << Math.Min(companyChestBusyHits - 1, 4)));
                        companyChestBusyUntilMs = Math.Max(companyChestBusyUntilMs, now + backoffMs);
                        companyChestOrganize.WaitingForApply = false;
                        companyChestOrganize.WaitObservedChangeAtMs = 0;

                        if (Configuration.DebugMode)
                        {
                            Svc.Log.Information($"[QuickTransfer] (MMB) Company Chest move rolled back; treating as busy. backoff={backoffMs}ms (hit {companyChestBusyHits}).");
                        }

                        if (companyChestBusyHits >= 3)
                        {
                            companyChestOrganize.Active = false;
                        }
                        return;
                    }
                    else if (now <= companyChestOrganize.WaitUntilMs)
                    {
                        LogSkip("waiting for apply");
                        return;
                    }
                    else
                    {
                        companyChestOrganize.WaitStuckCount++;
                        companyChestOrganize.WaitingForApply = false;
                        companyChestOrganize.WaitObservedChangeAtMs = 0;
                        if (companyChestOrganize.WaitStuckCount >= 3)
                        {
                            companyChestOrganize.Active = false;
                            if (Configuration.DebugMode)
                            {
                                Svc.Log.Information("[QuickTransfer] (MMB) Company Chest organize stalled (no inventory change observed); stopping to avoid spam.");
                                Svc.Log.Information(
                                    $"[QuickTransfer] (MMB) Stall snapshot: src={companyChestOrganize.WaitSrcType} slot={companyChestOrganize.WaitSrcSlot} " +
                                    $"was(id={companyChestOrganize.WaitSrcItemId},qty={companyChestOrganize.WaitSrcQty}) now(id={sId},qty={sQty}); " +
                                    $"dst={companyChestOrganize.WaitDstType} slot={companyChestOrganize.WaitDstSlot} " +
                                    $"was(id={companyChestOrganize.WaitDstItemId},qty={companyChestOrganize.WaitDstQty}) now(id={dId},qty={dQty});");
                            }
                            return;
                        }

                        // Back off a bit and retry.
                        var t = GetTimings();
                        companyChestOrganize.NextAttemptAtMs = now + t.noApplyBackoffMs;
                        LogSkip("no apply observed; backoff");
                        return;
                    }
                }
            }
            catch
            {
                // ignore; fall through
            }
        }

        if (now < companyChestOrganize.NextAttemptAtMs)
        {
            LogSkip("cooldown");
            return;
        }

        var pages = companyChestOrganize.Pages;
        if (pages.Length == 0)
        {
            companyChestOrganize.Active = false;
            return;
        }

        // Phase 0: merge stacks where possible. (Disabled for FC chest by starting at Phase=1.)
        if (companyChestOrganize.Phase == 0)
        {
            if (TryFindCompanyChestMergeMove(pages, out var srcType, out var srcSlot, out var dstType, out var dstSlot, out var needsNumeric))
            {
                // Snapshot BEFORE issuing the move (so we can detect when it applies).
                var preSrcId = 0u;
                var preDstId = 0u;
                var preSrcQty = 0;
                var preDstQty = 0;
                try
                {
                    var inv = InventoryManager.Instance();
                    if (inv != null)
                    {
                        TryGetSlotSnapshot(inv, srcType, srcSlot, out preSrcId, out preSrcQty);
                        TryGetSlotSnapshot(inv, dstType, dstSlot, out preDstId, out preDstQty);
                    }
                }
                catch
                {
                    // ignore
                }

                if (!TryCompanyChestMoveItem(srcType, srcSlot, dstType, dstSlot, needsNumeric))
                {
                    companyChestOrganize.Active = false;
                    return;
                }

                companyChestOrganize.WaitingForApply = true;
                companyChestOrganize.WaitSrcType = srcType;
                companyChestOrganize.WaitSrcSlot = srcSlot;
                companyChestOrganize.WaitSrcItemId = preSrcId;
                companyChestOrganize.WaitSrcQty = preSrcQty;
                companyChestOrganize.WaitDstType = dstType;
                companyChestOrganize.WaitDstSlot = dstSlot;
                companyChestOrganize.WaitDstItemId = preDstId;
                companyChestOrganize.WaitDstQty = preDstQty;
                var t = GetTimings();
                companyChestOrganize.WaitUntilMs = now + (needsNumeric ? t.numericApplyTimeoutMs : t.applyTimeoutMs);
                companyChestOrganize.WaitObservedChangeAtMs = 0;

                companyChestOrganize.Steps++;
                // Even after a move applies, add a small delay; Company Chest actions are more latency-sensitive.
                companyChestOrganize.NextAttemptAtMs = now + (needsNumeric ? t.numericStepDelayMs : t.stepDelayMs);

                if (Configuration.AutoConfirmCompanyChestQuantity && needsNumeric)
                {
                    pendingCompanyChestNumericConfirmUntilMs = now + 1500;
                    pendingCompanyChestNumericConfirmAttempts = 0;
                    pendingCompanyChestNumericArmed = true;
                    pendingNumericKind = PendingNumericKind.Move;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    pendingCompanyChestNumericHalf = false;
                    ArmSuppressInputNumeric(now);
                }

                if (Configuration.DebugMode)
                {
                    Svc.Log.Information($"[QuickTransfer] (MMB) Company Chest organize step {companyChestOrganize.Steps}: {srcType} slot={srcSlot} -> {dstType} slot={dstSlot} (phase=stack, numeric={needsNumeric}).");
                }
                return;
            }

            // No more merges; move on to compaction.
            companyChestOrganize.Phase = 1;
        }

        // Phase 1: compact items to fill empty slots from the start.
        if (TryFindCompanyChestCompactionMove(pages, out var cSrcType, out var cSrcSlot, out var cDstType, out var cDstSlot))
        {
            // Snapshot BEFORE issuing the move (so we can detect when it applies).
            var preSrcId = 0u;
            var preDstId = 0u;
            var preSrcQty = 0;
            var preDstQty = 0;
            try
            {
                var inv = InventoryManager.Instance();
                if (inv != null)
                {
                    TryGetSlotSnapshot(inv, cSrcType, cSrcSlot, out preSrcId, out preSrcQty);
                    TryGetSlotSnapshot(inv, cDstType, cDstSlot, out preDstId, out preDstQty);
                }
            }
            catch
            {
                // ignore
            }

            if (!TryCompanyChestMoveItem(cSrcType, cSrcSlot, cDstType, cDstSlot, false))
            {
                companyChestOrganize.Active = false;
                return;
            }

            companyChestOrganize.WaitingForApply = true;
            companyChestOrganize.WaitSrcType = cSrcType;
            companyChestOrganize.WaitSrcSlot = cSrcSlot;
            companyChestOrganize.WaitSrcItemId = preSrcId;
            companyChestOrganize.WaitSrcQty = preSrcQty;
            companyChestOrganize.WaitDstType = cDstType;
            companyChestOrganize.WaitDstSlot = cDstSlot;
            companyChestOrganize.WaitDstItemId = preDstId;
            companyChestOrganize.WaitDstQty = preDstQty;
            var t = GetTimings();
            companyChestOrganize.WaitUntilMs = now + t.applyTimeoutMs;
            companyChestOrganize.WaitObservedChangeAtMs = 0;

            companyChestOrganize.Steps++;
            companyChestOrganize.NextAttemptAtMs = now + t.stepDelayMs;

            if (Configuration.DebugMode)
            {
                Svc.Log.Information($"[QuickTransfer] (MMB) Company Chest organize step {companyChestOrganize.Steps}: {cSrcType} slot={cSrcSlot} -> {cDstType} slot={cDstSlot} (phase=compact).");
            }
            return;
        }

        // No more compaction moves; proceed to sorting.
        if (companyChestOrganize.Phase == 1)
        {
            companyChestOrganize.Phase = 2;
        }

        // Phase 2: reorder stacks by vanilla-ish keys (UI category order, SubcategorySort, itemId, HQ).
        if (companyChestOrganize.Phase == 2)
        {
            if (TryFindCompanyChestSortMove(pages, out var sSrcType, out var sSrcSlot, out var sDstType, out var sDstSlot))
            {
                // Snapshot BEFORE issuing the move (so we can detect when it applies).
                var preSrcId = 0u;
                var preDstId = 0u;
                var preSrcQty = 0;
                var preDstQty = 0;
                try
                {
                    var inv = InventoryManager.Instance();
                    if (inv != null)
                    {
                        TryGetSlotSnapshot(inv, sSrcType, sSrcSlot, out preSrcId, out preSrcQty);
                        TryGetSlotSnapshot(inv, sDstType, sDstSlot, out preDstId, out preDstQty);
                    }
                }
                catch
                {
                    // ignore
                }

                if (!TryCompanyChestMoveItem(sSrcType, sSrcSlot, sDstType, sDstSlot, false))
                {
                    companyChestOrganize.Active = false;
                    return;
                }

                companyChestOrganize.WaitingForApply = true;
                companyChestOrganize.WaitSrcType = sSrcType;
                companyChestOrganize.WaitSrcSlot = sSrcSlot;
                companyChestOrganize.WaitSrcItemId = preSrcId;
                companyChestOrganize.WaitSrcQty = preSrcQty;
                companyChestOrganize.WaitDstType = sDstType;
                companyChestOrganize.WaitDstSlot = sDstSlot;
                companyChestOrganize.WaitDstItemId = preDstId;
                companyChestOrganize.WaitDstQty = preDstQty;
                var t = GetTimings();
                companyChestOrganize.WaitUntilMs = now + t.applyTimeoutMs;
                companyChestOrganize.WaitObservedChangeAtMs = 0;

                companyChestOrganize.Steps++;
                companyChestOrganize.NextAttemptAtMs = now + t.stepDelayMs;

                if (Configuration.DebugMode)
                {
                    Svc.Log.Information($"[QuickTransfer] (MMB) Company Chest organize step {companyChestOrganize.Steps}: {sSrcType} slot={sSrcSlot} -> {sDstType} slot={sDstSlot} (phase=sort).");
                }
                return;
            }
        }

        // Done (no more moves).
        if (Configuration.DebugMode)
        {
            Svc.Log.Information($"[QuickTransfer] (MMB) Company Chest organize done; no moves found. pages=[{string.Join(", ", pages)}]");
        }
        companyChestOrganize.Active = false;
    }

    private bool TryFindCompanyChestMergeMove(
        InventoryType[] pages,
        out InventoryType srcType,
        out uint srcSlot,
        out InventoryType dstType,
        out uint dstSlot,
        out bool needsNumeric)
    {
        srcType = default;
        srcSlot = 0;
        dstType = default;
        dstSlot = 0;
        needsNumeric = false;

        var inv = InventoryManager.Instance();
        if (inv == null)
        {
            return false;
        }

        const int slotCap = 80;

        // Find a destination stack with free space, then a later source stack of same item to merge.
        foreach (var dt in pages)
        {
            for (var di = 0; di < slotCap; di++)
            {
                var d = inv->GetInventorySlot(dt, di);
                if (d == null)
                {
                    break;
                }
                if (d->ItemId == 0 || d->Quantity <= 0)
                {
                    continue;
                }

                var itemId = d->ItemId;
                var isHq = d->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                var maxStack = InventoryHelpers.GetItemStackSize(itemId);
                if (maxStack <= 1)
                {
                    continue;
                }

                var free = (int)maxStack - d->Quantity;
                if (free <= 0)
                {
                    continue;
                }

                // Find a later stack to merge into this one.
                var foundDest = false;
                var destGlobalIndex = 0;
                for (var pi = 0; pi < pages.Length; pi++)
                {
                    if (pages[pi] != dt)
                    {
                        continue;
                    }
                    destGlobalIndex = pi * slotCap + di;
                    foundDest = true;
                    break;
                }
                if (!foundDest)
                {
                    continue;
                }

                for (var p = 0; p < pages.Length; p++)
                {
                    var st = pages[p];
                    for (var si = 0; si < slotCap; si++)
                    {
                        var s = inv->GetInventorySlot(st, si);
                        if (s == null)
                        {
                            break;
                        }
                        if (s->ItemId == 0 || s->Quantity <= 0)
                        {
                            continue;
                        }
                        if (s->ItemId != itemId)
                        {
                            continue;
                        }
                        var sHq = s->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                        if (sHq != isHq)
                        {
                            continue;
                        }

                        var srcGlobalIndex = p * slotCap + si;
                        if (srcGlobalIndex <= destGlobalIndex)
                        {
                            continue;
                        }
                        if (st == dt && si == di)
                        {
                            continue;
                        }

                        // Merging stacks usually prompts for quantity.
                        srcType = st;
                        srcSlot = (uint)si;
                        dstType = dt;
                        dstSlot = (uint)di;
                        // Be conservative: if the client shows InputNumeric for this move, we must keep the move state alive.
                        // We auto-confirm max, so this will stack as much as possible.
                        needsNumeric = true;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool TryFindCompanyChestCompactionMove(
        InventoryType[] pages,
        out InventoryType srcType,
        out uint srcSlot,
        out InventoryType dstType,
        out uint dstSlot)
    {
        srcType = default;
        srcSlot = 0;
        dstType = default;
        dstSlot = 0;

        var inv = InventoryManager.Instance();
        if (inv == null)
        {
            return false;
        }

        const int slotCap = 80;

        // Find first empty, then next non-empty after it.
        for (var dp = 0; dp < pages.Length; dp++)
        {
            var dt = pages[dp];
            for (var di = 0; di < slotCap; di++)
            {
                var d = inv->GetInventorySlot(dt, di);
                if (d == null)
                {
                    break;
                }
                if (d->ItemId != 0)
                {
                    continue;
                }

                // Found empty destination.
                for (var sp = dp; sp < pages.Length; sp++)
                {
                    var st = pages[sp];
                    var start = sp == dp ? di + 1 : 0;
                    for (var si = start; si < slotCap; si++)
                    {
                        var s = inv->GetInventorySlot(st, si);
                        if (s == null)
                        {
                            break;
                        }
                        if (s->ItemId == 0 || s->Quantity <= 0)
                        {
                            continue;
                        }

                        srcType = st;
                        srcSlot = (uint)si;
                        dstType = dt;
                        dstSlot = (uint)di;
                        return true;
                    }
                }

                return false;
            }
        }

        return false;
    }

    private static bool TryFindCompanyChestSortMove(
        InventoryType[] pages,
        out InventoryType srcType,
        out uint srcSlot,
        out InventoryType dstType,
        out uint dstSlot)
    {
        srcType = default;
        srcSlot = 0;
        dstType = default;
        dstSlot = 0;

        if (pages.Length != 1)
        {
            return false;
        }

        var page = pages[0];
        var inv = InventoryManager.Instance();
        if (inv == null)
        {
            return false;
        }

        InventoryContainer* c;
        try { c = inv->GetInventoryContainer(page); }
        catch { return false; }
        if (c == null || !c->IsLoaded || c->Size <= 0)
        {
            return false;
        }

        var size = c->Size;
        if (size <= 1)
        {
            return false;
        }

        // Build keys for current slots.
        var keys = new ChestSortKey[size];
        var empty = new bool[size];
        for (var i = 0; i < size; i++)
        {
            var it = c->GetInventorySlot(i);
            if (it == null || it->ItemId == 0 || it->Quantity <= 0)
            {
                empty[i] = true;
                keys[i] = default;
                continue;
            }

            var id = it->ItemId;
            var hq = it->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
            keys[i] = InventoryHelpers.GetChestSortKey(id, hq);
        }

        // Ensure empties are at the end (safety; compaction phase should mostly handle this).
        for (var i = 0; i < size; i++)
        {
            if (!empty[i])
            {
                continue;
            }
            for (var j = i + 1; j < size; j++)
            {
                if (!empty[j])
                {
                    srcType = page;
                    srcSlot = (uint)j;
                    dstType = page;
                    dstSlot = (uint)i;
                    return true;
                }
            }
            break;
        }

        // Selection-sort step: for first index i, if there is a smaller key later, swap/move it into i.
        // This uses HandleItemMove's swap behavior for occupied destinations.
        for (var i = 0; i < size; i++)
        {
            if (empty[i])
            {
                break;
            }

            var best = i;
            for (var j = i + 1; j < size; j++)
            {
                if (empty[j])
                {
                    break; // empties at end
                }
                if (keys[j].CompareTo(keys[best]) < 0)
                {
                    best = j;
                }
            }

            if (best != i && keys[best].CompareTo(keys[i]) < 0)
            {
                srcType = page;
                srcSlot = (uint)best;
                dstType = page;
                dstSlot = (uint)i;
                return true;
            }
        }

        return false;
    }

    private bool TryCompanyChestMoveItem(
        InventoryType sourceType,
        uint sourceSlot,
        InventoryType destType,
        uint destSlot,
        bool keepAliveForInputNumeric)
    {
        var module = RaptureAtkModule.Instance();
        if (module == null)
        {
            return false;
        }

        // IMPORTANT:
        // HandleItemMove expects InventoryType values (e.g. Inventory1=0, FreeCompanyPage1=20000),
        // not "container ids" like 48/57.
        var srcInvType = (uint)sourceType;
        var dstInvType = (uint)destType;

        nint localValuesAlloc = 0;
        nint localRetAlloc = 0;
        try
        {
            AtkValue* values;
            AtkValue* ret;
            if (keepAliveForInputNumeric)
            {
                // Keep alive across the InputNumeric dialog.
                if (pendingMoveOutValuePtr != 0)
                {
                    try { Marshal.FreeHGlobal(pendingMoveOutValuePtr); }
                    catch
                    {
                        /* ignore */
                    }
                    pendingMoveOutValuePtr = 0;
                }
                if (pendingMoveAtkValuesPtr != 0)
                {
                    try { Marshal.FreeHGlobal(pendingMoveAtkValuesPtr); }
                    catch
                    {
                        /* ignore */
                    }
                    pendingMoveAtkValuesPtr = 0;
                }

                pendingMoveOutValuePtr = Marshal.AllocHGlobal(sizeof(AtkValue));
                pendingMoveAtkValuesPtr = Marshal.AllocHGlobal(sizeof(AtkValue) * 4);
                pendingMoveCreatedAtMs = Environment.TickCount64;
                pendingMoveSawInputNumeric = false;
                pendingMoveOutValueFreeAtMs = pendingMoveCreatedAtMs + 8000;

                ret = (AtkValue*)pendingMoveOutValuePtr;
                values = (AtkValue*)pendingMoveAtkValuesPtr;
            }
            else
            {
                localRetAlloc = Marshal.AllocHGlobal(sizeof(AtkValue));
                localValuesAlloc = Marshal.AllocHGlobal(sizeof(AtkValue) * 4);
                ret = (AtkValue*)localRetAlloc;
                values = (AtkValue*)localValuesAlloc;
            }

            ret->Type = AtkValueType.Int;
            ret->Int = 0;

            for (var i = 0; i < 4; i++)
            {
                values[i].Type = AtkValueType.UInt;
            }
            values[0].UInt = srcInvType;
            values[1].UInt = sourceSlot;
            values[2].UInt = dstInvType;
            values[3].UInt = destSlot;

            module->HandleItemMove(ret, values, 4);

            if (Configuration.DebugMode)
            {
                var inv = InventoryManager.Instance();
                TryGetSlotSnapshot(inv, sourceType, sourceSlot, out var sId, out var sQty);
                TryGetSlotSnapshot(inv, destType, destSlot, out var dId, out var dQty);
                Svc.Log.Information(
                    $"[QuickTransfer] (MMB) CompanyChest HandleItemMove: retInt={ret->Int}, " +
                    $"src={sourceType} slot={sourceSlot} (id={sId},qty={sQty}) -> dst={destType} slot={destSlot} (id={dId},qty={dQty}), keepAlive={keepAliveForInputNumeric}");
            }
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[QuickTransfer] Company Chest HandleItemMove failed.");
            return false;
        }
        finally
        {
            if (localRetAlloc != 0)
            {
                try { Marshal.FreeHGlobal(localRetAlloc); }
                catch
                {
                    /* ignore */
                }
            }
            if (localValuesAlloc != 0)
            {
                try { Marshal.FreeHGlobal(localValuesAlloc); }
                catch
                {
                    /* ignore */
                }
            }
        }
    }

    private static bool TryResolveCompanyChestCrystalDepositDestination(
        InventoryType sourceType,
        uint sourceSlot,
        uint itemId,
        bool isHq,
        uint maxStack,
        out uint destSlot)
    {
        destSlot = 0;

        // Player and FC crystal pouches share the same fixed slot indices.
        if (InventoryHelpers.IsPlayerCrystalsType(sourceType))
        {
            destSlot = sourceSlot;
            return true;
        }

        if (TryFindCompanyChestBestStackSlot(
            [InventoryType.FreeCompanyCrystals],
            itemId,
            isHq,
            maxStack,
            out var _,
            out destSlot))
        {
            return true;
        }

        var inv = InventoryManager.Instance();
        if (inv == null)
        {
            return false;
        }

        const int slotCap = 64;
        for (var i = 0; i < slotCap; i++)
        {
            var it = inv->GetInventorySlot(InventoryType.FreeCompanyCrystals, i);
            if (it == null)
            {
                break;
            }
            if (it->ItemId == itemId)
            {
                destSlot = (uint)i;
                return true;
            }
        }

        return false;
    }

    private bool TryResolveCompanyChestDepositDestination(
        InventoryType[] pages,
        uint itemId,
        bool isHq,
        uint maxStack,
        out InventoryType destType,
        out uint destSlot)
    {
        if (Configuration.CompanyChestDepositEmptySlotsFirst)
        {
            if (TryFindCompanyChestFirstEmptySlot(pages, out destType, out destSlot))
            {
                return true;
            }

            return TryFindCompanyChestBestStackSlot(pages, itemId, isHq, maxStack, out destType, out destSlot);
        }

        if (TryFindCompanyChestBestStackSlot(pages, itemId, isHq, maxStack, out destType, out destSlot))
        {
            return true;
        }

        return TryFindCompanyChestFirstEmptySlot(pages, out destType, out destSlot);
    }

    private static bool TryFindCompanyChestFirstEmptySlot(
        InventoryType[] pages,
        out InventoryType destType,
        out uint destSlot)
    {
        destType = default;
        destSlot = 0;

        if (pages.Length == 0)
        {
            return false;
        }

        var inv = InventoryManager.Instance();
        if (inv == null)
        {
            return false;
        }

        const int slotCap = 80;
        foreach (var t in pages)
        {
            for (var i = 0; i < slotCap; i++)
            {
                var item = inv->GetInventorySlot(t, i);
                if (item == null)
                {
                    break;
                }
                if (item->ItemId == 0)
                {
                    destType = t;
                    destSlot = (uint)i;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindCompanyChestBestStackSlot(
        InventoryType[] pages,
        uint itemId,
        bool isHq,
        uint maxStack,
        out InventoryType destType,
        out uint destSlot)
    {
        destType = default;
        destSlot = 0;

        if (pages.Length == 0 || itemId == 0 || maxStack <= 1)
        {
            return false;
        }

        var inv = InventoryManager.Instance();
        if (inv == null)
        {
            return false;
        }

        const int slotCap = 80;
        var bestFree = 0;
        foreach (var t in pages)
        {
            for (var i = 0; i < slotCap; i++)
            {
                var it = inv->GetInventorySlot(t, i);
                if (it == null)
                {
                    break;
                }

                if (it->ItemId != itemId)
                {
                    continue;
                }

                var hq = it->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                if (hq != isHq)
                {
                    continue;
                }

                var qty = it->Quantity;
                if (qty <= 0)
                {
                    continue;
                }

                var free = (int)maxStack - qty;
                if (free <= 0)
                {
                    continue;
                }

                if (free > bestFree)
                {
                    bestFree = free;
                    destType = t;
                    destSlot = (uint)i;
                }
            }
        }

        return bestFree > 0;
    }
    private bool TrySelectRemoveFromCompanyChestContextMenu()
    {
        try
        {
            var ctxMenu = (AddonContextMenu*)AddonHelpers.GetAddonByName("ContextMenu");
            if (ctxMenu == null)
            {
                return false;
            }

            // Find the list component and pick the row whose label is "Remove".
            // FreeCompanyChest uses a Default context menu, so the AgentInventoryContext index-based selection does not apply.
            for (uint listId = 1; listId <= 6; listId++)
            {
                var list = ctxMenu->GetComponentListById(listId);
                if (list == null)
                {
                    continue;
                }

                var itemCount = list->GetItemCount();
                if (itemCount is <= 0 or > 64)
                {
                    continue;
                }

                for (var i = 0; i < itemCount; i++)
                {
                    var labelPtr = list->GetItemLabel(i);
                    if ((byte*)labelPtr == null)
                    {
                        continue;
                    }

                    var label = Marshal.PtrToStringUTF8(new(labelPtr))?.TrimEnd('\0') ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(label))
                    {
                        continue;
                    }

                    if (Configuration.DebugMode)
                    {
                        Svc.Log.Information($"[QuickTransfer] ContextMenu listId={listId} row={i} label='{label}'");
                    }

                    if (!label.Equals("Remove", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Trigger via callback payload (matches the inventory context menu pattern).
                    AtkValueHelpers.GenerateCallback((AtkUnitBase*)ctxMenu, 0, i, 0U, 0, 0);

                    // Close slightly later (immediate close can cancel the action).
                    pendingCloseContextMenuAtMs = Environment.TickCount64 + 50;

                    if (Configuration.DebugMode)
                    {
                        Svc.Log.Information($"[QuickTransfer] Triggered Company Chest 'Remove' (listId={listId}, row={i}).");
                    }
                    return true;
                }
            }

            // Fallback: keep old string-scan (helpful for debugging), but don't attempt a blind click.
            return ContextMenuHandler.ContainsString((AtkUnitBase*)ctxMenu, "Remove", Configuration.DebugMode);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[QuickTransfer] Failed to select Remove from Company Chest context menu.");
            return false;
        }
    }
    private bool TryMapCompanyChestTabParamToPage(int eventParam, out InventoryType page)
    {
        page = default;
        try
        {
            // IMPORTANT: this mapping is about tab clicks, not how many compartments we *want* to operate on.
            // So we always consider all possible item pages (up to 5), even if the user configured fewer.
            var pages = GetAllCompanyChestItemPages();
            if (pages.Length == 0)
            {
                return false;
            }

            // Free Company Chest (your UI):
            // param=1 -> Items tab 1 (FreeCompanyPage1)
            // param=2 -> Items tab 2 (FreeCompanyPage2)
            // param=3 -> Items tab 3 (FreeCompanyPage3)
            // param=4 -> Items tab 4 (FreeCompanyPage4) [FC rank unlock]
            // param=5 -> Items tab 5 (FreeCompanyPage5) [FC rank unlock]
            // param=6 -> Crystals tab
            if (eventParam == 6)
            {
                page = InventoryType.FreeCompanyCrystals;
                return true;
            }

            if (eventParam < 1 || eventParam > pages.Length)
            {
                return false;
            }

            page = pages[eventParam - 1];
            return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }
}
