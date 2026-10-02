using Dalamud.Game.Chat;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Runtime.InteropServices;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace QuickTransfer;

public sealed unsafe partial class QuickTransferPlugin
{
    private const int CompanyChestSlotCap = 80;
    private const int CompanyChestCrystalSlotCap = 64;
    private const long CompanyChestTabMaxAgeMs = 180_000;

    private readonly record struct OrganizeTimings(
        int StepDelayMs,
        int StabilizeMs,
        int ApplyTimeoutMs,
        int NoApplyBackoffMs,
        int PageRetryMs,
        int NumericStepDelayMs,
        int NumericApplyTimeoutMs);

    private static void TryGetSlotSnapshot(
        InventoryManager* inv,
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
        }
    }

    private InventoryType[] GetCompanyChestInventoryTypes(int? maxCompartments = null)
    {
        var max = Math.Clamp(maxCompartments ?? Configuration.CompanyChestCompartments, 3, 5);
        return
        [
            .. Enum.GetValues<InventoryType>()
                .Where(InventoryHelpers.IsCompanyChestType)
                .OrderBy(v => (int)v)
                .Take(max)
        ];
    }

    // Busy/rollback responses back off exponentially: 5s, 10s, 20s, 40s, capped at 60s.
    private long RegisterCompanyChestBusyHit(long now)
    {
        companyChestBusyHits = Math.Min(companyChestBusyHits + 1, 10);
        long backoffMs = Math.Min(60000, 5000 * (1 << Math.Min(companyChestBusyHits - 1, 4)));
        companyChestBusyUntilMs = Math.Max(companyChestBusyUntilMs, now + backoffMs);
        return backoffMs;
    }

    private bool TryResolveCompanyChestPageFromAddon(AtkUnitBase* addon, out InventoryType page)
    {
        page = default;
        try
        {
            if (addon == null)
            {
                return false;
            }

            var nodeCount = addon->UldManager.NodeListCount;
            if (nodeCount <= 0)
            {
                return false;
            }

            // The addon keeps nodes for inactive tabs alive but hidden, so a first-match scan can pick the
            // wrong tab. Count FreeCompanyPage payloads on visible nodes and take the most frequent one.
            InventoryType bestPage = default;
            var bestHits = 0;
            Dictionary<InventoryType, int> hitsByPage = [];

            bool Tally(AtkDragDropInterface* ddi)
            {
                if (ddi == null ||
                    (nint)ddi < QuickTransferConstants.MinLikelyPointer ||
                    !DragDropHelpers.TryGetSlotFromDragDropInterface(ddi, out var invType, out var _) ||
                    !InventoryHelpers.IsCompanyChestDestinationType(invType))
                {
                    return false;
                }

                hitsByPage.TryGetValue(invType, out var cur);
                hitsByPage[invType] = ++cur;
                if (cur > bestHits)
                {
                    bestHits = cur;
                    bestPage = invType;
                }

                return true;
            }

            var maxNodes = Math.Min((int)nodeCount, 2000);
            for (var i = 0; i < maxNodes; i++)
            {
                var n = addon->UldManager.NodeList[i];
                if (n == null || n->Alpha_2 == 0 || n->Color.A == 0)
                {
                    continue;
                }

                var compNode = n->GetAsAtkComponentNode();
                if (compNode == null || compNode->Component == null)
                {
                    continue;
                }

                var component = compNode->Component;
                if (component->GetComponentType() != ComponentType.List)
                {
                    Tally(DragDropHelpers.TryGetDdiFromComponent(component));
                    continue;
                }

                // A handful of rows is enough evidence for a list.
                var list = (AtkComponentList*)component;
                var observed = 0;
                for (var li = 0; li < 30 && observed < 6; li++)
                {
                    if (Tally(DragDropHelpers.TryGetDdiFromListIndex(list, li)))
                    {
                        observed++;
                    }
                }
            }

            if (bestHits > 0)
            {
                page = bestPage;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    // Learns which AtkValue index holds the selected tab by correlating values with observed tab clicks.
    private void ObserveCompanyChestTabFromAtkValues(AtkUnitBase* addon, InventoryType selectedPage)
    {
        try
        {
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount <= 0)
            {
                return;
            }

            var values = addon->AtkValues;
            var max = Math.Min((int)addon->AtkValuesCount, 80);

            for (var i = 0; i < max; i++)
            {
                if (!AtkValueHelpers.TryGetAtkValueInt(values, max, i, out var n) || n is < 0 or > 10)
                {
                    continue;
                }

                if (!companyChestSelectedTabCandidates.TryGetValue(i, out var map))
                {
                    map = [];
                    companyChestSelectedTabCandidates[i] = map;
                }

                if (map.TryGetValue(n, out var existing) && existing != selectedPage)
                {
                    companyChestSelectedTabCandidates.Remove(i);
                    continue;
                }

                map[n] = selectedPage;
            }

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

            if (bestIdx >= 0 && bestDistinct >= 2 && companyChestSelectedTabAtkValueIndex != bestIdx)
            {
                companyChestSelectedTabAtkValueIndex = bestIdx;
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information($"[QuickTransfer] FC Chest AtkValues selected-tab index inferred: idx={bestIdx} (mappedPages={bestDistinct}).");
                }
            }
        }
        catch
        {
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

            if (!InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out var addon, QuickTransferConstants.WideAddonSearchMaxIndex) ||
                addon->Id != addonId ||
                addon->AtkValues == null ||
                addon->AtkValuesCount <= 0)
            {
                return false;
            }

            if (!companyChestSelectedTabCandidates.TryGetValue(companyChestSelectedTabAtkValueIndex, out var map) ||
                !AtkValueHelpers.TryGetAtkValueInt(addon->AtkValues, addon->AtkValuesCount, companyChestSelectedTabAtkValueIndex, out var n) ||
                !map.TryGetValue(n, out var p) ||
                !InventoryHelpers.IsCompanyChestDestinationType(p))
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
            if (!InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out var fcc, QuickTransferConstants.WideAddonSearchMaxIndex))
            {
                return false;
            }

            var addonId = fcc->Id;

            // Prefer what the addon is currently displaying, then recent hover/click observations.
            if (TryResolveCompanyChestPageFromAddon(fcc, out page))
            {
                return true;
            }

            var lp = lastHoverCompanyChestPage;
            if (lp != null && lp.Value.AddonId == addonId && now - lp.Value.SeenAtMs <= CompanyChestTabMaxAgeMs && InventoryHelpers.IsCompanyChestDestinationType(lp.Value.Page))
            {
                page = lp.Value.Page;
                return true;
            }

            var sp = lastSelectedCompanyChestPage;
            if (sp != null && sp.Value.AddonId == addonId && now - sp.Value.SeenAtMs <= CompanyChestTabMaxAgeMs && InventoryHelpers.IsCompanyChestDestinationType(sp.Value.Page))
            {
                page = sp.Value.Page;
                return true;
            }

            return TryResolveCompanyChestSelectedPageFromAtkValues(addonId, out page);
        }
        catch
        {
            return false;
        }
    }

    private void OnCompanyChestButtonClick(AtkUnitBase* addon, int eventParam, long now)
    {
        try
        {
            var id = addon != null ? addon->Id : 0u;
            if (id == 0)
            {
                return;
            }

            if (!TryMapCompanyChestTabParamToPage(eventParam, out var selectedPage))
            {
                if (Configuration.DebugMode && now - lastFcChestTabUnmappedLogMs >= 250)
                {
                    lastFcChestTabUnmappedLogMs = now;
                    Svc.Log.Information($"[QuickTransfer] FC Chest tab param unmapped: param={eventParam} (addonId={id})");
                }

                return;
            }

            lastSelectedCompanyChestPage = (selectedPage, id, now);
            ObserveCompanyChestTabFromAtkValues(addon, selectedPage);
            if (Configuration.DebugMode && now - lastReceiveEventDebugLogMs >= 250)
            {
                Svc.Log.Information($"[QuickTransfer] FC Chest selected tab: param={eventParam} -> {selectedPage} (addonId={id})");
            }

            if (companyChestOrganize.Active &&
                (companyChestOrganize.OwnerAddonId == 0 || companyChestOrganize.OwnerAddonId == id) &&
                companyChestOrganize.Pages is { Length: 1 } &&
                companyChestOrganize.Pages[0] != selectedPage)
            {
                companyChestOrganize.Active = false;
                companyChestOrganize.WaitingForApply = false;
                companyChestOrganize.WaitObservedChangeAtMs = 0;
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information($"[QuickTransfer] (MMB) Company Chest tab changed to {selectedPage}; stopping previous organize run.");
                }
            }

            if (companyChestDeposit.Active &&
                companyChestDeposit.DestPage != default &&
                companyChestDeposit.DestPage != selectedPage)
            {
                companyChestDeposit.Active = false;
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information($"[QuickTransfer] (Shift+RClick) Company Chest tab changed to {selectedPage}; stopping deposit run.");
                }
            }
        }
        catch
        {
        }
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            if (!Configuration.EnableCompanyChest || !companyChestOrganize.Active && !companyChestDeposit.Active)
            {
                return;
            }

            var text = message.Sender.TextValue;
            if (text.Length == 0)
            {
                return;
            }

            if (!text.Contains("Another player is using the chest", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("Unable to store item", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("Unable to complete company chest action", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var now = Environment.TickCount64;
            var backoffMs = RegisterCompanyChestBusyHit(now);

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
                companyChestOrganize.WaitingForApply = false;
                companyChestOrganize.WaitObservedChangeAtMs = 0;
                companyChestOrganize.NextAttemptAtMs = Math.Max(companyChestOrganize.NextAttemptAtMs, companyChestBusyUntilMs + 750);
                companyChestOrganize.ExpiresAtMs = Math.Max(companyChestOrganize.ExpiresAtMs, companyChestBusyUntilMs + 20000);
                companyChestOrganize.WaitStuckCount = 0;
            }

            // Deposits are user-initiated; stop outright rather than retrying later.
            companyChestDeposit.Active = false;
            ClearPendingNumeric();

            if (Configuration.DebugMode)
            {
                Svc.Log.Information($"[QuickTransfer] (MMB) FC Chest busy detected from chat; backoff={backoffMs}ms (hit {companyChestBusyHits}). msg='{text}'");
            }
        }
        catch
        {
        }
    }

    private bool StartCompanyChestDeposit(InventoryType sourceType, uint sourceSlot)
    {
        try
        {
            if (!Configuration.EnableCompanyChest ||
                RaptureAtkModule.Instance() == null ||
                !InventoryHelpers.IsCompanyChestOpen() ||
                !InventoryHelpers.IsCompanyChestDepositSourceType(sourceType) ||
                !InventoryHelpers.TryGetItemInfo(sourceType, (int)sourceSlot, out var itemId, out var isHq, out var qty))
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
                LastQty = qty
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

        if (!Configuration.EnableCompanyChest ||
            RaptureAtkModule.Instance() == null ||
            !InventoryHelpers.IsCompanyChestOpen() ||
            now >= companyChestDeposit.ExpiresAtMs ||
            companyChestDeposit.Steps >= 40)
        {
            companyChestDeposit.Active = false;
            return;
        }

        // After issuing a move, wait for the source quantity to change so the same move is not repeated.
        if (companyChestDeposit.WaitForQtyChangeUntilMs > 0 && now <= companyChestDeposit.WaitForQtyChangeUntilMs)
        {
            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out var _))
            {
                return;
            }

            if (!InventoryHelpers.TryGetItemInfo(companyChestDeposit.SourceType, (int)companyChestDeposit.SourceSlot, out var _, out var _, out var qNow) ||
                qNow == companyChestDeposit.LastQty)
            {
                return;
            }

            companyChestDeposit.LastQty = qNow;
            companyChestDeposit.WaitForQtyChangeUntilMs = 0;
        }

        if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out var _) ||
            now < companyChestDeposit.NextAttemptAtMs)
        {
            return;
        }

        // Stop if the slot emptied or now holds a different item (the user moved or split it).
        if (!InventoryHelpers.TryGetItemInfo(companyChestDeposit.SourceType, (int)companyChestDeposit.SourceSlot, out var itemId, out var isHq, out var qty) ||
            qty == 0 ||
            itemId != companyChestDeposit.ItemId ||
            isHq != companyChestDeposit.IsHq)
        {
            companyChestDeposit.Active = false;
            return;
        }

        if (companyChestDeposit.DestPage == default || !InventoryHelpers.IsCompanyChestDestinationType(companyChestDeposit.DestPage))
        {
            if (!TryResolveCompanyChestActivePage(now, out var activePage) || !InventoryHelpers.IsCompanyChestDestinationType(activePage))
            {
                companyChestDeposit.Active = false;
                return;
            }

            companyChestDeposit.DestPage = activePage;
        }

        var page = companyChestDeposit.DestPage;
        var maxStack = InventoryHelpers.GetItemStackSize(itemId);
        var needsQuantityConfirm = qty > 1 && maxStack > 1;

        InventoryType destType;
        uint destSlot;
        if (page == InventoryType.FreeCompanyCrystals)
        {
            destType = InventoryType.FreeCompanyCrystals;
            if (!TryResolveCompanyChestCrystalDepositDestination(companyChestDeposit.SourceType, companyChestDeposit.SourceSlot, itemId, isHq, maxStack, out destSlot))
            {
                companyChestDeposit.Active = false;
                return;
            }
        }
        else if (!TryResolveCompanyChestDepositDestination([page], itemId, isHq, maxStack, out destType, out destSlot))
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
            ArmPendingNumeric(now, PendingNumericKind.Store, 1500);
        }

        if (Configuration.DebugMode)
        {
            Svc.Log.Information($"[QuickTransfer] (Shift+RClick) Company Chest deposit step {companyChestDeposit.Steps}: {companyChestDeposit.SourceType} slot={companyChestDeposit.SourceSlot} -> {destType} slot={destSlot} (page={page}, qty={qty}, stackMax={maxStack}).");
        }
    }

    private void StartCompanyChestOrganize(long now, InventoryType selectedPage)
    {
        if (!InventoryHelpers.IsCompanyChestType(selectedPage) ||
            !Configuration.EnableCompanyChest ||
            !InventoryHelpers.IsCompanyChestOpen() ||
            RaptureAtkModule.Instance() == null ||
            now <= companyChestBusyUntilMs)
        {
            return;
        }

        if (companyChestOrganize.Active && now < companyChestOrganize.ExpiresAtMs)
        {
            if (companyChestOrganize.Pages is { Length: 1 } && companyChestOrganize.Pages[0] != selectedPage)
            {
                companyChestOrganize.Active = false;
            }
            else
            {
                // Same tab: keep progress, just extend the deadline.
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
        if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out var fcc, QuickTransferConstants.WideAddonSearchMaxIndex))
        {
            ownerAddonId = fcc->Id;
        }

        companyChestOrganize = new()
        {
            Active = true,
            OwnerAddonId = ownerAddonId,
            NextAttemptAtMs = now,
            ExpiresAtMs = now + 60000,
            Phase = CompanyChestOrganizePhase.Stack,
            Pages = [selectedPage]
        };

        if (Configuration.DebugMode)
        {
            Svc.Log.Information($"[QuickTransfer] (MMB) Company Chest organize started (selectedPage={selectedPage}).");
        }
    }

    // Slows down automatically once the server starts rejecting actions.
    private OrganizeTimings GetOrganizeTimings() => Math.Clamp(companyChestBusyHits, 0, 2) switch
    {
        0 => new(750, 300, 1300, 650, 350, 1500, 3200),
        1 => new(1000, 450, 1800, 900, 500, 2200, 4500),
        var _ => new(1300, 650, 2500, 1200, 750, 3000, 6000)
    };

    // Organize runs as a state machine, one move per step: stack partial stacks, compact into the
    // leading slots, then selection-sort. Each move waits until the inventory reflects it.
    private void ProcessCompanyChestOrganize(long now)
    {
        void LogSkip(string reason)
        {
            if (!Configuration.DebugMode)
            {
                return;
            }

            if (!string.Equals(lastCompanyChestOrganizeSkipReason, reason, StringComparison.Ordinal) ||
                now - lastCompanyChestOrganizeSkipLogMs >= 2000)
            {
                lastCompanyChestOrganizeSkipReason = reason;
                lastCompanyChestOrganizeSkipLogMs = now;
                Svc.Log.Information($"[QuickTransfer] (MMB) Company Chest organize waiting: {reason}");
            }
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

        if (!Configuration.EnableCompanyChest ||
            RaptureAtkModule.Instance() == null ||
            !InventoryHelpers.IsCompanyChestOpen() ||
            now >= companyChestOrganize.ExpiresAtMs ||
            companyChestOrganize.Steps >= 140)
        {
            companyChestOrganize.Active = false;
            return;
        }

        if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out var _))
        {
            LogSkip("InputNumeric visible");
            return;
        }

        var pages = companyChestOrganize.Pages;
        if (!ArePagesReady(pages))
        {
            companyChestOrganize.NextAttemptAtMs = now + GetOrganizeTimings().PageRetryMs;
            LogSkip($"pages not ready yet; waiting. pages=[{string.Join(", ", pages)}]");
            return;
        }

        if (companyChestOrganize.WaitingForApply && !CheckOrganizeMoveApplied(now, LogSkip))
        {
            return;
        }

        if (now < companyChestOrganize.NextAttemptAtMs)
        {
            LogSkip("cooldown");
            return;
        }

        if (pages.Length == 0)
        {
            companyChestOrganize.Active = false;
            return;
        }

        if (companyChestOrganize.Phase == CompanyChestOrganizePhase.Stack)
        {
            if (TryFindCompanyChestMergeMove(pages, out var srcType, out var srcSlot, out var dstType, out var dstSlot))
            {
                // Merging prompts for a quantity; auto-confirm uses the max so as much as possible stacks.
                IssueOrganizeMove(now, srcType, srcSlot, dstType, dstSlot, needsNumeric: true, "stack");
                return;
            }

            companyChestOrganize.Phase = CompanyChestOrganizePhase.Compact;
        }

        if (TryFindCompanyChestCompactionMove(pages, out var cSrcType, out var cSrcSlot, out var cDstType, out var cDstSlot))
        {
            IssueOrganizeMove(now, cSrcType, cSrcSlot, cDstType, cDstSlot, needsNumeric: false, "compact");
            return;
        }

        companyChestOrganize.Phase = CompanyChestOrganizePhase.Sort;

        if (TryFindCompanyChestSortMove(pages, out var sSrcType, out var sSrcSlot, out var sDstType, out var sDstSlot))
        {
            IssueOrganizeMove(now, sSrcType, sSrcSlot, sDstType, sDstSlot, needsNumeric: false, "sort");
            return;
        }

        if (Configuration.DebugMode)
        {
            Svc.Log.Information($"[QuickTransfer] (MMB) Company Chest organize done; no moves found. pages=[{string.Join(", ", pages)}]");
        }

        companyChestOrganize.Active = false;
    }

    // Containers can report loaded while slot pointers are still null; treating that as "no moves" would end the run early.
    private static bool ArePagesReady(InventoryType[] pages)
    {
        try
        {
            var inv = InventoryManager.Instance();
            if (inv == null)
            {
                return true;
            }

            foreach (var p in pages)
            {
                if (!InventoryHelpers.IsContainerLoaded(inv, p) || inv->GetInventorySlot(p, 0) == null)
                {
                    return false;
                }
            }
        }
        catch
        {
        }

        return true;
    }

    // Returns true once the previous move has applied and stabilized (or waiting gave up), false to keep waiting.
    private bool CheckOrganizeMoveApplied(long now, Action<string> logSkip)
    {
        try
        {
            var inv = InventoryManager.Instance();
            if (inv == null)
            {
                return true;
            }

            ref var o = ref companyChestOrganize;
            TryGetSlotSnapshot(inv, o.WaitSrcType, o.WaitSrcSlot, out var sId, out var sQty);
            TryGetSlotSnapshot(inv, o.WaitDstType, o.WaitDstSlot, out var dId, out var dQty);

            var applied = sId != o.WaitSrcItemId || sQty != o.WaitSrcQty || dId != o.WaitDstItemId || dQty != o.WaitDstQty;
            var t = GetOrganizeTimings();

            if (applied)
            {
                // Wait a short stabilization window in case the server rejects and rolls back.
                if (o.WaitObservedChangeAtMs == 0)
                {
                    o.WaitObservedChangeAtMs = now;
                }

                if (now - o.WaitObservedChangeAtMs < t.StabilizeMs)
                {
                    logSkip("waiting for apply (stabilize)");
                    return false;
                }

                o.WaitingForApply = false;
                o.WaitUntilMs = 0;
                o.WaitStuckCount = 0;
                o.WaitObservedChangeAtMs = 0;
                return true;
            }

            if (o.WaitObservedChangeAtMs != 0)
            {
                // A change was seen but the slots are back to the snapshot: the server rolled the move back.
                var backoffMs = RegisterCompanyChestBusyHit(now);
                o.WaitingForApply = false;
                o.WaitObservedChangeAtMs = 0;

                if (Configuration.DebugMode)
                {
                    Svc.Log.Information($"[QuickTransfer] (MMB) Company Chest move rolled back; treating as busy. backoff={backoffMs}ms (hit {companyChestBusyHits}).");
                }

                if (companyChestBusyHits >= 3)
                {
                    o.Active = false;
                }

                return false;
            }

            if (now <= o.WaitUntilMs)
            {
                logSkip("waiting for apply");
                return false;
            }

            o.WaitStuckCount++;
            o.WaitingForApply = false;
            o.WaitObservedChangeAtMs = 0;
            if (o.WaitStuckCount >= 3)
            {
                o.Active = false;
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information("[QuickTransfer] (MMB) Company Chest organize stalled (no inventory change observed); stopping to avoid spam.");
                    Svc.Log.Information(
                        $"[QuickTransfer] (MMB) Stall snapshot: src={o.WaitSrcType} slot={o.WaitSrcSlot} " +
                        $"was(id={o.WaitSrcItemId},qty={o.WaitSrcQty}) now(id={sId},qty={sQty}); " +
                        $"dst={o.WaitDstType} slot={o.WaitDstSlot} " +
                        $"was(id={o.WaitDstItemId},qty={o.WaitDstQty}) now(id={dId},qty={dQty});");
                }

                return false;
            }

            o.NextAttemptAtMs = now + t.NoApplyBackoffMs;
            logSkip("no apply observed; backoff");
            return false;
        }
        catch
        {
            return true;
        }
    }

    private void IssueOrganizeMove(
        long now,
        InventoryType srcType,
        uint srcSlot,
        InventoryType dstType,
        uint dstSlot,
        bool needsNumeric,
        string phaseName)
    {
        var inv = InventoryManager.Instance();
        TryGetSlotSnapshot(inv, srcType, srcSlot, out var preSrcId, out var preSrcQty);
        TryGetSlotSnapshot(inv, dstType, dstSlot, out var preDstId, out var preDstQty);

        if (!TryCompanyChestMoveItem(srcType, srcSlot, dstType, dstSlot, needsNumeric))
        {
            companyChestOrganize.Active = false;
            return;
        }

        var t = GetOrganizeTimings();
        ref var o = ref companyChestOrganize;
        o.WaitingForApply = true;
        o.WaitSrcType = srcType;
        o.WaitSrcSlot = srcSlot;
        o.WaitSrcItemId = preSrcId;
        o.WaitSrcQty = preSrcQty;
        o.WaitDstType = dstType;
        o.WaitDstSlot = dstSlot;
        o.WaitDstItemId = preDstId;
        o.WaitDstQty = preDstQty;
        o.WaitUntilMs = now + (needsNumeric ? t.NumericApplyTimeoutMs : t.ApplyTimeoutMs);
        o.WaitObservedChangeAtMs = 0;
        o.Steps++;
        o.NextAttemptAtMs = now + (needsNumeric ? t.NumericStepDelayMs : t.StepDelayMs);

        if (Configuration.AutoConfirmCompanyChestQuantity && needsNumeric)
        {
            ArmPendingNumeric(now, PendingNumericKind.Move, 1500, suppressMs: 1500);
        }

        if (Configuration.DebugMode)
        {
            Svc.Log.Information($"[QuickTransfer] (MMB) Company Chest organize step {o.Steps}: {srcType} slot={srcSlot} -> {dstType} slot={dstSlot} (phase={phaseName}, numeric={needsNumeric}).");
        }
    }

    // Finds a non-full stack and a later stack of the same item (and HQ flag) to merge into it.
    private static bool TryFindCompanyChestMergeMove(
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

        for (var dp = 0; dp < pages.Length; dp++)
        {
            var dt = pages[dp];
            for (var di = 0; di < CompanyChestSlotCap; di++)
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
                if (maxStack <= 1 || (int)maxStack - d->Quantity <= 0)
                {
                    continue;
                }

                var destGlobalIndex = dp * CompanyChestSlotCap + di;
                for (var sp = 0; sp < pages.Length; sp++)
                {
                    var st = pages[sp];
                    for (var si = 0; si < CompanyChestSlotCap; si++)
                    {
                        var s = inv->GetInventorySlot(st, si);
                        if (s == null)
                        {
                            break;
                        }

                        if (s->ItemId != itemId ||
                            s->Quantity <= 0 ||
                            s->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) != isHq ||
                            sp * CompanyChestSlotCap + si <= destGlobalIndex)
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
            }
        }

        return false;
    }

    // Finds the first empty slot and the next occupied slot after it.
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

        for (var dp = 0; dp < pages.Length; dp++)
        {
            var dt = pages[dp];
            for (var di = 0; di < CompanyChestSlotCap; di++)
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

                for (var sp = dp; sp < pages.Length; sp++)
                {
                    var st = pages[sp];
                    var start = sp == dp ? di + 1 : 0;
                    for (var si = start; si < CompanyChestSlotCap; si++)
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

    // One selection-sort step: move the smallest later key into the first out-of-order slot.
    // HandleItemMove swaps when the destination is occupied.
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

        var c = inv->GetInventoryContainer(page);
        if (c == null || !c->IsLoaded || c->Size <= 1)
        {
            return false;
        }

        var size = c->Size;
        var keys = new ChestSortKey[size];
        var empty = new bool[size];
        for (var i = 0; i < size; i++)
        {
            var it = c->GetInventorySlot(i);
            if (it == null || it->ItemId == 0 || it->Quantity <= 0)
            {
                empty[i] = true;
                continue;
            }

            keys[i] = InventoryHelpers.GetChestSortKey(it->ItemId, it->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
        }

        srcType = page;
        dstType = page;

        // Compaction should already have moved empties to the end; handle any stragglers first.
        var firstEmpty = Array.IndexOf(empty, true);
        if (firstEmpty >= 0)
        {
            var nextOccupied = Array.IndexOf(empty, false, firstEmpty + 1);
            if (nextOccupied >= 0)
            {
                srcSlot = (uint)nextOccupied;
                dstSlot = (uint)firstEmpty;
                return true;
            }
        }

        for (var i = 0; i < size && !empty[i]; i++)
        {
            var best = i;
            for (var j = i + 1; j < size && !empty[j]; j++)
            {
                if (keys[j].CompareTo(keys[best]) < 0)
                {
                    best = j;
                }
            }

            if (best != i)
            {
                srcSlot = (uint)best;
                dstSlot = (uint)i;
                return true;
            }
        }

        srcType = default;
        dstType = default;
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

        nint localValuesAlloc = 0;
        nint localRetAlloc = 0;
        try
        {
            AtkValue* values;
            AtkValue* ret;
            if (keepAliveForInputNumeric)
            {
                ReleasePendingMoveBuffers();
                pendingMoveOutValuePtr = Marshal.AllocHGlobal(sizeof(AtkValue));
                pendingMoveAtkValuesPtr = Marshal.AllocHGlobal(sizeof(AtkValue) * 4);
                pendingMoveCreatedAtMs = Environment.TickCount64;
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

            values[0].UInt = (uint)sourceType;
            values[1].UInt = sourceSlot;
            values[2].UInt = (uint)destType;
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
                Marshal.FreeHGlobal(localRetAlloc);
            }

            if (localValuesAlloc != 0)
            {
                Marshal.FreeHGlobal(localValuesAlloc);
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
        // Player and FC crystal pouches share the same fixed slot indices.
        if (InventoryHelpers.IsPlayerCrystalsType(sourceType))
        {
            destSlot = sourceSlot;
            return true;
        }

        if (TryFindCompanyChestBestStackSlot([InventoryType.FreeCompanyCrystals], itemId, isHq, maxStack, out var _, out destSlot))
        {
            return true;
        }

        destSlot = 0;
        var inv = InventoryManager.Instance();
        if (inv == null)
        {
            return false;
        }

        for (var i = 0; i < CompanyChestCrystalSlotCap; i++)
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
        => Configuration.CompanyChestDepositEmptySlotsFirst
            ? TryFindCompanyChestFirstEmptySlot(pages, out destType, out destSlot) ||
              TryFindCompanyChestBestStackSlot(pages, itemId, isHq, maxStack, out destType, out destSlot)
            : TryFindCompanyChestBestStackSlot(pages, itemId, isHq, maxStack, out destType, out destSlot) ||
              TryFindCompanyChestFirstEmptySlot(pages, out destType, out destSlot);

    private static bool TryFindCompanyChestFirstEmptySlot(
        InventoryType[] pages,
        out InventoryType destType,
        out uint destSlot)
    {
        destType = default;
        destSlot = 0;

        var inv = InventoryManager.Instance();
        if (inv == null)
        {
            return false;
        }

        foreach (var t in pages)
        {
            for (var i = 0; i < CompanyChestSlotCap; i++)
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

    // Picks the matching stack with the most free space.
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

        if (itemId == 0 || maxStack <= 1)
        {
            return false;
        }

        var inv = InventoryManager.Instance();
        if (inv == null)
        {
            return false;
        }

        var bestFree = 0;
        foreach (var t in pages)
        {
            for (var i = 0; i < CompanyChestSlotCap; i++)
            {
                var it = inv->GetInventorySlot(t, i);
                if (it == null)
                {
                    break;
                }

                if (it->ItemId != itemId ||
                    it->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) != isHq ||
                    it->Quantity <= 0)
                {
                    continue;
                }

                var free = (int)maxStack - it->Quantity;
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

    // The FC chest uses a Default context menu, so the AgentInventoryContext index-based selection
    // does not apply; find the "Remove" row in the menu's list component instead.
    private bool TrySelectRemoveFromCompanyChestContextMenu()
    {
        try
        {
            var ctxMenu = (AddonContextMenu*)InventoryHelpers.GetAddonByName(QuickTransferConstants.ContextMenuAddonName);
            if (ctxMenu == null)
            {
                return false;
            }

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

                    AtkValueHelpers.GenerateCallback((AtkUnitBase*)ctxMenu, 0, i, 0U, 0, 0);

                    // Closing immediately can cancel the action.
                    pendingCloseContextMenuAtMs = Environment.TickCount64 + 50;

                    if (Configuration.DebugMode)
                    {
                        Svc.Log.Information($"[QuickTransfer] Triggered Company Chest 'Remove' (listId={listId}, row={i}).");
                    }

                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[QuickTransfer] Failed to select Remove from Company Chest context menu.");
            return false;
        }
    }

    // Tab buttons report params 1..5 for item compartments and 6 for crystals.
    private bool TryMapCompanyChestTabParamToPage(int eventParam, out InventoryType page)
    {
        page = default;
        if (eventParam == 6)
        {
            page = InventoryType.FreeCompanyCrystals;
            return true;
        }

        var pages = GetCompanyChestInventoryTypes(5);
        if (eventParam < 1 || eventParam > pages.Length)
        {
            return false;
        }

        page = pages[eventParam - 1];
        return true;
    }
}
