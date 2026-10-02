using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static QuickTransfer.QuickTransferConstants;
namespace QuickTransfer;

public sealed unsafe partial class QuickTransferPlugin
{
    private const long HoverMaxAgeMs = 20000;

    // Inventory's owner addon id is commonly 17, with collision hits on child ids whose HostId/ParentId point at it.
    private const uint InventoryOwnerAddonIdHeuristic = 17;

    private void QueueMiddleClickSort(InventoryType type, int slot, uint addonId, long now)
    {
        pendingMiddleClickSortRequest = (type, slot, addonId, now);
        pendingMiddleClickSortUntilMs = now + DeferredRequestTimeoutMs;
        lastMiddleClickSortMs = now;
    }

    private static bool TryGetVisibleAddonId(string name, out uint id)
    {
        id = 0;
        if (InventoryHelpers.TryGetVisibleAddon(name, out var a, WideAddonSearchMaxIndex) && a->Id != 0)
        {
            id = a->Id;
            return true;
        }

        return false;
    }

    private static string InferOwnerAddonName(InventoryType t)
    {
        if (InventoryHelpers.IsPlayerInventoryType(t))
        {
            return InventoryAddonName;
        }

        if (InventoryHelpers.IsSaddlebagType(t))
        {
            return SaddlebagAddonName;
        }

        if (InventoryHelpers.IsArmouryType(t))
        {
            return ArmouryBoardAddonName;
        }

        if (InventoryHelpers.IsCompanyChestType(t))
        {
            return FreeCompanyChestAddonName;
        }

        return InventoryHelpers.IsRetainerType(t) ? RetainerGrid0AddonName : string.Empty;
    }

