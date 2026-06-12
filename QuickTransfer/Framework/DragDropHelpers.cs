using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
namespace QuickTransfer.Framework;

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
                if (idx is < 0 or > 512)
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
            return ddi2 != null ? ddi2 : null;
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

        return ddi != null;
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
        return slot is not < 0 and not > 500;
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

    public static bool TryResolveTargetFromWeirdPayload(
        ReadOnlySpan<InventoryType> containers,
        int rawInt1,
        int rawInt2,
        short refIdx,
        out InventoryType type,
        out int slot)
    {
        type = default;
        slot = -1;

        try
        {
            if (containers.Length == 0)
                return false;

            InventoryManager* inv = InventoryManager.Instance();
            if (inv == null)
                return false;

            List<int> candidates = new(capacity: 4) { rawInt2, rawInt1, refIdx };
            foreach(int s in candidates.Distinct())
            {
                if (s is < 0 or > 500)
                    continue;

                foreach(InventoryType t in containers)
                {
                    InventoryItem* it = inv->GetInventorySlot(t, s);
                    if (it != null && it->ItemId != 0)
                    {
                        type = t;
                        slot = s;
                        return true;
                    }
                }
            }

            foreach(InventoryType t in containers)
            {
                InventoryContainer* c = inv->GetInventoryContainer(t);
                if (c == null || !c->IsLoaded || c->Size <= 0)
                    continue;

                for(int i = 0; i < c->Size; i++)
                {
                    InventoryItem* it = c->GetInventorySlot(i);
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
            // ignore
        }

        return false;
    }

    public static AtkDragDropInterface* TryGetDdiFromListIndex(AtkComponentList* list, int idx)
    {
        if (list == null)
            return null;
        if (idx is < 0 or > 512)
            return null;
        try
        {
            AtkComponentListItemRenderer* r = list->GetItemRenderer(idx);
            return r != null ? &r->AtkDragDropInterface : null;
        }
        catch
        {
            return null;
        }
    }

    public static AtkDragDropInterface* TryGetDdiFromComponent(AtkComponentBase* component, int preferredListIndex = 0)
    {
        if (component == null)
            return null;

        try
        {
            ComponentType t = component->GetComponentType();
            return t switch
            {
                ComponentType.DragDrop => &((AtkComponentDragDrop*)component)->AtkDragDropInterface,
                ComponentType.ListItemRenderer => &((AtkComponentListItemRenderer*)component)->AtkDragDropInterface,
                ComponentType.List => TryGetDdiFromListIndex((AtkComponentList*)component, preferredListIndex),
                var _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
