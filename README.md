# QuickTransfer

Move items around FFXIV without digging through right-click menus.

Open `/qt` anytime to change shortcuts or turn features off.

## What you get

- **Shift + right-click** moves an item where it belongs (saddlebag, retainer, vendor, trade, FC chest)
- **Ctrl + right-click** sends gear to the armoury chest (or back out)
- **Alt + right-click** splits a stack in half
- **Middle-click** sorts the window under your cursor
- Quantity prompts (“How many?”) are filled in and confirmed for you

QuickTransfer only clicks options the game already offers. If the option isn’t in the menu, nothing happens.

## Install

1. Open Dalamud settings with `/xlsettings` → **Experimental**.
2. Under **Custom Plugin Repositories**, add:

   ```
   https://puni.sh/api/repository/kage
   ```

   Tick **Enabled**, then save.
3. Open the plugin installer (`/xlplugins`), search for **QuickTransfer**, and install.

## Try it (30 seconds)

1. Open your inventory and chocobo saddlebag.
2. Hold **Shift** and right-click an item in your inventory.
3. It moves to the saddlebag. Do the same there to bring it back.

That’s the whole idea. The same Shift + right-click works with retainers, vendors, trade, and the FC chest.

## Shortcuts

| Shortcut | What it does |
| --- | --- |
| **Shift** + right-click | Quick transfer — moves the item to wherever makes sense (table below). |
| **Ctrl** + right-click | Armoury — stash gear in the armoury, or take it back out. Only while a saddlebag, retainer, or FC chest is open. |
| **Alt** + right-click | Split a stack in half. On an FC chest item, withdraws half. |
| **Middle-click** | Sort the window under your cursor. On the FC chest, tidies the current tab. |

A quick tap of the key right before the click is enough — you don’t have to hold it.

Change or disable any of these in **`/qt` → Settings → Keybindings**. Mouse 4 / Mouse 5 for sort are optional there (off by default, so they don’t steal Back / Forward).

## Where Shift + right-click sends things

| You have open… | What happens |
| --- | --- |
| Saddlebag | Moves between inventory and saddlebag |
| Retainer | Entrusts to the retainer, or retrieves |
| Retainer + saddlebag | Inventory → saddlebag; saddlebag → retainer |
| Trade window | Offers the item (full stack filled in) |
| Vendor | Sells the item |
| FC chest | Stores in the open tab, or withdraws from the chest |

### FC chest tips

- **Storing** uses the tab you currently have open. Crystals go to the crystal tab.
- **Middle-click** on the chest merges stacks, closes gaps, then sorts. This takes a few seconds — let it finish.
- If another FC member is using the chest, QuickTransfer stops instead of spamming errors. Try again shortly.
- Set **Unlocked item tabs** in settings to match your FC (3–5).

## Settings worth knowing

Type `/qt`. The **Controls** tab shows your current shortcuts at a glance.

| Section | Useful for |
| --- | --- |
| **Keybindings** | Change keys, turn shortcuts off, pick which mouse buttons sort |
| **Free Company Chest** | Helpers on/off, auto-confirm quantities, empty slots vs topping up stacks |
| **Vendor quick sell** | Selling on/off, auto-confirm sell dialog |
| **Advanced** | Delay between actions, debug mode for bug reports |

## Good to know

- **Market board listing:** while your retainer’s sell list is open, Shift + right-click is left alone so you can list items normally (toggle in Keybindings).
- **Key conflicts with other plugins?** Rebind or disable the shortcut in Keybindings.
- **AetherBags** saddlebag and retainer windows are supported.
- **Nothing happens?** That option probably isn’t on the item’s menu (e.g. untradable items). Right-click normally to check.

## Reporting a problem

Open an issue on [GitHub](https://github.com/Kagekazu/QuickTransfer/issues) with:

1. What you did  
2. What you expected  
3. What happened instead  

Turning on **Debug mode** in `/qt → Settings → Advanced` and attaching the log from `/xllog` helps a lot.