    private bool TryUpdateLastHoverAddonFromCollisionManager(long now)
    {
        try
        {
            var stage = AtkStage.Instance();
            if (stage == null || stage->AtkCollisionManager == null)
            {
                return false;
            }

            var hit = stage->AtkCollisionManager->IntersectingAddon;
            if (hit == null || hit->Id == 0)
            {
                return false;
            }

            Dictionary<uint, string> visibleById = [];
            foreach (var name in AllContainerAddonNames)
            {
                // First name wins in case an alias lookup returns an addon whose id is already mapped.
                if (TryGetVisibleAddonId(name, out var id))
                {
                    visibleById.TryAdd(id, name);
                }
            }

            uint hitId = hit->Id;
            uint hostId = hit->HostId;
            uint parentId = hit->ParentId;
            uint[] candidateIds = [hitId, hostId, parentId];

            uint ownerId = 0;
            var ownerName = string.Empty;
            var ownerSource = string.Empty;

            // Prefer the hit itself, then its host, then its parent; first by visible window, then by
            // previously observed context-menu targets (Inventory is often not found by name lookup).
            foreach (var id in candidateIds)
            {
                if (id != 0 && visibleById.TryGetValue(id, out var name))
                {
                    (ownerId, ownerName, ownerSource) = (id, name, "visible");
                    break;
                }
            }

            if (ownerId == 0)
            {
                foreach (var id in candidateIds)
                {
                    if (id == 0 || !lastGoodContextTargetByAddonId.TryGetValue(id, out var good))
                    {
                        continue;
                    }

                    var inferred = InferOwnerAddonName(good.Type);
                    if (inferred.Length > 0)
                    {
                        (ownerId, ownerName, ownerSource) = (id, inferred, "lastGood");
                        break;
                    }
                }
            }

            if (ownerId == 0 && (hostId == InventoryOwnerAddonIdHeuristic || parentId == InventoryOwnerAddonIdHeuristic))
            {
                (ownerId, ownerName, ownerSource) = (InventoryOwnerAddonIdHeuristic, InventoryAddonName, "heuristic17");
            }

            if (ownerId == 0)
            {
                if (Configuration.DebugMode && now - lastCursorHitTestLogMs >= 1000)
                {
                    lastCursorHitTestLogMs = now;
                    Svc.Log.Information($"[QuickTransfer] (MMB) CollisionManager hit addonId={hitId} hostId={hostId} parentId={parentId} (unmapped). Visible owners=[{string.Join(", ", visibleById.Select(kv => $"{kv.Value}:{kv.Key}"))}] lastGoodOwnerIds=[{string.Join(", ", lastGoodContextTargetByAddonId.Keys.Take(24))}]");
                }

                return false;
            }

            lastHoverAddon = (ownerName, ownerId, now);

            if (Configuration.DebugMode && now - lastCursorHitTestLogMs >= 1000)
            {
                lastCursorHitTestLogMs = now;
                Svc.Log.Information($"[QuickTransfer] (MMB) CollisionManager picked addon '{ownerName}' (ownerAddonId={ownerId}, hitAddonId={hitId}, source={ownerSource}).");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryUpdateLastHoverAddonFromCursorHitTest(long now)
    {
        try
        {
            if (TryUpdateLastHoverAddonFromCollisionManager(now))
            {
                return true;
            }

            if (!ModifierBindings.TryGetClientCursorPos(out var x, out var y))
            {
                return false;
            }

            AtkUnitBase* best = null;
            var bestName = string.Empty;
            uint bestDepth = 0;
            ushort bestDraw = 0;

            foreach (var name in AllContainerAddonNames)
            {
                if (!InventoryHelpers.TryGetVisibleAddon(name, out var a, WideAddonSearchMaxIndex) ||
                    !a->IsReady ||
                    !a->CheckWindowCollisionAtCoords(x, y))
                {
                    continue;
                }

                // Topmost window wins.
                var depth = a->DepthLayer;
                var draw = a->DrawOrderIndex;
                if (best == null || depth > bestDepth || depth == bestDepth && draw > bestDraw)
                {
                    best = a;
                    bestName = name;
                    bestDepth = depth;
                    bestDraw = draw;
                }
            }

            if (best == null || best->Id == 0)
            {
                return false;
            }

            lastHoverAddon = (bestName, best->Id, now);

            if (Configuration.DebugMode && now - lastCursorHitTestLogMs >= 1000)
            {
                lastCursorHitTestLogMs = now;
                Svc.Log.Information($"[QuickTransfer] (MMB) Cursor hit-test picked addon '{bestName}' (addonId={best->Id}) at ({x},{y}).");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // Last resort when nothing is hovered: act only if exactly one sortable window is open.
    private bool TryQueueMiddleClickSortFromVisibleWindows(long now)
    {
        try
        {
            var visibleCount = 0;
            InventoryType chosenType = default;
            var chosenSlot = -1;
            uint chosenAddonId = 0;

            void Consider(InventoryType[] containers, params string[] addonNames)
            {
                foreach (var name in addonNames)
                {
                    if (!InventoryHelpers.TryGetVisibleAddon(name, out var addon, WideAddonSearchMaxIndex))
                    {
                        continue;
                    }

                    if (InventoryHelpers.TryFindFirstOccupiedSlot(containers, out var t, out var s))
                    {
                        visibleCount++;
                        (chosenType, chosenSlot, chosenAddonId) = (t, s, addon->Id);
                    }

                    return;
                }
            }

            Consider(InventoryHelpers.ArmouryInventoryTypes, ArmouryBoardAddonName);
            Consider(InventoryHelpers.SaddlebagInventoryTypes, SaddlebagAddonName, Saddlebag2AddonName);
            Consider(InventoryHelpers.PlayerInventoryTypes, InventoryAddonName);
            Consider(InventoryHelpers.RetainerInventoryTypes, RetainerGrid0AddonName, RetainerGridAddonName, RetainerSellListAddonName);

            // The FC chest has no native Sort; queuing one of its pages triggers the organize pass.
            if (InventoryHelpers.TryGetVisibleAddon(FreeCompanyChestAddonName, out var fcc, WideAddonSearchMaxIndex))
            {
                var page = default(InventoryType?);
                if (TryGetRecentCompanyChestTab(fcc->Id, now, HoverMaxAgeMs, includeCrystals: false, out var recentPage, out var _))
                {
                    page = recentPage;
                }
                else if (GetCompanyChestInventoryTypes() is { Length: > 0 } pages)
                {
                    page = pages[0];
                }

                if (page != null)
                {
                    visibleCount++;
                    (chosenType, chosenSlot, chosenAddonId) = (page.Value, 0, fcc->Id);
                }
            }

            if (visibleCount != 1 || chosenAddonId == 0 || chosenSlot < 0)
            {
                return false;
            }

            var openSlot = DragDropHelpers.PickContextMenuSlot(chosenType, chosenSlot);
            QueueMiddleClickSort(chosenType, openSlot, chosenAddonId, now);

            if (Configuration.DebugMode)
            {
                Svc.Log.Information($"[QuickTransfer] (MMB) No hover DDI; bootstrapped from visible window: {chosenType} slot={openSlot} addonId={chosenAddonId}");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryQueueMiddleClickSortFromLastHoverAddon(long now)
    {
        try
        {
            var h = lastHoverAddon;
            if (h == null || now - h.Value.SeenAtMs > HoverMaxAgeMs)
            {
                return false;
            }

            var (addonName, addonId, _) = h.Value;

            bool Is(params string[] names) => names.Any(n => addonName.Equals(n, StringComparison.OrdinalIgnoreCase));

            InventoryType[] containers;
            if (Is(InventoryAddonName))
            {
                containers = InventoryHelpers.PlayerInventoryTypes;
            }
            else if (Is(SaddlebagAddonName, Saddlebag2AddonName))
            {
                containers = InventoryHelpers.SaddlebagInventoryTypes;
            }
            else if (Is(RetainerGrid0AddonName, RetainerGridAddonName, RetainerSellListAddonName))
            {
                containers = InventoryHelpers.RetainerInventoryTypes;
            }
            else if (Is(FreeCompanyChestAddonName))
            {
                return TryQueueCompanyChestOrganizeFromHover(addonId, now);
            }
            else if (Is(ArmouryAddonNames))
            {
                containers = InventoryHelpers.ArmouryInventoryTypes;
            }
            else
            {
                return false;
            }

            if (addonId == 0)
            {
                return false;
            }

            // A target that previously produced a context menu is the most reliable.
            if (lastGoodContextTargetByAddonId.TryGetValue(addonId, out var good) && InventoryHelpers.IsSortableContainerType(good.Type))
            {
                var openSlot = DragDropHelpers.PickContextMenuSlot(good.Type, good.Slot);
                QueueMiddleClickSort(good.Type, openSlot, addonId, now);
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information($"[QuickTransfer] (MMB) Using last-good target for hovered addon '{addonName}': {good.Type} slot={openSlot} addonId={addonId}");
                }

                return true;
            }

            if (!InventoryHelpers.TryFindFirstOccupiedSlot(containers, out var type, out var slot))
            {
                return false;
            }

            var bootstrapSlot = DragDropHelpers.PickContextMenuSlot(type, slot);
            QueueMiddleClickSort(type, bootstrapSlot, addonId, now);
            if (Configuration.DebugMode)
            {
                Svc.Log.Information($"[QuickTransfer] (MMB) Bootstrapped from hovered addon '{addonName}': {type} slot={bootstrapSlot} addonId={addonId}");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // Organizes only the active tab. If it cannot be determined, do nothing rather than guess
    // (guessing page 1 sorted the wrong tab).
    private bool TryQueueCompanyChestOrganizeFromHover(uint addonId, long now)
    {
        var fccMatches = InventoryHelpers.TryGetVisibleAddon(FreeCompanyChestAddonName, out var fcc, WideAddonSearchMaxIndex) && fcc->Id == addonId;

        // Reading the displayed page from the addon avoids relying on tab click params, which vary by client.
        if (fccMatches && TryResolveCompanyChestPageFromAddon(fcc, out var curPage) && InventoryHelpers.IsCompanyChestType(curPage))
        {
            QueueMiddleClickSort(curPage, 0, addonId, now);
            if (Configuration.DebugMode)
            {
                Svc.Log.Information($"[QuickTransfer] (MMB) Resolved active Company Chest tab from payload: {curPage} (addonId={addonId})");
            }

            return true;
        }

        if (Configuration.DebugMode && fccMatches)
        {
            Svc.Log.Information("[QuickTransfer] (MMB) Company Chest payload tab probe failed; falling back to hover/selected tab.");
        }

        if (TryGetRecentCompanyChestTab(addonId, now, CompanyChestTabMaxAgeMs, includeCrystals: false, out var recentPage, out var source))
        {
            QueueMiddleClickSort(recentPage, 0, addonId, now);
            if (Configuration.DebugMode)
            {
                Svc.Log.Information($"[QuickTransfer] (MMB) Using {source} Company Chest tab: {recentPage} slot=0 addonId={addonId}");
            }

            return true;
        }

        if (TryResolveCompanyChestSelectedPageFromAtkValues(addonId, out var atkPage))
        {
            QueueMiddleClickSort(atkPage, 0, addonId, now);
            if (Configuration.DebugMode)
            {
                Svc.Log.Information($"[QuickTransfer] (MMB) Using Company Chest tab from AtkValues: {atkPage} slot=0 addonId={addonId}");
            }

            return true;
        }

        if (Configuration.DebugMode)
        {
            Svc.Log.Information("[QuickTransfer] (MMB) Company Chest tab unknown; no action taken (waiting for a tab click or hover).");
        }

        return false;
    }

    private void TryQueueMiddleClickSortFromHover(long now)
    {
        if (!Configuration.Enabled || !ModifierBindings.IsMiddleClickConfigured(Configuration) || now - lastMiddleClickSortMs < 250)
        {
            return;
        }

        var hDdi = lastHoverDdi;
        if (hDdi == null || now - hDdi.Value.SeenAtMs > HoverMaxAgeMs)
        {
            if (TryUpdateLastHoverAddonFromCursorHitTest(now) && TryQueueMiddleClickSortFromLastHoverAddon(now) ||
                TryQueueMiddleClickSortFromLastHoverAddon(now) ||
                TryQueueMiddleClickSortFromVisibleWindows(now))
            {
                return;
            }

            if (Configuration.DebugMode)
            {
                Svc.Log.Information("[QuickTransfer] (MMB) No recent hover slot/dragdrop captured; cannot queue sort.");
            }

            return;
        }

        try
        {
            var ddiAddonId = hDdi.Value.AddonId;

            var ddiFresh = now - hDdi.Value.SeenAtMs <= 250;
            if (!ddiFresh && TryUpdateLastHoverAddonFromCursorHitTest(now) && TryQueueMiddleClickSortFromLastHoverAddon(now))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(lastHoverAddonName))
            {
                lastHoverAddon = (lastHoverAddonName, ddiAddonId, now);
                if (TryQueueMiddleClickSortFromLastHoverAddon(now))
                {
                    return;
                }
            }

            if (lastGoodContextTargetByAddonId.TryGetValue(ddiAddonId, out var good))
            {
                var openSlot = DragDropHelpers.PickContextMenuSlot(good.Type, good.Slot);
                QueueMiddleClickSort(good.Type, openSlot, ddiAddonId, now);
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information($"[QuickTransfer] (MMB) Used last-good target by addonId (no hover metadata): {good.Type} slot={openSlot} addonId={ddiAddonId}");
                }

                return;
            }

            // Open a short window so a native context menu opened by this press is still treated as a sort.
            pendingMiddleClickSortUntilMs = now + DeferredRequestTimeoutMs;
            lastMiddleClickSortMs = now;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[QuickTransfer] (MMB) Failed to queue sort from hover dragdrop.");
        }
    }

    private void ProcessDeferredSortMenuClick(long now)
    {
        var pendingSort = pendingDeferredSortMenuClick;
        if (pendingSort == null)
        {
            return;
        }

        var age = now - pendingSort.Value.EnqueuedAtMs;
        if (age < 50)
        {
            return;
        }

        var agent = (AgentInventoryContext*)pendingSort.Value.AgentPtr;
        if (age > DeferredRequestTimeoutMs)
        {
            if (Configuration.DebugMode)
            {
                Svc.Log.Information($"[QuickTransfer] (MMB) Deferred sort timed out (ContextItemCount={(agent != null ? agent->ContextItemCount : -1)}).");
            }

            pendingDeferredSortMenuClick = null;
            pendingMiddleClickSortUntilMs = 0;
            return;
        }

        try
        {
            var addon = (AtkUnitBase*)pendingSort.Value.AddonPtr;
            if (addon == null)
            {
                addon = InventoryHelpers.GetAddonByName(ContextMenuAddonName);
                if (addon != null)
                {
                    pendingDeferredSortMenuClick = (pendingSort.Value.AgentPtr, (nint)addon, pendingSort.Value.EnqueuedAtMs);
                }
            }

            // ContextItemCount stays 0 for a frame or two after OpenForItemSlot.
            if (agent == null || agent->ContextItemCount <= 0 || addon == null)
            {
                return;
            }

            if (ContextMenuHandler.TrySelectSortAndClose(agent, addon, out var chosenText, out var chosenIndex))
            {
                pendingDeferredSortMenuClick = null;
                pendingMiddleClickSortUntilMs = 0;
                lastActionTickMs = now;
                ArmSuppressContextMenu(now, 500);
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information(chosenIndex >= 0
                        ? $"[QuickTransfer] (MMB) Selected context action '{chosenText}' (idx={chosenIndex}) via deferred OnMenuOpened."
                        : "[QuickTransfer] (MMB) Already sorted (Undo Sort present); no action taken.");
                }

                return;
            }

            // The menu may still be populating; after ~300ms give up and close it so no hidden menu is left behind.
            if (age < 300)
            {
                return;
            }

            if (Configuration.DebugMode)
            {
                Svc.Log.Information($"[QuickTransfer] (MMB) Context menu opened but no 'Sort' entry was found (count={agent->ContextItemCount}).");
                ContextMenuHandler.DebugDumpContextMenu(agent, 32);
            }

            pendingDeferredSortMenuClick = null;
            pendingMiddleClickSortUntilMs = 0;
            ContextMenuHandler.CloseContextMenuAddon(agent, addon);
        }
        catch (Exception ex)
        {
            pendingDeferredSortMenuClick = null;
            pendingMiddleClickSortUntilMs = 0;
            Svc.Log.Warning(ex, "[QuickTransfer] Deferred sort select failed.");
        }
    }
}
