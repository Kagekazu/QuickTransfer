using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
namespace QuickTransfer.Framework;

internal static unsafe class DragDropHelpers
{
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

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null)
        {
            return false;
        }

        addonId = addon->Id;

        // List item events can provide a renderer directly.
        if (eventData != null &&
            eventType is AtkEventType.ListItemRollOver or AtkEventType.ListItemRollOut or AtkEventType.ListItemClick or
                AtkEventType.ListItemDoubleClick or AtkEventType.ListItemSelect)
        {
            try
            {
                var r = eventData->ListItemData.ListItemRenderer;
                if (r != null)
                {
                    ddi = r->DragDropComponent != null
                        ? &r->DragDropComponent->AtkDragDropInterface
                        : &r->AtkDragDropInterface;
                }
            }
            catch
            {
            }
        }

        if (ddi != null)
        {
            return true;
        }

        var isDragDropEvent =
            eventType is AtkEventType.DragDropBegin or
                AtkEventType.DragDropCanAcceptCheck or
                AtkEventType.DragDropClick or
                AtkEventType.DragDropDiscard or
                AtkEventType.DragDropEnd or
                AtkEventType.DragDropInsert or
                AtkEventType.DragDropInsertAttempt or
                AtkEventType.DragDropRollOut or
                AtkEventType.DragDropRollOver;

        ddi = isDragDropEvent && eventData != null ? eventData->DragDropData.DragDropInterface : null;

        if (ddi == null && isDragDropEvent && eventData != null && eventData->DragDropData.ComponentNode != null)
        {
            try
            {
                ddi = TryGetDdiFromComponentOrHoveredList(eventData->DragDropData.ComponentNode->Component);
            }
            catch
            {
            }
        }

        if (ddi == null)
        {
            var atkEvent = (AtkEvent*)recv.AtkEvent;
            if (atkEvent != null && atkEvent->Node != null)
            {
                var compNode = atkEvent->Node->GetAsAtkComponentNode();
                if (compNode != null)
                {
                    ddi = TryGetDdiFromComponentOrHoveredList(compNode->Component);
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
        {
            return false;
        }

        var payload = ddi->GetPayloadContainer();
        if (payload == null)
        {
            return false;
        }

        invType = (InventoryType)payload->Int1;
        slot = payload->Int2;
        return slot is >= 0 and <= 500;
    }

    // Opening a context menu on an empty slot yields no "Sort" entry, so prefer an occupied one.
    public static int PickContextMenuSlot(InventoryType type, int preferredSlot)
    {
        try
        {
            var inv = InventoryManager.Instance();
            if (inv == null)
            {
                return preferredSlot;
            }

            var c = inv->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded || c->Size <= 0)
            {
                return preferredSlot;
            }

            if (preferredSlot >= 0 && preferredSlot < c->Size)
            {
                var preferred = c->GetInventorySlot(preferredSlot);
                if (preferred != null && preferred->ItemId != 0)
                {
                    return preferredSlot;
                }
            }

            for (var i = 0; i < c->Size; i++)
            {
                var it = c->GetInventorySlot(i);
                if (it != null && it->ItemId != 0)
                {
                    return i;
                }
            }
        }
        catch
        {
        }

        return preferredSlot;
    }

    public static AtkDragDropInterface* TryGetDdiFromListIndex(AtkComponentList* list, int idx)
    {
        if (list == null || idx is < 0 or > 512)
        {
            return null;
        }

        try
        {
            var r = list->GetItemRenderer(idx);
            return r != null ? &r->AtkDragDropInterface : null;
        }
        catch
        {
            return null;
        }
    }

    public static AtkDragDropInterface* TryGetDdiFromComponent(AtkComponentBase* component)
    {
        if (component == null)
        {
            return null;
        }

        try
        {
            return component->GetComponentType() switch
            {
                ComponentType.DragDrop => &((AtkComponentDragDrop*)component)->AtkDragDropInterface,
                ComponentType.ListItemRenderer => &((AtkComponentListItemRenderer*)component)->AtkDragDropInterface,
                ComponentType.List => TryGetDdiFromListIndex((AtkComponentList*)component, 0),
                var _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static AtkDragDropInterface* TryGetDdiFromComponentOrHoveredList(AtkComponentBase* component)
    {
        if (component == null)
        {
            return null;
        }

        return component->GetComponentType() == ComponentType.List
            ? TryGetHoveredListDdi((AtkComponentList*)component)
            : TryGetDdiFromComponent(component);
    }

    private static AtkDragDropInterface* TryGetHoveredListDdi(AtkComponentList* list)
    {
        var hovered = TryGetDdiFromListIndex(list, list->HoveredItemIndex);
        if (hovered != null)
        {
            return hovered;
        }

        hovered = TryGetDdiFromListIndex(list, list->HoveredItemIndex2);
        return hovered != null ? hovered : TryGetDdiFromListIndex(list, list->HoveredItemIndex3);
    }
}
