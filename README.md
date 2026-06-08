<div align="center">
<img src="https://raw.githubusercontent.com/Kagekazu/QuickTransfer/main/images/icon.png" alt="QuickTransfer" width="15%">

# QuickTransfer

Quick item transfer + split helpers for FFXIV inventory windows.

</div>

QuickTransfer clicks **existing** context menu options for you. If an option is not present for the clicked item, nothing happens.

## Modifiers

| Input                  | Action                                                                  |
|------------------------|-------------------------------------------------------------------------|
| **Shift + Right Click** | Default quick transfer (direction depends on what's open)              |
| **Ctrl + Right Click**  | Armoury actions when Saddlebag, Retainer, or FC Chest is open          |
| **Alt + Right Click**   | Split a stack in half (or remove half from FC Chest)                   |
| **Middle Click**        | Sort the container, or organize the active FC Chest tab                |

When multiple modifiers are held, priority is **Alt → Ctrl → Shift**. Brief taps still count — you don't need to hold through the menu.

## What Shift + Right Click does

| Open containers              | Inventory side                  | Other side                       |
|------------------------------|---------------------------------|----------------------------------|
| Inventory + Chocobo Saddlebag | Add All to Saddlebag           | Remove All from Saddlebag        |
| Armoury + Chocobo Saddlebag  | Add All to Saddlebag            | Remove All from Saddlebag        |
| Inventory + Armoury          | Place in Armoury Chest (gear)  | Return to Inventory              |
| Inventory + Retainer         | Entrust to Retainer            | Retrieve from Retainer           |
| Armoury + Retainer           | Entrust to Retainer            | Retrieve from Retainer           |
| Retainer + Chocobo Saddlebag | Add All to Saddlebag (retainer)| Entrust to Retainer (saddlebag)  |
| Trade window open            | Trade (auto-fills max qty)     | —                                |
| Vendor shop open             | Sell                            | —                                |
| FC Chest open                | Deposit to active tab          | Remove (withdraw)                |

## Ctrl + Right Click

While a **Saddlebag**, **Retainer**, or **FC Chest** is open, prioritizes:

- Inventory gear → **Place in Armoury Chest**
- Armoury gear → **Return to Inventory**

## Alt + Right Click

Selects the existing **Split** menu entry on a stackable item. With auto-confirm on, fills **half** and confirms automatically.

## Middle Click

- Normal containers: auto-selects **Sort** from the item menu (the menu doesn't pop).
- FC Chest: runs an **organize pass** on the active tab (auto-stack + compact). The FC Chest item menu has no Sort entry.

## Quantity dialogs

Trade and Split always auto-confirm. Vendor Sell and FC Chest quantity prompts auto-confirm when their respective toggles are on. Localized prompts are handled — the plugin recognises the dialog by context (open addon + expected max), not by English text.

## Settings

| Setting                              | Default | Description                                                              |
|--------------------------------------|---------|--------------------------------------------------------------------------|
| Plugin enabled                       | On      | Master switch                                                            |
| Transfer cooldown                    | 200 ms  | Minimum time between right-click actions                                 |
| Middle-click sort                    | On      | MMB sorts container / organizes FC Chest tab                             |
| FC Chest helpers                     | On      | Shift/Ctrl/Alt on Inventory ↔ FC Chest                                   |
| FC Chest middle-click organize       | On      | MMB runs stack + compact on the active FC Chest tab                      |
| FC Chest auto-confirm quantity       | On      | Auto-fills and confirms store / remove / split prompts                   |
| FC Chest compartments                | 3       | How many FC item tabs are unlocked (3–5)                                 |
| Vendor quick sell                    | On      | Shift + Right Click selects Sell at a vendor                             |
| Vendor auto-confirm sell             | On      | Auto-fills "How many?" and "Are you certain?" prompts                    |
| Debug mode                           | Off     | Logs detailed actions to chat (troubleshooting only)                     |

Command: `/qt` opens the settings window.
