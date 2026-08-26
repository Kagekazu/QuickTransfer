using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
namespace QuickTransfer.Framework;

internal static unsafe class DragDropHelpers
{
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
                    if (r->DragDropComponent != null)
                    {
                        ddi = &r->DragDropComponent->AtkDragDropInterface;
                    }
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
        {
            return true;
        }

        static AtkDragDropInterface* TryGetHoveredListDdi(AtkComponentList* list)
        {
            if (list == null)
            {
                return null;
            }

            var hovered = TryGetDdiFromListIndex(list, list->HoveredItemIndex);
            if (hovered != null)
            {
                return hovered;
            }

            hovered = TryGetDdiFromListIndex(list, list->HoveredItemIndex2);
            return hovered != null ? hovered : TryGetDdiFromListIndex(list, list->HoveredItemIndex3);
        }

        static AtkDragDropInterface* TryGetDdiFromComponentOrHoveredList(AtkComponentBase* component)
        {
            if (component == null)
            {
                return null;
            }

            return component->GetComponentType() == ComponentType.List
                ? TryGetHoveredListDdi((AtkComponentList*)component)
                : TryGetDdiFromComponent(component);
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

        ddi = (isDragDropEvent && eventData != null) ? eventData->DragDropData.DragDropInterface : null;

        if (ddi == null && isDragDropEvent && eventData != null && eventData->DragDropData.ComponentNode != null)
        {
            try
            {
                ddi = TryGetDdiFromComponentOrHoveredList(eventData->DragDropData.ComponentNode->Component);
            }
            catch
            {
                // ignore
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
        return slot is not < 0 and not > 500;
    }

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
                var it0 = c->GetInventorySlot(preferredSlot);
                if (it0 != null && it0->ItemId != 0)
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
            {
                return false;
            }

            var inv = InventoryManager.Instance();
            if (inv == null)
            {
                return false;
            }

            List<int> candidates = [rawInt2, rawInt1, refIdx];
            foreach (var s in candidates.Distinct())
            {
                if (s is < 0 or > 500)
                {
                    continue;
                }

                foreach (var t in containers)
                {
                    var it = inv->GetInventorySlot(t, s);
                    if (it != null && it->ItemId != 0)
                    {
                        type = t;
                        slot = s;
                        return true;
                    }
                }
            }

            foreach (var t in containers)
            {
                var c = inv->GetInventoryContainer(t);
                if (c == null || !c->IsLoaded || c->Size <= 0)
                {
                    continue;
                }

                for (var i = 0; i < c->Size; i++)
                {
                    var it = c->GetInventorySlot(i);
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
        {
            return null;
        }
        if (idx is < 0 or > 512)
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

    public static AtkDragDropInterface* TryGetDdiFromComponent(AtkComponentBase* component, int preferredListIndex = 0)
    {
        if (component == null)
        {
            return null;
        }

        try
        {
            var t = component->GetComponentType();
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
