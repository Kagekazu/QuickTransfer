<div align="center">
<img src="https://raw.githubusercontent.com/Kagekazu/QuickTransfer/main/images/icon.png" alt="QuickTransfer" width="15%">

# QuickTransfer

Move items faster in FFXIV — without memorizing a dozen context menus.

</div>

QuickTransfer is a Dalamud plugin that **clicks the right context menu option for you**. Hold a modifier, right-click an item, and it picks the transfer, split, or sell action that makes sense for what's open. If that option isn't available, nothing happens — it won't force-move items on its own.

Open settings anytime with **`/qt`**.

---

## What you get

- **One shortcut for transfers** — saddlebags, armoury, retainer, trade, vendor sell, and FC chest deposits/withdrawals
- **Quick armoury swaps** — place gear in the armoury chest or pull it back while another container is open
- **Split stacks in one click** — halves a stack and confirms the quantity prompt for you
- **Sort with middle-click** — sorts a container, or organizes your FC chest tab (stack + compact)
- **Customizable keys** — rebind Shift/Ctrl/Alt or turn individual shortcuts off if they clash with other plugins

---

## Shortcuts (defaults)

Hold the modifier, **right-click** an item. Defaults shown below — change them in **Settings → Keybindings**.

| Shortcut | What it does |
|----------|--------------|
| **Shift + Right-click** | Quick transfer — direction depends on what's open (see table below) |
| **Ctrl + Right-click** | Armoury actions while Saddlebag, Retainer, or FC Chest is open |
| **Alt + Right-click** | Split a stack in half |
| **Middle-click** | Sort the container, or organize the active FC chest tab |

**Tips:**
- A quick tap on the modifier still counts — you don't need to hold it through the whole menu
- If two shortcuts share the same key, priority is **Split → Armoury → Quick transfer**
- Middle-click can use your side mouse buttons too (Mouse 4 / 5)

---

## Quick transfer (Shift + Right-click by default)

What happens depends on which windows you have open:

| You have open… | Click an item in… | Result |
|----------------|-------------------|--------|
| Inventory + Chocobo Saddlebag | Inventory | Add All to Saddlebag |
| Inventory + Chocobo Saddlebag | Saddlebag | Remove All from Saddlebag |
| Inventory + Armoury | Inventory (gear) | Place in Armoury Chest |
| Inventory + Armoury | Armoury | Return to Inventory |
| Inventory + Retainer | Inventory | Entrust to Retainer |
| Inventory + Retainer | Retainer | Retrieve from Retainer |
| Retainer + Chocobo Saddlebag | Retainer | Entrust to Retainer |
| Retainer + Chocobo Saddlebag | Saddlebag | Add All to Saddlebag |
| Trade window | Inventory | Trade (fills max quantity) |
| Vendor shop | Inventory | Sell |
| FC Chest | Inventory / Armoury / Crystals | Deposit to the active tab |
| FC Chest | The chest itself | Remove (withdraw) |

---

## Armoury shortcut (Ctrl + Right-click by default)

While a **Saddlebag**, **Retainer**, or **FC Chest** is open:

- Gear in your inventory → **Place in Armoury Chest**
- Gear in your armoury → **Return to Inventory**

---

## Split (Alt + Right-click by default)

Splits a stackable item in half. Quantity prompts are filled and confirmed automatically.

On the **FC Chest**, removes half of a stack instead.

---

## Middle-click sort & organize

- **Normal inventories** (inventory, saddlebag, retainer, etc.) — picks **Sort** without popping the menu
- **FC Chest** — runs an organize pass on the **active tab** (stacks items and compacts empty slots). The FC chest menu doesn't have a Sort option, so this is a separate helper

---

## Settings worth knowing

| Setting | What it does |
|---------|--------------|
| **Keybindings** | Change modifiers, turn shortcuts on/off, pick middle-click buttons |
| **FC Chest helpers** | Deposit/withdraw with quick transfer, middle-click organize, auto-confirm quantities |
| **Vendor quick sell** | Quick transfer selects Sell at vendors; optional auto-confirm for sell dialogs |
| **Transfer cooldown** | Minimum gap between actions — raise this if clicks feel too fast or get skipped |

Trade and Split always auto-confirm quantity dialogs. Vendor sell and FC chest prompts auto-confirm when their toggles are on. Works with non-English clients.

---

## Clashes with other plugins?

If Shift/Ctrl/Alt is already taken by AutoRetainer, Pandora's Box, MarketBuddy, or similar, open **`/qt` → Settings → Keybindings** and either:

1. **Rebind** QuickTransfer's modifiers to keys you don't use elsewhere, or
2. **Turn off** the shortcuts you don't need

You can keep FC chest organize on middle-click even if you disable the right-click transfers.
