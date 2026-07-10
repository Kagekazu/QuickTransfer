# QuickTransfer

Move items faster in FFXIV — hold a modifier, right-click an item, and
QuickTransfer picks the right context menu action for whatever you have open.
If that option is not there, nothing happens.

## Shortcuts

Defaults below. Change modifiers or turn actions off in **Settings → Keybindings**
(`/qt`).

- **Quick transfer** — modifier + right-click on an item. Sends gear and stacks
  between inventory, saddlebag, armoury, retainer, trade, vendors, and FC chest
  depending on which windows are open.
- **Armoury** — while saddlebag, retainer, or FC chest is open: place gear in the
  armoury chest from inventory, or return armoury gear to inventory.
- **Split** — modifier + right-click on a stack to split it in half. Quantity
  prompts are filled and confirmed for you.
- **Middle-click** — sort a container, or organize the active FC chest tab (stack
  and compact). Side mouse buttons work too.

Default modifiers: **Shift** (quick transfer), **Ctrl** (armoury), **Alt**
(split). A quick tap on the modifier still counts — you do not need to hold it
through the menu.

## What it handles

QuickTransfer only clicks options that already exist in the game menu — it does
not move items on its own.

Common quick-transfer cases:

- Inventory ↔ chocobo saddlebag (add all / remove all)
- Inventory ↔ armoury (place gear / return to inventory)
- Inventory ↔ retainer (entrust / retrieve)
- Trade window open → trade (fills max quantity)
- Vendor open → sell
- FC chest open → deposit from inventory, armoury, or crystals; withdraw from the
  chest itself

FC chest middle-click organize is separate from sort — the FC chest menu has no
Sort entry.

## Settings

- **Keybindings** — rebind Shift/Ctrl/Alt, disable individual shortcuts, choose
  middle-click buttons, adjust modifier latch.
- **FC Chest** — deposit/withdraw helpers, middle-click organize, auto-confirm
  quantity dialogs, compartment count.
- **Vendor quick sell** — quick transfer selects Sell; optional auto-confirm for
  sell dialogs.

If shortcuts clash with other plugins, rebind or turn off the actions you do
not need in Keybindings.

## Install

Add this custom plugin repository in Dalamud:

`https://puni.sh/api/repository/kage`

Then install **QuickTransfer** from the plugin installer.

## Getting started

1. Open QuickTransfer with `/qt`.
2. Check **Controls** for your current bindings, **Settings → Keybindings** to
   customize them.
3. Hold your quick-transfer modifier and right-click an item with the target
   container open (saddlebag, retainer, armoury, vendor, etc.).
