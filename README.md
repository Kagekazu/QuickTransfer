# QuickTransfer

Move, sell, split and sort items in FFXIV without digging through right-click menus. Settings: `/qt`.

## Install

1. `/xlsettings` → **Experimental** → add this repo (tick **Enabled**):

   ```
   https://puni.sh/api/repository/kage
   ```

2. `/xlplugins` → search **QuickTransfer** → install.

## Shortcuts

| | |
| --- | --- |
| **Shift** + right-click | **Quick transfer** — moves the item where it belongs (see below) |
| **Ctrl** + right-click | **Armoury in / out** — while a saddlebag, retainer or FC chest is open |
| **Alt** + right-click | **Split stack in half** (on an FC chest item: takes half out) |
| **Middle-click** | **Sort under cursor** (FC chest: tidies the tab you're on) |

A quick tap of the key before clicking is enough. Change keys, mouse buttons or turn shortcuts off in `/qt` → **Shortcuts**.

**Where Quick transfer sends items** — the first match wins:

| Open | Result |
| --- | --- |
| Vendor | Sells the item |
| Trade window | Adds the item to the trade |
| FC chest | Deposits to the current tab, or takes the item out |
| Retainer + saddlebag | Inventory and retainer items to the saddlebag, saddlebag items to the retainer |
| Retainer | Between inventory and retainer |
| Saddlebag | Between inventory and saddlebag |
| Nothing else | Between inventory and Armoury Chest |

`/qt` shows a **Right now** line under each shortcut, so you can see what a click will do before you make it.

**Try it:** open your inventory and saddlebag, then Shift + right-click an item.

## Worth knowing

- "How many?" boxes can be filled in for you (`/qt` → **Fill in quantity prompts for me**). Trade amounts always are.
- QuickTransfer only picks options the game already offers. If nothing happens, the option isn't there for that item.
- While you list items on the retainer market board, Quick transfer stays out of the way.
- Mouse 4 / 5 for sorting are off by default, since they are often Back / Forward or push-to-talk.
- Key clash with another plugin? Pick another key in `/qt` → **Shortcuts**.

## Help

[GitHub issues](https://github.com/Kagekazu/QuickTransfer/issues): say what you did, what you expected and what happened. Turning on **Debug log** in `/qt` → **Advanced** and sharing `/xllog` helps a lot.
