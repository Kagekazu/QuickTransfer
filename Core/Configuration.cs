using Dalamud.Configuration;

namespace QuickTransfer;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public bool Enabled { get; set; } = true;
    // Default OFF (explicitly requested).
    public bool DebugMode { get; set; }
    public int TransferCooldownMs { get; set; } = 200;

    public bool EnableMiddleClickSort { get; set; } = true;
    public bool EnableCompanyChestMiddleClickOrganize { get; set; } = true;

    public bool EnableCompanyChest { get; set; } = true;
    public bool AutoConfirmCompanyChestQuantity { get; set; } = true;
    public int CompanyChestCompartments { get; set; } = 3; // 3..5 (default game starts at 3)

    public bool EnableVendorQuickSell { get; set; } = true;
    public bool AutoConfirmVendorSell { get; set; } = true;
    public int Version { get; set; } = 3;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
