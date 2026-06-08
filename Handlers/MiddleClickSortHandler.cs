using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Keys;
using ECommons;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;
using AutoContextAction = QuickTransfer.ContextMenuHandler.AutoContextAction;
using ModifierMode = QuickTransfer.ContextMenuHandler.ModifierMode;

namespace QuickTransfer;

public sealed unsafe partial class Plugin
{
    private bool TryUpdateLastHoverAddonFromCollisionManager(long now)
    {
        try
        {
            AtkStage* stage = AtkStage.Instance();
            if (stage == null || stage->AtkCollisionManager == null)
                return false;

            AtkUnitBase* hit = stage->AtkCollisionManager->IntersectingAddon;
            if (hit == null || hit->Id == 0)
                return false;

            // We can't reliably compare addon pointers here:
            // - The collision manager can report child addons/overlays
            // - Some users have addon indices > 6
            //
            // Instead, map via Id/HostId/ParentId to a known *owner* addon window.
            bool TryGetVisibleAddonId(string name, out uint id)
            {
                id = 0;
                try
                {
                    if (InventoryHelpers.TryGetVisibleAddon(name, out AtkUnitBase* a, QuickTransferConstants.WideAddonSearchMaxIndex) && a != null && a->Id != 0)
                    {
                        id = a->Id;
                        return true;
                    }
                }
                catch
                {
                    // ignore
                }

                return false;
            }

            Dictionary<uint, string> visibleById = new(capacity: 16);
            void AddVisible(string name)
            {
                if (TryGetVisibleAddonId(name, out uint id) && id != 0)
                {
                    // Don't overwrite an existing mapping. This prevents rare mis-labeling if an alias query
                    // accidentally returns an unexpected addon that reuses an id already mapped earlier.
                    visibleById.TryAdd(id, name);
                }
            }

            AddVisible("Inventory");
            AddVisible("InventoryBuddy");
            AddVisible("InventoryBuddy2");
            AddVisible("RetainerGrid0");
            AddVisible("RetainerGrid");
            AddVisible("RetainerSellList");
            AddVisible(QuickTransferConstants.FreeCompanyChestAddonName);
            foreach(string n in QuickTransferConstants.ArmouryAddonNames)
                AddVisible(n);

            uint hitId = hit->Id;
            uint hostId = hit->HostId;
            uint parentId = hit->ParentId;

            uint ownerId = 0;
            string ownerName = string.Empty;
            string ownerSource = string.Empty;

            bool Pick(uint id)
            {
                if (id == 0)
                    return false;
                if (!visibleById.TryGetValue(id, out string? n))
                    return false;
                ownerId = id;
                ownerName = n;
                ownerSource = "visible";
                return true;
            }

            // Prefer direct hit, then host, then parent.
            if (!Pick(hitId) && !Pick(hostId) && !Pick(parentId))
            {
                static string InferOwnerNameFromInvType(InventoryType t)
                {
                    if (InventoryHelpers.IsPlayerInventoryType(t))
                        return "Inventory";
                    if (InventoryHelpers.IsSaddlebagType(t))
                        return "InventoryBuddy";
                    if (InventoryHelpers.IsArmouryType(t))
                        return "ArmouryBoard";
                    if (InventoryHelpers.IsCompanyChestType(t))
                        return QuickTransferConstants.FreeCompanyChestAddonName;
                    if (InventoryHelpers.IsRetainerType(t))
                        return "RetainerGrid0";
                    return string.Empty;
                }

                bool PickFromLastGood(uint id)
                {
                    if (id == 0)
                        return false;
                    if (!lastGoodContextTargetByAddonId.TryGetValue(id, out (InventoryType Type, int Slot, int A4) good))
                        return false;

                    string inferred = InferOwnerNameFromInvType(good.Type);
                    if (string.IsNullOrEmpty(inferred))
                        return false;

                    ownerId = id;
                    ownerName = inferred;
                    ownerSource = "lastGood";
                    return true;
                }

                // If the owner window isn't visible via addon lookup (common for Inventory), fall back to previously observed "good" targets.
                if (!PickFromLastGood(hitId) && !PickFromLastGood(hostId) && !PickFromLastGood(parentId))
                {
                    // Heuristic: Inventory's owner addon id is commonly 17, while collision hits are child ids (e.g. 108/110)
                    // with HostId/ParentId pointing at 17. Prefer that to make MMB Inventory sort work even without a prior RClick.
                    const uint inventoryOwnerId = 17;
                    if (hostId == inventoryOwnerId || parentId == inventoryOwnerId)
                    {
                        ownerId = inventoryOwnerId;
                        ownerName = "Inventory";
                        ownerSource = "heuristic17";
                    }
                    else
                    {
                        // If we couldn't map to a known owner addon, still log what we saw to help diagnose.
                        if (Configuration.DebugMode && now - lastCursorHitTestLogMs >= 1000)
                        {
                            lastCursorHitTestLogMs = now;
                            Log.Information($"[QuickTransfer] (MMB) CollisionManager hit addonId={hitId} hostId={hostId} parentId={parentId} (unmapped). Visible owners=[{string.Join(", ", visibleById.Select(kv => $"{kv.Value}:{kv.Key}"))}] lastGoodOwnerIds=[{string.Join(", ", lastGoodContextTargetByAddonId.Keys.Take(24))}]");
                        }
                        return false;
                    }
                }
            }

            lastHoverAddon = (ownerName, ownerId, now);

            if (Configuration.DebugMode && now - lastCursorHitTestLogMs >= 1000)
            {
                lastCursorHitTestLogMs = now;
                Log.Information($"[QuickTransfer] (MMB) CollisionManager picked addon '{ownerName}' (ownerAddonId={ownerId}, hitAddonId={hitId}, source={ownerSource}).");
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
            // Prefer the game's own collision manager; it already knows what addon is under the cursor.
            if (TryUpdateLastHoverAddonFromCollisionManager(now))
                return true;

            if (!CursorHoverHelpers.TryGetClientCursorPos(out short x, out short y))
                return false;

            AtkUnitBase* best = null;
            string bestName = string.Empty;
            uint bestId = 0;
            uint bestDepth = 0;
            ushort bestDraw = 0;

            void Consider(string? name, AtkUnitBase* a)
            {
                if (a == null)
                    return;

                try
                {
                    if (!a->IsVisible || !a->IsReady)
                        return;

                    if (!a->CheckWindowCollisionAtCoords(x, y))
                        return;

                    uint depth = a->DepthLayer;
                    ushort draw = a->DrawOrderIndex;
                    if (best == null || depth > bestDepth || (depth == bestDepth && draw > bestDraw))
                    {
                        best = a;
                        bestName = name ?? string.Empty;
                        bestId = a->Id;
                        bestDepth = depth;
                        bestDraw = draw;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (InventoryHelpers.TryGetVisibleAddon("Inventory", out AtkUnitBase* inv) && inv != null)
                Consider("Inventory", inv);

            if (InventoryHelpers.TryGetVisibleAddon("InventoryBuddy", out AtkUnitBase* sb) && sb != null)
                Consider("InventoryBuddy", sb);
            if (InventoryHelpers.TryGetVisibleAddon("InventoryBuddy2", out AtkUnitBase* sb2) && sb2 != null)
                Consider("InventoryBuddy2", sb2);

            if (InventoryHelpers.TryGetVisibleAddon("RetainerGrid0", out AtkUnitBase* rg0, QuickTransferConstants.WideAddonSearchMaxIndex) && rg0 != null)
                Consider("RetainerGrid0", rg0);
            if (InventoryHelpers.TryGetVisibleAddon("RetainerGrid", out AtkUnitBase* rg, QuickTransferConstants.WideAddonSearchMaxIndex) && rg != null)
                Consider("RetainerGrid", rg);
            if (InventoryHelpers.TryGetVisibleAddon("RetainerSellList", out AtkUnitBase* rsl, QuickTransferConstants.WideAddonSearchMaxIndex) && rsl != null)
                Consider("RetainerSellList", rsl);

            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out AtkUnitBase* fcc, QuickTransferConstants.WideAddonSearchMaxIndex) && fcc != null)
                Consider(QuickTransferConstants.FreeCompanyChestAddonName, fcc);

            foreach(string n in QuickTransferConstants.ArmouryAddonNames)
            {
                if (InventoryHelpers.TryGetVisibleAddon(n, out AtkUnitBase* ab) && ab != null)
                    Consider(n, ab);
            }

            if (best == null || bestId == 0 || string.IsNullOrEmpty(bestName))
                return false;

            lastHoverAddon = (bestName, bestId, now);

            if (Configuration.DebugMode && now - lastCursorHitTestLogMs >= 1000)
            {
                lastCursorHitTestLogMs = now;
                Log.Information($"[QuickTransfer] (MMB) Cursor hit-test picked addon '{bestName}' (addonId={bestId}) at ({x},{y}).");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryQueueMiddleClickSortFromVisibleWindows(long now)
    {
        try
        {
            // If multiple inventory windows are open, we can't know which one the cursor is over without a hover DDI.
            // In that case, refuse and require hover capture.
            int visibleCount = 0;

            InventoryType chosenType = default;
            int chosenSlot = -1;
            uint chosenAddonId = 0;

            // ArmouryBoard
            if (InventoryHelpers.TryGetVisibleAddon("ArmouryBoard", out AtkUnitBase* ab, QuickTransferConstants.WideAddonSearchMaxIndex) && ab != null)
            {
                if (DragDropHelpers.TryResolveTargetFromWeirdPayload(DragDropHelpers.ArmouryBoardIndexToType, -1, -1, -1, out InventoryType t, out int s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = ab->Id;
                }
            }

            // Saddlebags
            if (InventoryHelpers.TryGetVisibleAddon("InventoryBuddy", out AtkUnitBase* sb, QuickTransferConstants.WideAddonSearchMaxIndex) && sb != null)
            {
                if (DragDropHelpers.TryResolveTargetFromWeirdPayload(QuickTransferConstants.SaddlebagInventoryTypes, -1, -1, -1, out InventoryType t, out int s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = sb->Id;
                }
            }
            else if (InventoryHelpers.TryGetVisibleAddon("InventoryBuddy2", out AtkUnitBase* sb2, QuickTransferConstants.WideAddonSearchMaxIndex) && sb2 != null)
            {
                if (DragDropHelpers.TryResolveTargetFromWeirdPayload(QuickTransferConstants.SaddlebagInventoryTypes, -1, -1, -1, out InventoryType t, out int s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = sb2->Id;
                }
            }

            // Player inventory
            if (InventoryHelpers.TryGetVisibleAddon("Inventory", out AtkUnitBase* inv, QuickTransferConstants.WideAddonSearchMaxIndex) && inv != null)
            {
                if (DragDropHelpers.TryResolveTargetFromWeirdPayload(QuickTransferConstants.PlayerInventoryTypes, -1, -1, -1, out InventoryType t, out int s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = inv->Id;
                }
            }

            // Retainer inventory
            if (InventoryHelpers.TryGetVisibleAddon("RetainerGrid0", out AtkUnitBase* rg0, QuickTransferConstants.WideAddonSearchMaxIndex) && rg0 != null)
            {
                if (DragDropHelpers.TryResolveTargetFromWeirdPayload(QuickTransferConstants.RetainerInventoryTypes, -1, -1, -1, out InventoryType t, out int s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = rg0->Id;
                }
            }
            else if (InventoryHelpers.TryGetVisibleAddon("RetainerGrid", out AtkUnitBase* rg, QuickTransferConstants.WideAddonSearchMaxIndex) && rg != null)
            {
                if (DragDropHelpers.TryResolveTargetFromWeirdPayload(QuickTransferConstants.RetainerInventoryTypes, -1, -1, -1, out InventoryType t, out int s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = rg->Id;
                }
            }
            else if (InventoryHelpers.TryGetVisibleAddon("RetainerSellList", out AtkUnitBase* rsl, QuickTransferConstants.WideAddonSearchMaxIndex) && rsl != null)
            {
                if (DragDropHelpers.TryResolveTargetFromWeirdPayload(QuickTransferConstants.RetainerInventoryTypes, -1, -1, -1, out InventoryType t, out int s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = rsl->Id;
                }
            }

            // Free Company Chest (no native Sort context menu; MMB triggers our organize pass)
            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out AtkUnitBase* fcc, QuickTransferConstants.WideAddonSearchMaxIndex) && fcc != null)
            {
                (InventoryType Page, uint AddonId, long SeenAtMs)? lp = lastHoverCompanyChestPage;
                if (lp != null && lp.Value.AddonId == fcc->Id && now - lp.Value.SeenAtMs <= 20000 && InventoryHelpers.IsCompanyChestType(lp.Value.Page))
                {
                    visibleCount++;
                    chosenType = lp.Value.Page;
                    chosenSlot = 0;
                    chosenAddonId = fcc->Id;
                }
                else
                {
                    (InventoryType Page, uint AddonId, long SeenAtMs)? sp = lastSelectedCompanyChestPage;
                    if (sp != null && sp.Value.AddonId == fcc->Id && now - sp.Value.SeenAtMs <= 20000 && InventoryHelpers.IsCompanyChestType(sp.Value.Page))
                    {
                        visibleCount++;
                        chosenType = sp.Value.Page;
                        chosenSlot = 0;
                        chosenAddonId = fcc->Id;
                    }
                    else
                    {
                        InventoryType[] pages = GetCompanyChestInventoryTypes();
                        if (pages.Length > 0)
                        {
                            visibleCount++;
                            chosenType = pages[0];
                            chosenSlot = 0;
                            chosenAddonId = fcc->Id;
                        }
                    }
                }
            }

            if (visibleCount != 1 || chosenAddonId == 0 || chosenSlot < 0)
                return false;

            int openSlot = DragDropHelpers.PickContextMenuSlot(chosenType, chosenSlot);
            pendingMiddleClickSortRequest = (chosenType, openSlot, chosenAddonId, now);
            pendingMiddleClickSortUntilMs = now + 1500;
            lastMiddleClickSortMs = now;

            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] (MMB) No hover DDI; bootstrapped from visible window: {chosenType} slot={openSlot} addonId={chosenAddonId}");

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
            (string AddonName, uint AddonId, long SeenAtMs)? h = lastHoverAddon;
            if (h == null || now - h.Value.SeenAtMs > 20000)
                return false;

            string addonName = h.Value.AddonName;
            uint addonId = h.Value.AddonId;

            ReadOnlySpan<InventoryType> containers = default;
            if (addonName.Equals("Inventory", StringComparison.OrdinalIgnoreCase))
                containers = QuickTransferConstants.PlayerInventoryTypes;
            else if (addonName.Equals("InventoryBuddy", StringComparison.OrdinalIgnoreCase) || addonName.Equals("InventoryBuddy2", StringComparison.OrdinalIgnoreCase))
                containers = QuickTransferConstants.SaddlebagInventoryTypes;
            else if (addonName.Equals("RetainerGrid0", StringComparison.OrdinalIgnoreCase) ||
                     addonName.Equals("RetainerGrid", StringComparison.OrdinalIgnoreCase) ||
                     addonName.Equals("RetainerSellList", StringComparison.OrdinalIgnoreCase))
                containers = QuickTransferConstants.RetainerInventoryTypes;
            else if (addonName.Equals(QuickTransferConstants.FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase))
            {
                const long companyChestTabMaxAgeMs = 180000; // 3 minutes

                // FC Chest has no native "Sort"; MMB triggers our organize pass.
                // Run only on the currently selected tab, approximated as the most recently hovered/clicked FreeCompanyPage payload.

                // First preference: read the currently displayed page directly from the addon via a payload probe.
                // This avoids relying on tab ButtonClick params, which vary across clients/builds.
                if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out AtkUnitBase* fcc, QuickTransferConstants.WideAddonSearchMaxIndex) &&
                    fcc != null &&
                    fcc->Id == addonId &&
                    TryResolveCompanyChestPageFromAddon(fcc, out InventoryType curPage) &&
                    InventoryHelpers.IsCompanyChestType(curPage))
                {
                    pendingMiddleClickSortRequest = (curPage, 0, addonId, now);
                    pendingMiddleClickSortUntilMs = now + 1500;
                    lastMiddleClickSortMs = now;
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] (MMB) Resolved active Company Chest tab from payload: {curPage} (addonId={addonId})");
                    return true;
                }
                if (Configuration.DebugMode && InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out AtkUnitBase* fccDbg, QuickTransferConstants.WideAddonSearchMaxIndex) && fccDbg != null && fccDbg->Id == addonId)
                {
                    // Diagnostic: we expected to be able to infer the active page from visible payloads, but couldn't.
                    // This helps identify whether the probe is failing entirely or just returning a non-page payload.
                    Log.Information("[QuickTransfer] (MMB) Company Chest payload tab probe failed; falling back to hover/selected tab.");
                }

                (InventoryType Page, uint AddonId, long SeenAtMs)? lp = lastHoverCompanyChestPage;
                if (lp != null && lp.Value.AddonId == addonId && now - lp.Value.SeenAtMs <= companyChestTabMaxAgeMs && InventoryHelpers.IsCompanyChestType(lp.Value.Page))
                {
                    pendingMiddleClickSortRequest = (lp.Value.Page, 0, addonId, now);
                    pendingMiddleClickSortUntilMs = now + 1500;
                    lastMiddleClickSortMs = now;
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] (MMB) Using last-hovered Company Chest tab: {lp.Value.Page} slot=0 addonId={addonId}");
                    return true;
                }

                (InventoryType Page, uint AddonId, long SeenAtMs)? sp = lastSelectedCompanyChestPage;
                if (sp != null && sp.Value.AddonId == addonId && now - sp.Value.SeenAtMs <= companyChestTabMaxAgeMs && InventoryHelpers.IsCompanyChestType(sp.Value.Page))
                {
                    pendingMiddleClickSortRequest = (sp.Value.Page, 0, addonId, now);
                    pendingMiddleClickSortUntilMs = now + 1500;
                    lastMiddleClickSortMs = now;
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] (MMB) Using selected Company Chest tab: {sp.Value.Page} slot=0 addonId={addonId}");
                    return true;
                }

                if (TryResolveCompanyChestSelectedPageFromAtkValues(addonId, out InventoryType atkPage))
                {
                    pendingMiddleClickSortRequest = (atkPage, 0, addonId, now);
                    pendingMiddleClickSortUntilMs = now + 1500;
                    lastMiddleClickSortMs = now;
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] (MMB) Using Company Chest tab from AtkValues: {atkPage} slot=0 addonId={addonId}");
                    return true;
                }

                // If we couldn't infer the selected tab, do NOT guess (guessing Page1 is what causes "I clicked tab 2 but it sorted tab 1").
                if (Configuration.DebugMode)
                    Log.Information("[QuickTransfer] (MMB) Company Chest tab unknown; no action taken (waiting for a tab click or hover).");
                return false;
            }
            else if (QuickTransferConstants.ArmouryAddonNames.Any(n => addonName.Equals(n, StringComparison.OrdinalIgnoreCase)))
                containers = DragDropHelpers.ArmouryBoardIndexToType;

            if (containers.Length == 0 || addonId == 0)
                return false;

            // Prefer last known-good context target when available (more likely to produce a menu).
            if (lastGoodContextTargetByAddonId.TryGetValue(addonId, out (InventoryType Type, int Slot, int A4) good) &&
                (InventoryHelpers.IsPlayerInventoryType(good.Type) || InventoryHelpers.IsArmouryType(good.Type) || InventoryHelpers.IsSaddlebagType(good.Type) || InventoryHelpers.IsRetainerType(good.Type) || InventoryHelpers.IsCompanyChestType(good.Type)))
            {
                int openSlot = DragDropHelpers.PickContextMenuSlot(good.Type, good.Slot);
                pendingMiddleClickSortRequest = (good.Type, openSlot, addonId, now);
                pendingMiddleClickSortUntilMs = now + 1500;
                lastMiddleClickSortMs = now;
                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] (MMB) Using last-good target for hovered addon '{addonName}': {good.Type} slot={openSlot} addonId={addonId}");
                return true;
            }

            if (!DragDropHelpers.TryResolveTargetFromWeirdPayload(containers, -1, -1, -1, out InventoryType type, out int slot))
                return false;

            int openSlot2 = DragDropHelpers.PickContextMenuSlot(type, slot);
            pendingMiddleClickSortRequest = (type, openSlot2, addonId, now);
            pendingMiddleClickSortUntilMs = now + 1500;
            lastMiddleClickSortMs = now;
            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] (MMB) Bootstrapped from hovered addon '{addonName}': {type} slot={openSlot2} addonId={addonId}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TryQueueMiddleClickSortFromHover(long now)
    {
        if (!Configuration.Enabled || !Configuration.EnableMiddleClickSort)
            return;

        if (now - lastMiddleClickSortMs < 250)
            return;

        (nint DdiPtr, uint AddonId, long SeenAtMs)? hDdi = lastHoverDdi;
        // Rollover events only fire when moving the cursor; keep a generous window so MMB works while stationary.
        if (hDdi == null || now - hDdi.Value.SeenAtMs > 20000)
        {
            // Inventory sometimes does not emit hover events; fall back to a window hit-test at the cursor.
            // This also lets us disambiguate which window is being targeted when multiple are open.
            if (TryUpdateLastHoverAddonFromCursorHitTest(now) && TryQueueMiddleClickSortFromLastHoverAddon(now))
                return;

            if (TryQueueMiddleClickSortFromLastHoverAddon(now))
                return;
            if (TryQueueMiddleClickSortFromVisibleWindows(now))
                return;
            if (Configuration.DebugMode)
                Log.Information("[QuickTransfer] (MMB) No recent hover slot/dragdrop captured; cannot queue sort.");
            return;
        }

        try
        {
            uint ddiAddonId = hDdi.Value.AddonId;

            // Key rule for stability across windows:
            // - A stored hover DDI can be stale if the UI doesn't emit MouseOut/RollOut events (common for Inventory/Saddlebags).
            // - Therefore, if the DDI wasn't updated very recently, prefer a live hit-test (collision manager) to determine
            //   which window is actually under the cursor right now.
            //
            // Armoury remains stable because the collision manager typically also reports it correctly, and we no longer
            // let stale "lastHoverAddon" from other windows override a fresh cursor hit-test.
            bool ddiFresh = now - hDdi.Value.SeenAtMs <= 250;
            if (!ddiFresh)
            {
                if (TryUpdateLastHoverAddonFromCursorHitTest(now) && TryQueueMiddleClickSortFromLastHoverAddon(now))
                    return;
            }

            // Otherwise, use the DDI's addon id and cached addon name as the target.
            if (!string.IsNullOrWhiteSpace(lastHoverAddonName))
            {
                lastHoverAddon = (lastHoverAddonName, ddiAddonId, now);
                if (TryQueueMiddleClickSortFromLastHoverAddon(now))
                    return;
            }

            // As a fallback, still allow using the last-good target for this addon id.
            if (lastGoodContextTargetByAddonId.TryGetValue(ddiAddonId, out (InventoryType Type, int Slot, int A4) good2))
            {
                int openSlot = DragDropHelpers.PickContextMenuSlot(good2.Type, good2.Slot);
                pendingMiddleClickSortRequest = (good2.Type, openSlot, ddiAddonId, now);
                pendingMiddleClickSortUntilMs = now + 1500;
                lastMiddleClickSortMs = now;
                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] (MMB) Used last-good target by addonId (no hover metadata): {good2.Type} slot={openSlot} addonId={ddiAddonId}");
                return;
            }

            // If we can't decide safely, do nothing.
            pendingMiddleClickSortUntilMs = now + 1500;
            lastMiddleClickSortMs = now;
        }
        catch(Exception ex)
        {
            // Best-effort only; avoid crashing the client if the hovered pointer becomes invalid.
            Log.Warning(ex, "[QuickTransfer] (MMB) Failed to queue sort from hover dragdrop.");
        }
    }
    private void ProcessDeferredSortMenuClick(long now)
    {
        (nint AgentPtr, nint AddonPtr, long EnqueuedAtMs)? pendingSort = pendingDeferredSortMenuClick;
        if (pendingSort == null)
            return;

        // Give the context menu a moment to populate after OpenForItemSlot.
        if (now - pendingSort.Value.EnqueuedAtMs < 50)
            return;

        if (now - pendingSort.Value.EnqueuedAtMs > 1500)
        {
            if (Configuration.DebugMode)
            {
                try
                {
                    AgentInventoryContext* agent = (AgentInventoryContext*)pendingSort.Value.AgentPtr;
                    int count = agent != null ? agent->ContextItemCount : -1;
                    Log.Information($"[QuickTransfer] (MMB) Deferred sort timed out (ContextItemCount={count}).");
                }
                catch
                {
                    Log.Information("[QuickTransfer] (MMB) Deferred sort timed out.");
                }
            }
            pendingDeferredSortMenuClick = null;
            pendingMiddleClickSortUntilMs = 0;
            return;
        }

        try
        {
            AgentInventoryContext* agent = (AgentInventoryContext*)pendingSort.Value.AgentPtr;
            AtkUnitBase* addon = (AtkUnitBase*)pendingSort.Value.AddonPtr;

            // If we didn't have the ContextMenu addon pointer yet, try to resolve it now.
            if (addon == null)
            {
                try
                {
                    AtkUnitBase* cm = AddonHelpers.GetAddonByName("ContextMenu");
                    if (cm != null)
                    {
                        addon = cm;
                        pendingDeferredSortMenuClick = (pendingSort.Value.AgentPtr, (nint)addon, pendingSort.Value.EnqueuedAtMs);
                        pendingSort = pendingDeferredSortMenuClick;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            // If the menu hasn't populated yet, keep waiting.
            // AgentInventoryContext::ContextItemCount tends to remain 0 for a frame or two after OpenForItemSlot.
            if (agent == null || agent->ContextItemCount <= 0)
                return;

            if (addon == null)
                return;

            if (ContextMenuHandler.TrySelectSortAndClose(agent, addon, out string chosenText, out int chosenIndex))
            {
                pendingDeferredSortMenuClick = null;
                pendingMiddleClickSortUntilMs = 0;
                lastActionTickMs = now;
                ArmSuppressContextMenu(now, 500);
                if (Configuration.DebugMode)
                {
                    Log.Information(chosenIndex >= 0 ? $"[QuickTransfer] (MMB) Selected context action '{chosenText}' (idx={chosenIndex}) via deferred OnMenuOpened." : "[QuickTransfer] (MMB) Already sorted (Undo Sort present); no action taken.");
                }
            }
            else
            {
                // If we opened a menu but didn't find Sort, wait briefly in case the menu is still updating.
                // After ~300ms, give up and close it to avoid leaving a hidden menu behind.
                if (now - pendingSort.Value.EnqueuedAtMs < 300)
                    return;

                if (Configuration.DebugMode)
                {
                    Log.Information($"[QuickTransfer] (MMB) Context menu opened but no 'Sort' entry was found (count={agent->ContextItemCount}).");
                    ContextMenuHandler.DebugDumpContextMenu(agent, 32);
                }

                pendingDeferredSortMenuClick = null;
                pendingMiddleClickSortUntilMs = 0;
                try
                {
                    ContextMenuHandler.CloseContextMenuAddon(agent, addon);
                }
                catch
                {
                    /* ignore */
                }
            }
        }
        catch(Exception ex)
        {
            pendingDeferredSortMenuClick = null;
            pendingMiddleClickSortUntilMs = 0;
            Log.Warning(ex, "[QuickTransfer] Deferred sort select failed.");
        }
    }
}

