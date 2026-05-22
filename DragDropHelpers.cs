using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
namespace QuickTransfer;

/// <summary>
///     Helper functions for parsing drag-drop interfaces from UI events.
/// </summary>
internal static unsafe class DragDropHelpers
{
    // ArmouryBoard drag-drop payloads are not always (InventoryType, Slot).
    // On some builds the payload's Int1 is a category index, and Int2 is the slot within that category.
    // This mapping is best-effort and is only applied when we're sure the hover comes from the ArmouryBoard addon.
    internal static readonly InventoryType[] ArmouryBoardIndexToType =
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

    public static bool TryGetDragDropInterfaceFromReceiveEvent(
        AddonArgs args,
        AddonReceiveEventArgs recv,
        AtkEventType eventType,
        AtkEventData* eventData,
        out uint addonId,
        out AtkDragDropInterface* ddi)
    {
        addonId = 0;
        ddi = null;

        AtkUnitBase* addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null)
            return false;
        addonId = addon->Id;

        // List item events can provide a renderer directly.
        if (eventData != null &&
            eventType is AtkEventType.ListItemRollOver or AtkEventType.ListItemRollOut or AtkEventType.ListItemClick or
                AtkEventType.ListItemDoubleClick or AtkEventType.ListItemSelect)
        {
            try
            {
                AtkComponentListItemRenderer* r = eventData->ListItemData.ListItemRenderer;
                if (r != null)
                {
                    // Prefer the embedded DragDrop component if present.
                    if (r->DragDropComponent != null)
                        ddi = &r->DragDropComponent->AtkDragDropInterface;
                    else
                    {
                        try { ddi = &r->AtkDragDropInterface; }
                        catch
                        {
                            /* ignore */
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        if (ddi != null)
            return true;

        static AtkDragDropInterface* TryGetDdiFromList(AtkComponentList* list)
        {
            if (list == null)
                return null;

            // The list tracks a hovered item index itself, which is much safer than trying to interpret eventParam.
            // Prefer HoveredItemIndex, then fall back to other hover slots.
            static AtkDragDropInterface* FromIndex(AtkComponentList* l, int idx)
            {
                if (idx < 0 || idx > 512)
                    return null;
                try
                {
                    AtkComponentListItemRenderer* r = l->GetItemRenderer(idx);
                    return r != null ? &r->AtkDragDropInterface : null;
                }
                catch
                {
                    return null;
                }
            }

            AtkDragDropInterface* ddi0 = FromIndex(list, list->HoveredItemIndex);
            if (ddi0 != null)
                return ddi0;

            AtkDragDropInterface* ddi1 = FromIndex(list, list->HoveredItemIndex2);
            if (ddi1 != null)
                return ddi1;

            AtkDragDropInterface* ddi2 = FromIndex(list, list->HoveredItemIndex3);
            if (ddi2 != null)
                return ddi2;

            return null;
        }

        static AtkDragDropInterface* TryGetDdiFromComponent(AtkComponentBase* component)
        {
            if (component == null)
                return null;

            ComponentType t = component->GetComponentType();
            return t switch
            {
                ComponentType.DragDrop => &((AtkComponentDragDrop*)component)->AtkDragDropInterface,
                ComponentType.ListItemRenderer => &((AtkComponentListItemRenderer*)component)->AtkDragDropInterface,
                ComponentType.List => TryGetDdiFromList((AtkComponentList*)component),
                var _ => null
            };
        }

        // Prefer the drag-drop interface directly from event data when present.
        // IMPORTANT: only trust DragDropData for actual drag-drop event types; for MouseOver it can contain garbage.
        bool isDragDropEvent =
            eventType is AtkEventType.DragDropBegin or
                AtkEventType.DragDropCanAcceptCheck or
                AtkEventType.DragDropClick or
                AtkEventType.DragDropDiscard or
                AtkEventType.DragDropEnd or
                AtkEventType.DragDropInsert or
                AtkEventType.DragDropInsertAttempt or
                AtkEventType.DragDropRollOut or
                AtkEventType.DragDropRollOver;

        ddi = (isDragDropEvent && eventData != null) ? eventData->DragDropData.DragDropInterface : null;

        // Some drag-drop events (notably DragDropRollOver) provide a ComponentNode but not a DragDropInterface.
        // IMPORTANT: never read DragDropData.ComponentNode for non-dragdrop events (AtkEventData is a union).
        if (ddi == null && isDragDropEvent && eventData != null && eventData->DragDropData.ComponentNode != null)
        {
            try
            {
                AtkComponentNode* compNode = eventData->DragDropData.ComponentNode;
                AtkComponentBase* component = compNode->Component;
                ddi = TryGetDdiFromComponent(component);
            }
            catch
            {
                // ignore
            }
        }

        // Fallback: some event types provide MouseData, but the target is still a DragDrop component.
        if (ddi == null)
        {
            AtkEvent* atkEvent = (AtkEvent*)recv.AtkEvent;
            if (atkEvent != null && atkEvent->Node != null)
            {
                AtkResNode* node = atkEvent->Node;
                AtkComponentNode* compNode = node->GetAsAtkComponentNode();
                if (compNode != null)
                {
                    AtkComponentBase* component = compNode->Component;
                    ddi = TryGetDdiFromComponent(component);
                }
            }
        }

        if (ddi == null)
            return false;

        return true;
    }

    public static bool TryGetSlotFromDragDropInterface(
        AtkDragDropInterface* ddi,
        out InventoryType invType,
        out int slot)
    {
        invType = default;
        slot = -1;
        if (ddi == null)
            return false;

        AtkDragDropPayloadContainer* payload = ddi->GetPayloadContainer();
        if (payload == null)
            return false;

        invType = (InventoryType)payload->Int1;
        slot = payload->Int2;
        if (slot < 0 || slot > 500)
            return false;

        return true;
    }

    public static bool TryGetSlotFromDragDropInterfaceForAddon(
        AtkDragDropInterface* ddi,
        string addonName,
        uint addonId,
        out InventoryType invType,
        out int slot,
        out int rawInt1,
        out int rawInt2,
        out uint rawFlags)
    {
        invType = default;
        slot = -1;
        rawInt1 = 0;
        rawInt2 = 0;
        rawFlags = 0;

        if (ddi == null)
            return false;

        AtkDragDropPayloadContainer* payload;
        try
        {
            payload = ddi->GetPayloadContainer();
        }
        catch
        {
            return false;
        }
        if (payload == null)
            return false;

        rawInt1 = payload->Int1;
        rawInt2 = payload->Int2;
        rawFlags = payload->Flags;

        // Default interpretation (most inventory add-ons): (InventoryType, Slot)
        invType = (InventoryType)rawInt1;
        slot = rawInt2;

        // ArmouryBoard special-case: some builds use (CategoryIndex, Slot)
        // and Int1 may look like Inventory1..Inventory4 (0..3), which is clearly wrong for ArmouryBoard.
        if (!string.IsNullOrEmpty(addonName) &&
            addonName.Equals("ArmouryBoard", StringComparison.OrdinalIgnoreCase) &&
            InventoryHelpers.TryGetVisibleAddon("ArmouryBoard", out AtkUnitBase* ab) &&
            ab != null &&
            ab->Id == addonId)
        {
            if (rawInt1 >= 0 && rawInt1 < ArmouryBoardIndexToType.Length)
            {
                invType = ArmouryBoardIndexToType[rawInt1];
                slot = rawInt2;
            }
        }

        if (slot < 0 || slot > 500)
            return false;

        return true;
    }

    public static int PickContextMenuSlot(InventoryType type, int preferredSlot)
    {
        try
        {
            InventoryManager* inv = InventoryManager.Instance();
            if (inv == null)
                return preferredSlot;

            InventoryContainer* c = inv->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded || c->Size <= 0)
                return preferredSlot;

            // Prefer the hovered slot when in range AND it contains an item.
            if (preferredSlot >= 0 && preferredSlot < c->Size)
            {
                InventoryItem* it0 = c->GetInventorySlot(preferredSlot);
                if (it0 != null && it0->ItemId != 0)
                    return preferredSlot;
            }

            // Fallback: find the first slot with an item.
            for(int i = 0; i < c->Size; i++)
            {
                InventoryItem* it = c->GetInventorySlot(i);
                if (it != null && it->ItemId != 0)
                    return i;
            }
        }
        catch
        {
            // ignore
        }

        return preferredSlot;
    }
}
