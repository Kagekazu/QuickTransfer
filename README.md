# QuickTransfer

Move items in FFXIV without digging through right-click menus. Settings: `/qt`.

## Install

1. `/xlsettings` → **Experimental** → add this repo (tick **Enabled**):

   ```
   https://puni.sh/api/repository/kage
   ```

2. `/xlplugins` → search **QuickTransfer** → install.

## Shortcuts

| | |
| --- | --- |
| **Shift** + right-click | Move the item where it belongs (saddlebag, retainer, vendor, trade, FC chest) |
| **Ctrl** + right-click | Armoury in / out (only with saddlebag, retainer, or FC chest open) |
| **Alt** + right-click | Split stack in half |
| **Middle-click** | Sort under cursor (FC chest: tidy current tab) |

A quick key-tap before the click is enough. Change anything in `/qt` → Keybindings.

**Try it:** open inventory + saddlebag → Shift + right-click an item.

## Worth knowing

- Quantity prompts are filled in for you.
- Only clicks options the game already offers — if nothing happens, the option isn’t there.
- Retainer market sell list: Shift + right-click is left alone so you can list items.
- Mouse 4 / 5 for sort are optional in Keybindings (off by default).
- Key clash with another plugin? Rebind in Keybindings.

## Help

[GitHub issues](https://github.com/Kagekazu/QuickTransfer/issues) — say what you did, what you expected, what happened. Debug mode in `/qt` → Advanced + `/xllog` helps.
