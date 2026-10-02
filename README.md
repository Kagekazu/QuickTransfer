# QuickTransfer

Move items around FFXIV without digging through right-click menus.

Hold **Shift** and right-click an item, and QuickTransfer picks the menu option
you were going to pick anyway: put it in the saddlebag, hand it to your
retainer, sell it, trade it, or store it in the FC chest - depending on which
windows you have open.

QuickTransfer only ever clicks options the game already offers you. If the
option isn't in the menu, nothing happens.

## Install

1. In game, open the Dalamud settings with `/xlsettings` and go to
   **Experimental**.
2. Under **Custom Plugin Repositories**, add:

   ```
   https://puni.sh/api/repository/kage
   ```

   Make sure its **Enabled** box is ticked.
3. Save, then open the plugin installer (`/xlplugins`), search for
   **QuickTransfer**, and install it.

## Try it

1. Open your inventory and your chocobo saddlebag.
2. Hold **Shift** and right-click an item in your inventory.
3. It moves to the saddlebag. Shift + right-click it there to bring it back.

That's the whole idea. The same Shift + right-click works with retainers,
vendors, the trade window and the FC chest.

## Shortcuts

| Shortcut | What it does |
| --- | --- |
| **Shift** + right-click | Quick transfer - moves the item to wherever makes sense (see below). |
| **Ctrl** + right-click | Armoury chest - puts gear into the armoury chest, or takes it back out to your inventory. Only active while a saddlebag, retainer or FC chest is open. |
| **Alt** + right-click | Split a stack in half. On an FC chest item, withdraws half the stack. |
| **Middle-click** | Sorts the window under your cursor. In the FC chest, tidies up the current tab. Mouse 4 and Mouse 5 work too. |

You don't have to keep holding the key while the menu opens - a quick tap
right before the click is enough.

All of these can be changed or turned off in **`/qt` → Settings → Keybindings**.

## Where Shift + right-click sends things

| You have open… | Shift + right-click does |
| --- | --- |
| Saddlebag | Moves items between inventory and saddlebag |
| Retainer | Entrusts items to the retainer, or retrieves them |
| Retainer and saddlebag | Inventory items go to the saddlebag; saddlebag items go to the retainer |
| Trade window | Offers the item in the trade, with the full stack filled in |
| A vendor | Sells the item |
| FC chest | Stores the item in the tab you have selected, or withdraws it from the chest |

Quantity prompts ("How many?") are filled in and confirmed for you.

### FC chest

- **Storing** puts the item in the tab you currently have open. Crystals go into
  the crystal tab.
- **Middle-click** on the chest tidies the open tab: it merges partial stacks,
  closes up gaps, and sorts items by type. It takes a few seconds because the
  chest only accepts one move at a time - let it finish.
- If someone else in your FC is using the chest, QuickTransfer backs off and
  stops instead of spamming errors. Just try again in a moment.
- Set **Unlocked item tabs** in settings to match how many tabs your FC has
  (3 to 5).

## Settings

Type `/qt` to open settings. The **Controls** tab shows your current shortcuts
at a glance.

- **Keybindings** - change which key does what, turn individual shortcuts off,
  and pick which mouse buttons count as middle-click.
- **Free Company Chest** - turn the FC chest helpers on or off, auto-confirm
  quantity prompts, and choose whether storing prefers empty slots or topping
  up existing stacks.
- **Vendor quick sell** - turn selling on or off, and whether the sell
  confirmation is accepted automatically.
- **Advanced** - the delay between actions and a debug mode for bug reports.

## Good to know

- **Selling on the market board:** while your retainer's sell list is open,
  Shift + right-click is left alone so you can list items normally. You can turn
  this off in Keybindings.
- **Other plugins using the same keys?** Change the key or turn the shortcut off
  in Keybindings.
- **AetherBags** saddlebag and retainer windows are supported.
- **Nothing happens?** The option probably isn't in that item's menu (for
  example, untradable items can't be traded). Right-click the item normally to
  check.

## Reporting a problem

Open an issue on
[GitHub](https://github.com/Kagekazu/QuickTransfer/issues) with what you did,
what you expected, and what happened instead. Turning on **Debug mode** in
`/qt → Settings → Advanced` and attaching the log from `/xllog` helps a lot.
