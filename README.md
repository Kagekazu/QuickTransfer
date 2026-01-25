# QuickTransfer - FFXIV Quick Transfer Plugin

A Dalamud plugin for Final Fantasy XIV that enables quick item transfer between inventory containers using **Shift + Right-Click**, by automatically selecting an existing entry from the game's context menu.

## Features

- **Quick Transfer**: Hold Shift and right-click an item to automatically trigger the matching context menu action
- **Cooldown Protection**: Built-in cooldown to prevent accidental double-moves
- **Debug Mode**: For troubleshooting and development

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

If an option is not present for the clicked item, **nothing happens**.

## Configuration Options

| Setting | Description | Default |
|---------|-------------|---------|
| Enabled | Enable/disable the plugin | True |
| Debug Mode | Log transfer attempts to chat | False |
| Transfer Cooldown | Milliseconds between transfers | 200 |

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
├── pluginmaster.json         # Custom repository metadata (for Dalamud)
└── README.md                # This file
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
- Check that you have both source and target inventories open
- Ensure the target inventory has space
- Try increasing the transfer cooldown

### Game Crashes
- Disable debug mode for normal play
- Reduce the transfer cooldown if set too low
- Report bugs with detailed steps

### Debug Mode

Enable Debug Mode to see transfer attempts in chat:
```
[QuickTransfer] (Shift+RClick) Selected context action 'Remove All from Saddlebag' (idx=0) via deferred OnMenuOpened.
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

### Version 1.0.0
- Initial release
- Shift+Right-Click context menu automation for Inventory / Armoury / Saddlebags
