# QuickTransfer - FFXIV Quick Transfer Plugin

A Dalamud plugin for Final Fantasy XIV that adds quick inventory actions via the game's existing context menus:

- **Shift + Right Click**: quick transfers (including vendor sell when shop is open)
- **Ctrl + Right Click**: armoury-mode transfers (when a special container is open)
- **Alt + Right Click**: split a stack in half

## Features

- **Quick Transfer**: Hold Shift and right-click an item to automatically trigger the matching context menu action
- **Vendor Quick Sell**: With a vendor shop open, Shift + Right Click auto-selects **Sell**. With **Auto-confirm vendor sell** enabled, quantity dialogs and "Are you certain?" confirmations are auto-filled and confirmed
- **Trade Window Support**: Shift + Right Click items from inventory into Trade window with auto-fill max quantity
- **Company Chest**: Shift + Right Click to deposit/withdraw when Free Company Chest is open; middle-click runs organize (stack + compact)
- **Armoury Mode**: Hold Ctrl and right-click to prioritize armoury actions while a special container is open
- **Split Half**: Hold Alt and right-click to split a stack and auto-fill half
- **Middle-Click Sort**: Middle-click an item to auto-select **Sort** (or organize in FC chest)
- **Cooldown Protection**: Built-in cooldown to prevent accidental double-moves
- **Debug Mode**: For troubleshooting and development (disabled by default)

## Installation

### Prerequisites

