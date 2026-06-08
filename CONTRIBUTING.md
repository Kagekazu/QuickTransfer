# Contributing to QuickTransfer

Thank you for your interest in contributing!

## Getting started

1. Fork the repository and clone your fork.
2. Install the .NET 10 SDK and a local Dalamud dev environment (XIVLauncher with dev plugin loading enabled).
3. Open **`QuickTransfer.slnx`** in Rider or Visual Studio, then build with **Debug | x64** or **Release | x64**.
4. Point Dev Plugin Locations at `bin\x64\Debug\QuickTransfer.dll` (or Release).

## Pull requests

- Keep changes focused — one feature or fix per PR when possible.
- Match existing code style (nullable enabled, `unsafe` only where needed, minimal comments).
- Test affected flows in-game before submitting (inventory, saddlebags, retainer, FC chest, vendor, trade).
- Update README.md if you add or change user-facing behavior.

## Reporting issues

1. Check existing [GitHub issues](https://github.com/Kagekazu/QuickTransfer/issues) to avoid duplicates.
2. Include steps to reproduce, plugin version, game patch, and relevant debug logs (enable Debug Mode in settings).

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