1. **XIVLauncher**: Download and install from [goatcorp.github.io](https://goatcorp.github.io/)
2. **Dalamud**: Enable plugins in XIVLauncher settings
3. **Dev Plugin Loading**: Enable "Dev Plugin Locations" in Dalamud settings for development builds
4. **.NET SDK**: Install the .NET 10 SDK (this project targets `net10.0-windows`)

### Installing the Plugin

#### Method 1: Custom Dalamud repository (recommended)
1. In-game, open **Dalamud Settings** → **Experimental**
2. Under **Custom Plugin Repositories**, add this URL:
   - `https://raw.githubusercontent.com/Knack117/QuickTransfer/main/pluginmaster.json`
3. Click **Save**
4. Type `/xlplugins` in-game, search for **QuickTransfer**, and click **Install**

#### Method 2: Development build (local)
1. Clone or download this repository
2. Open the solution in Visual Studio 2022
3. Build the solution (Release configuration)
4. In-game, open Dalamud Settings → Experimental → Dev Plugin Locations
5. Add the path to the compiled DLL (typically `bin/Release/QuickTransfer.dll` or `bin/Debug/QuickTransfer.dll`)
6. Type `/xlplugins` in-game and enable QuickTransfer

## Usage

### Quick Transfer (Shift + Right Click)

The plugin only clicks **existing** context menu options when they are available:

- **Inventory + Chocobo Saddlebags**
  - Inventory → **Add All to Saddlebag**
  - Saddlebags → **Remove All from Saddlebag**
- **Armoury Chest + Chocobo Saddlebags**
  - Armoury → **Add All to Saddlebag**
  - Saddlebags → **Remove All from Saddlebag**
- **Inventory + Armoury Chest**
  - (Gear) Inventory → **Place in Armoury Chest**
  - Armoury → **Return to Inventory**
- **Trade Window**
  - Inventory → **Trade** (auto-fills and confirms max quantity for stackable items)
- **Vendor Shop**
  - With a vendor shop open, Shift + Right Click → **Sell**. Enable **Auto-confirm vendor sell** to auto-fill quantity and click OK on "Are you certain?" dialogs.
- **Company Chest (Free Company Chest)**
  - Shift + Right Click Inventory/Armoury → deposit; Shift + Right Click Company Chest → **Remove** (withdraw)

If an option is not present for the clicked item, **nothing happens**.

### Armoury Mode (Ctrl + Right Click)

- While a **Saddlebag**, **Retainer**, or **Company Chest** is open, **Ctrl + Right Click** will prioritize:
  - Inventory gear → **Place in Armoury Chest**
  - Armoury gear → **Return to Inventory**

### Split Stack (Alt + Right Click)

- **Alt + Right Click** a **stackable** item to select the existing **Split** context menu action.
- If **Auto-confirm quantity prompts** is enabled, QuickTransfer will enter **half** and confirm automatically.

### Middle-Click Sort / Organize (MMB)

- For inventories that include a **Sort** entry in the item context menu, **middle-click an item** to auto-select **Sort** (without showing the menu).
- In the **Free Company Chest**, item context menus do not include Sort, so **middle-click** will run an **organize pass** (auto-stack + compact).

## Configuration Options

| Setting | Description | Default |
|---------|-------------|---------|
| Enabled | Enable/disable the plugin | True |
| Debug Mode | Log transfer attempts to chat | False |
| Transfer Cooldown | Milliseconds between transfers | 200 |
| Enable Middle-Click Sort | Enable MMB sort behavior | True |
| Enable Company Chest | Enable FC chest helpers | True |
| Company Chest: Middle-Click Organize | Enable MMB organize (stack+compact) in FC chest | True |
| Auto-confirm quantity prompts | Auto-fill and confirm InputNumeric prompts (Split / FC chest) | True |
| Enable Vendor Quick Sell | Shift+RClick auto-selects "Sell" when vendor is open | True |
| Auto-confirm vendor sell | Auto-fill quantity and click OK on sell dialogs ("How many?", "Are you certain?") | True |

## Development

### Setting Up Development Environment

1. Install Visual Studio 2022 with the .NET 10 SDK
2. Clone this repository
3. Open `QuickTransfer.csproj`
4. Build the project

### Building

```bash
# Build Debug
dotnet build --configuration Debug

# Build Release
dotnet build --configuration Release
```

Release build produces `bin/Release/QuickTransfer/latest.zip` for distribution.

### Testing

1. Enable "Dev Plugin Locations" in Dalamud settings
2. Add the path to your build output directory
3. In-game, the plugin will automatically reload when you rebuild

### Project Structure

```
QuickTransfer/
├── QuickTransfer.cs          # Main plugin class
├── QuickTransfer.csproj      # Project file
├── QuickTransferWindow.cs    # Configuration UI
├── ContextMenuHandler.cs     # Context menu matching and selection
├── InventoryHelpers.cs       # Inventory/addon detection
├── DragDropHelpers.cs        # Drag-drop parsing
├── AtkValueHelpers.cs        # AtkValue and addon utilities
├── pluginmaster.json         # Custom repository metadata (for Dalamud)
└── README.md                 # This file
```

### Adding New Features

1. Fork the repository
2. Create a feature branch
3. Implement your changes
4. Test thoroughly
5. Submit a pull request

## Troubleshooting

### Plugin Not Loading
- Ensure Dalamud is properly installed
- Check that you're using the correct .NET version
- Verify the DLL path is correct in Dev Plugin Locations

### Transfers Not Working
- Make sure the plugin is enabled
- Check that you have both source and target inventories open (or the correct container for the action)
- Ensure the target inventory has space
- Try increasing the transfer cooldown

### Game Crashes
- Disable debug mode for normal play
- Reduce the transfer cooldown if set too low
- Report bugs with detailed steps

### Debug Mode

Enable Debug Mode to see transfer attempts in chat:
```
[QuickTransfer] (Shift+RClick) Selected context action 'Remove All from Saddlebag' (idx=0) via OpenForItemSlot.
```

## Compatibility

- **Game Version**: Tested on FFXIV 7.0+ (Dawntrail)
- **Dalamud Version**: Uses `Dalamud.NET.Sdk` (targets your installed Dalamud)
- **.NET Version**: .NET 10.0 Windows (`net10.0-windows`)

## Contributing

Contributions are welcome! Please read the contributing guidelines before submitting pull requests.

### Reporting Issues

1. Check existing issues to avoid duplicates
2. Include steps to reproduce
3. Include plugin version and game version
4. Include any relevant logs

## License

This plugin is licensed under the MIT License - see the `LICENSE` file for details.

## Credits

- **goatcorp**: For creating XIVLauncher and Dalamud
- **Dalamud Community**: For the extensive plugin ecosystem
- **Contributors**: Thanks to everyone who has contributed to this project

## Changelog

### Version 1.0.5
- **New**: Vendor Quick Sell — Shift + Right Click at a vendor shop auto-selects **Sell**
- **New**: Auto-confirm vendor sell dialogs — auto-fill quantity ("How many to sell?") and click OK on "Are you certain you wish to sell it?" (unique/untradable items)
- README and configuration table updated for all current options

### Version 1.0.4
- **New**: Trade window support — Shift + Right Click items from inventory into Trade window
- **New**: Auto-fill and confirm max quantity when trading stackable items
- Trade window actions work independently of Company Chest settings

### Version 1.0.3
- Fix: inventory **Alt+RightClick Split** now reliably auto-fills **half** (including InventoryExpansion / localized prompts)
- Change: **Debug Mode is disabled by default** (and migrated off on update)

### Version 1.0.0
- Initial release
- Shift+Right-Click context menu automation for Inventory / Armoury / Saddlebags
