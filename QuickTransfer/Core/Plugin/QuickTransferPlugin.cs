using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.EzHookManager;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Runtime.InteropServices;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;
using AutoContextAction = QuickTransfer.Framework.ContextMenuHandler.AutoContextAction;
using ModifierMode = QuickTransfer.Framework.ContextMenuHandler.ModifierMode;

namespace QuickTransfer;

public sealed unsafe partial class QuickTransferPlugin : IAsyncDalamudPlugin
{
    private readonly Dictionary<int, Dictionary<int, InventoryType>> companyChestSelectedTabCandidates = [];
    private readonly Dictionary<uint, (InventoryType Type, int Slot, int A4)> lastGoodContextTargetByAddonId = [];
    private readonly Dictionary<(uint OwnerAddonId, uint InventoryType), int> observedContextA4 = [];
    private readonly IDalamudPluginInterface pluginInterface;
    private EzHook<AgentInventoryContext.Delegates.OpenForItemSlot>? openForItemSlotHook;
    private PluginUI pluginUi = null!;
    private bool ecommonsInitialized;

    private readonly WindowSystem windowSystem = new("QuickTransfer");
    private int companyChestBusyHits;
    private long companyChestBusyUntilMs;

    private CompanyChestDepositState companyChestDeposit;

    private CompanyChestOrganizeState companyChestOrganize;
    private int companyChestSelectedTabAtkValueIndex = -1;
    private bool debugPrintedReceiveEventHook;

    private long lastActionTickMs;
    private long lastAltSeenMs;
    private long lastCompanyChestOrganizeSkipLogMs;
    private string lastCompanyChestOrganizeSkipReason = string.Empty;
    private long lastCtrlSeenMs;
    private long lastCursorHitTestLogMs;
    private long lastFcChestTabUnmappedLogMs;
    private (string AddonName, uint AddonId, long SeenAtMs)? lastHoverAddon;
    private string lastHoverAddonName = string.Empty;
    private (InventoryType Page, uint AddonId, long SeenAtMs)? lastHoverCompanyChestPage;
    private (nint DdiPtr, uint AddonId, long SeenAtMs)? lastHoverDdi;
    private long lastMiddleClickSortMs;
    private long lastObservedA4LogMs;
    private long lastReceiveEventDebugLogMs;
    private (InventoryType Page, uint AddonId, long SeenAtMs)? lastSelectedCompanyChestPage;

    private long lastShiftSeenMs;
    private bool lastVkMButtonDown;
    private bool lastVkX1ButtonDown;
    private bool lastVkX2ButtonDown;
    private long pendingCloseContextMenuAtMs;

    private long pendingCompanyChestNumericConfirmUntilMs;
    private uint pendingCompanyChestNumericDesired;
    private bool pendingCompanyChestNumericHalf;
    private bool pendingCompanyChestNumericValueSet;
    private long pendingCompanyChestNumericValueSetAtMs;
    private (string AddonName, long EnqueuedAtMs, ModifierMode Mode)? pendingDeferredDefaultMenu;
    private (nint AgentPtr, nint AddonPtr, long EnqueuedAtMs, ModifierMode Mode)? pendingDeferredMenuClick;
    private (nint AgentPtr, nint AddonPtr, long EnqueuedAtMs)? pendingDeferredSortMenuClick;
    private (InventoryType Type, int Slot, uint AddonId, long EnqueuedAtMs)? pendingMiddleClickSortRequest;
    private long pendingMiddleClickSortUntilMs;
    private nint pendingMoveAtkValuesPtr;
    private long pendingMoveCreatedAtMs;
    private long pendingMoveOutValueFreeAtMs;
    private nint pendingMoveOutValuePtr;
    private bool pendingMoveSawInputNumeric;
    private PendingNumericKind pendingNumericKind;
    private uint pendingSplitExpectedMax;
    private long pendingSplitExpectedUntilMs;
    private long suppressContextMenuUntilMs;
    private long suppressInputNumericUntilMs;

    public QuickTransferPlugin(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public Configuration Configuration { get; private set; } = null!;

    public Task LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ECommonsMain.Init(pluginInterface, this);
        ecommonsInitialized = true;

        Configuration = Svc.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(Svc.PluginInterface);

        try
        {
            if (Configuration.Version < 3)
            {
                Configuration.DebugMode = false;
                Configuration.Version = 3;
                Configuration.Save();
            }
            else if (Configuration.Version < 4)
            {
                Configuration.Version = 4;
                Configuration.Save();
            }
            else if (Configuration.Version < 5)
            {
                Configuration.Version = 5;
                Configuration.Save();
            }
        }
        catch
        {
            // ignore
        }

        Configuration.SanitizeKeybindings();

        pluginUi = new(Configuration);
        windowSystem.AddWindow(pluginUi);

        Svc.Commands.AddHandler(QuickTransferConstants.CommandName, new(OnCommand)
        {
            HelpMessage = "Open QuickTransfer settings"
        });

        Svc.PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        Svc.PluginInterface.UiBuilder.OpenMainUi += OpenConfigUi;

        try
        {
            var funcPtr = AgentInventoryContext.MemberFunctionPointers.OpenForItemSlot;
            if (funcPtr != null)
            {
                openForItemSlotHook = new(
                    (nint)funcPtr,
                    OpenForItemSlotDetour);
            }
            else
            {
                Svc.Log.Warning("[QuickTransfer] AgentInventoryContext.MemberFunctionPointers.OpenForItemSlot is null - signature may not be resolved");
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[QuickTransfer] Failed to hook OpenForItemSlot using ClientStructs delegate - falling back to manual signature");
            try
            {
                openForItemSlotHook = new("83 B9 ?? ?? ?? ?? ?? 7E ?? 39 91", OpenForItemSlotDetour);
            }
            catch (Exception ex2)
            {
                Svc.Log.Error(ex2, "[QuickTransfer] Failed to hook OpenForItemSlot with fallback signature");
            }
        }

        Svc.ContextMenu.OnMenuOpened += OnContextMenuOpened;
        Svc.Framework.Update += OnFrameworkUpdate;

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, QuickTransferConstants.InputNumericAddonName, OnInputNumericPreSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, QuickTransferConstants.ContextMenuAddonName, OnAddonPreDraw);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, QuickTransferConstants.InputNumericAddonName, OnAddonPreDraw);
        foreach (var name in QuickTransferConstants.ReceiveEventAddonNames)
        {
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, name, OnAddonReceiveEvent);
        }

        Svc.Chat.ChatMessage += OnChatMessage;

        Svc.Log.Information($"Loaded {Svc.PluginInterface.Manifest.Name}.");
        Svc.Log.Information(
            $"[QuickTransfer] DebugMode={Configuration.DebugMode}, Enabled={Configuration.Enabled}, " +
            $"EnableMiddleClickSort={Configuration.EnableMiddleClickSort}, " +
            $"EnableCompanyChest={Configuration.EnableCompanyChest}, " +
            $"EnableCompanyChestMiddleClickOrganize={Configuration.EnableCompanyChestMiddleClickOrganize}");
        if (Configuration.DebugMode)
        {
            try
            {
                var matches = Enum.GetNames<InventoryType>()
                    .Where(n => n.Contains("FreeCompany", StringComparison.OrdinalIgnoreCase) ||
                                n.Contains("Company", StringComparison.OrdinalIgnoreCase) ||
                                n.Contains("Chest", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Svc.Log.Information($"[QuickTransfer] InventoryType names containing Company/Chest: {string.Join(", ", matches)}");
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, "[QuickTransfer] Failed to enumerate InventoryType names (debug).");
            }
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (!ecommonsInitialized)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            Configuration?.PersistIfDirty();
        }
        catch
        {
            // ignore
        }

        try
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
            Svc.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
            Svc.Chat.ChatMessage -= OnChatMessage;
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreSetup, QuickTransferConstants.InputNumericAddonName, OnInputNumericPreSetup);
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, QuickTransferConstants.ContextMenuAddonName, OnAddonPreDraw);
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, QuickTransferConstants.InputNumericAddonName, OnAddonPreDraw);
            foreach (var name in QuickTransferConstants.ReceiveEventAddonNames)
            {
                Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, name, OnAddonReceiveEvent);
            }

            Svc.PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
            Svc.PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
            Svc.PluginInterface.UiBuilder.OpenMainUi -= OpenConfigUi;

            windowSystem.RemoveAllWindows();
            Svc.Commands.RemoveHandler(QuickTransferConstants.CommandName);
        }
        catch (Exception ex)
        {
            try
            {
                Svc.Log.Warning(ex, "[QuickTransfer] Error while unregistering during dispose.");
            }
            catch
            {
                // ignore
            }
        }

        ECommonsMain.Dispose();
        return ValueTask.CompletedTask;
    }

    private void ArmSuppressContextMenu(long now, int durationMs = 250)
        => suppressContextMenuUntilMs = Math.Max(suppressContextMenuUntilMs, now + durationMs);

    private void ArmSuppressInputNumeric(long now, int durationMs = 1500)
        => suppressInputNumericUntilMs = Math.Max(suppressInputNumericUntilMs, now + durationMs);

    private void ArmPendingNumeric(long now, PendingNumericKind kind, int windowMs, bool half = false, int suppressMs = 0)
    {
        pendingCompanyChestNumericConfirmUntilMs = windowMs > 0 ? now + windowMs : 0;
        pendingNumericKind = kind;
        pendingCompanyChestNumericValueSet = false;
        pendingCompanyChestNumericValueSetAtMs = 0;
        pendingCompanyChestNumericDesired = 0;
        pendingCompanyChestNumericHalf = half;
        if (suppressMs > 0)
        {
            ArmSuppressInputNumeric(now, suppressMs);
        }
    }

    private void ClearPendingNumeric(bool restoreInputNumericAlpha = false)
    {
        pendingCompanyChestNumericConfirmUntilMs = 0;
        pendingNumericKind = PendingNumericKind.None;
        pendingCompanyChestNumericValueSet = false;
        pendingCompanyChestNumericValueSetAtMs = 0;
        pendingCompanyChestNumericDesired = 0;
        pendingCompanyChestNumericHalf = false;
        pendingSplitExpectedMax = 0;
        pendingSplitExpectedUntilMs = 0;
        if (restoreInputNumericAlpha)
        {
            suppressInputNumericUntilMs = 0;
        }
    }

    private void LogInputNumericMismatch(AtkUnitBase* inputNumeric, string verb)
    {
        if (!Configuration.DebugMode)
        {
            return;
        }

        try
        {
            var promptVal = inputNumeric->AtkValues + 6;
            var prompt = promptVal->Type is AtkValueType.String or AtkValueType.ManagedString ? AtkValueHelpers.ReadAtkValueString(*promptVal) : string.Empty;
            var minVal = inputNumeric->AtkValues + 2;
            var maxVal = inputNumeric->AtkValues + 3;
            var min = minVal->Type == AtkValueType.UInt ? minVal->UInt : 0U;
            var max = maxVal->Type == AtkValueType.UInt ? maxVal->UInt : 0U;
            Svc.Log.Information($"[QuickTransfer] Auto-confirm InputNumeric {verb} (kind={pendingNumericKind}, prompt='{prompt}', min={min}, max={max}, expectedSplitMax={pendingSplitExpectedMax}).");
        }
        catch
        {
            Svc.Log.Information($"[QuickTransfer] Auto-confirm InputNumeric {verb} (kind={pendingNumericKind}).");
        }
    }

    private void OnCommand(string command, string args) => OpenConfigUi();

    private void OpenConfigUi() => pluginUi.IsOpen = true;

    private void OpenForItemSlotDetour(
        AgentInventoryContext* agent,
        InventoryType inventoryType,
        int slot,
        int a4,
        uint addonId)
    {
        openForItemSlotHook?.Original(agent, inventoryType, slot, a4, addonId);

        if (!Configuration.Enabled)
        {
            return;
        }

        try
        {
            observedContextA4[(addonId, (uint)inventoryType)] = a4;

            if (agent != null && agent->ContextItemCount > 0)
            {
                lastGoodContextTargetByAddonId[addonId] = (inventoryType, slot, a4);
            }

            if (Configuration.DebugMode && Environment.TickCount64 - lastObservedA4LogMs >= 1000)
            {
                lastObservedA4LogMs = Environment.TickCount64;
                Svc.Log.Information($"[QuickTransfer] Observed OpenForItemSlot: type={inventoryType} slot={slot} a4={a4} addonId={addonId} ctxCount={(agent != null ? agent->ContextItemCount : -1)}");
            }
        }
        catch
        {
            // ignore
        }

        var mode = GetModifierModeLatched(Environment.TickCount64);

        if (mode == null)
        {
            return;
        }

        if (InventoryHelpers.ShouldYieldQuickTransferForRetainerMarket(Configuration, mode.Value, inventoryType))
        {
            if (Configuration.DebugMode)
            {
                Svc.Log.Information("[QuickTransfer] Yielding quick transfer — retainer sell list open.");
            }

            return;
        }

        var saddlebagOpen = InventoryHelpers.IsSaddlebagOpen();
        var retainerOpen = InventoryHelpers.IsRetainerOpen();
        var companyChestOpen = InventoryHelpers.IsCompanyChestOpen();
        var specialOpen = saddlebagOpen || retainerOpen || companyChestOpen;

        if (mode == ModifierMode.Ctrl &&
            (!specialOpen ||
             InventoryHelpers.IsSaddlebagType(inventoryType) ||
             InventoryHelpers.IsRetainerType(inventoryType) ||
             InventoryHelpers.IsCompanyChestType(inventoryType)))
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - lastActionTickMs < Configuration.TransferCooldownMs)
        {
            return;
        }

        if (mode == ModifierMode.Alt)
        {
            return;
        }

        if (mode == ModifierMode.Shift && companyChestOpen && Configuration.EnableCompanyChest)
        {
            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out var _))
            {
                return;
            }

            if (InventoryHelpers.IsCompanyChestDepositSourceType(inventoryType) && StartCompanyChestDeposit(inventoryType, (uint)slot))
            {
                lastActionTickMs = now;
                ContextMenuHandler.TryCloseCurrentContextMenu(agent);
                return;
            }
        }

        if (ContextMenuHandler.TryAutoSelectFromAgent(agent, mode.Value, Configuration, out var chosenText, out var chosenIndex, ref pendingCloseContextMenuAtMs))
        {
            lastActionTickMs = now;
            if (Configuration.DebugMode)
            {
                Svc.Log.Information($"[QuickTransfer] ({mode} + RClick) Selected context action '{chosenText}' (idx={chosenIndex}) via OpenForItemSlot.");
            }

            if (mode == ModifierMode.Shift &&
                chosenText.Length > 0 &&
                ContextMenuHandler.ContextLabelMatches(AutoContextAction.Trade, chosenText) &&
                InventoryHelpers.IsTradeOpen())
            {
                ArmPendingNumeric(now, PendingNumericKind.Trade, 1500, suppressMs: 1500);
            }
            if (Configuration.AutoConfirmVendorSell &&
                mode == ModifierMode.Shift &&
                chosenText.Length > 0 &&
                ContextMenuHandler.ContextLabelMatches(AutoContextAction.Sell, chosenText) &&
                InventoryHelpers.IsVendorOpen())
            {
                ArmPendingNumeric(now, PendingNumericKind.Sell, 1500, suppressMs: 1500);
            }
        }
        else if (Configuration.DebugMode && mode == ModifierMode.Ctrl)
        {
            Svc.Log.Information("[QuickTransfer] (Ctrl + RClick) No matching armoury action found in context menu.");
            ContextMenuHandler.DebugDumpContextMenu(agent, 24);
        }
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (!Configuration.Enabled)
        {
            return;
        }

        var now = Environment.TickCount64;
        var middleSortActive = pendingMiddleClickSortUntilMs > 0 && now <= pendingMiddleClickSortUntilMs;
        var mode = middleSortActive ? null : GetModifierModeLatched(now);

        if (!middleSortActive && mode == null)
        {
            return;
        }

        var saddlebagOpen = InventoryHelpers.IsSaddlebagOpen();
        var retainerOpen = InventoryHelpers.IsRetainerOpen();
        var companyChestOpen = InventoryHelpers.IsCompanyChestOpen();
        var specialOpen = saddlebagOpen || retainerOpen || companyChestOpen;

        if (!middleSortActive && mode == ModifierMode.Ctrl && !specialOpen)
        {
            return;
        }

        if (Configuration.DebugMode)
        {
            Svc.Log.Information($"[QuickTransfer] OnMenuOpened: AddonName='{args.AddonName}', MenuType={args.MenuType}, AgentPtr=0x{args.AgentPtr.ToInt64():X}, AddonPtr=0x{args.AddonPtr.ToInt64():X}");
        }

        if (middleSortActive && args.MenuType == ContextMenuType.Inventory)
        {
            if (args.AgentPtr != nint.Zero && args.AddonPtr != nint.Zero)
            {
                pendingDeferredSortMenuClick = (args.AgentPtr, args.AddonPtr, now);
                return;
            }
        }

        if (args.MenuType == ContextMenuType.Default &&
            (mode == ModifierMode.Shift || mode == ModifierMode.Alt) &&
            Configuration.EnableCompanyChest &&
            string.Equals(args.AddonName, QuickTransferConstants.FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase))
        {
            pendingDeferredDefaultMenu = (args.AddonName ?? string.Empty, now, mode.Value);
            return;
        }

        if (args.MenuType != ContextMenuType.Inventory)
        {
            return;
        }

        if (args.AgentPtr == nint.Zero || args.AddonPtr == nint.Zero)
        {
            return;
        }

        if (mode == null)
        {
            return;
        }

        try
        {
            var agent = (AgentInventoryContext*)args.AgentPtr;
            if (InventoryHelpers.ShouldYieldQuickTransferForRetainerMarket(
                    Configuration,
                    mode.Value,
                    agent->TargetInventoryId))
            {
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information("[QuickTransfer] Yielding deferred quick transfer — retainer sell list open.");
                }

                return;
            }
        }
        catch
        {
            // ignore
        }

        pendingDeferredMenuClick = (args.AgentPtr, args.AddonPtr, Environment.TickCount64, mode.Value);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!Configuration.Enabled)
        {
            return;
        }

        var now = Environment.TickCount64;

        var mDown = ModifierBindings.IsMouseButtonDown(0x04);
        var x1Down = ModifierBindings.IsMouseButtonDown(0x05);
        var x2Down = ModifierBindings.IsMouseButtonDown(0x06);

        var prevM = lastVkMButtonDown;
        var prevX1 = lastVkX1ButtonDown;
        var prevX2 = lastVkX2ButtonDown;

        if (Configuration.DebugMode && (mDown != prevM || x1Down != prevX1 || x2Down != prevX2))
        {
            Svc.Log.Information($"[QuickTransfer] Win32 mouse state: M={(mDown ? 1 : 0)} X1={(x1Down ? 1 : 0)} X2={(x2Down ? 1 : 0)}");
        }

        lastVkMButtonDown = mDown;
        lastVkX1ButtonDown = x1Down;
        lastVkX2ButtonDown = x2Down;

        var middleEdge = ModifierBindings.IsMiddleClickEdge(mDown, prevM, x1Down, prevX1, x2Down, prevX2, Configuration);
        if (middleEdge)
        {
            if (ModifierBindings.IsMiddleClickConfigured(Configuration))
            {
                TryQueueMiddleClickSortFromHover(now);
            }
            else if (Configuration.DebugMode)
            {
                Svc.Log.Information("[QuickTransfer] (MMB) Press detected, but middle-click sort is disabled or no mouse buttons are selected.");
            }
        }

        if (Svc.KeyState[VirtualKey.SHIFT])
        {
            lastShiftSeenMs = now;
        }
        if (Svc.KeyState[VirtualKey.CONTROL])
        {
            lastCtrlSeenMs = now;
        }
        if (Svc.KeyState[VirtualKey.MENU])
        {
            lastAltSeenMs = now;
        }

        var shouldAutoConfirm = pendingNumericKind == PendingNumericKind.Trade ||
                                pendingNumericKind == PendingNumericKind.Split ||
                                Configuration.AutoConfirmVendorSell && pendingNumericKind == PendingNumericKind.Sell ||
                                Configuration.AutoConfirmCompanyChestQuantity && pendingNumericKind != PendingNumericKind.None;

        if (shouldAutoConfirm &&
            pendingNumericKind != PendingNumericKind.None &&
            pendingCompanyChestNumericConfirmUntilMs > 0 &&
            now <= pendingCompanyChestNumericConfirmUntilMs)
        {
            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out var inputNumeric))
            {
                ArmSuppressInputNumeric(now);
                if (!pendingCompanyChestNumericValueSet)
                {
                    if (!TrySetInputNumericToMax(inputNumeric, pendingNumericKind))
                    {
                        LogInputNumericMismatch(inputNumeric, "skipped");
                        ClearPendingNumeric(restoreInputNumericAlpha: true);
                    }
                    else
                    {
                        pendingCompanyChestNumericValueSet = true;
                        pendingCompanyChestNumericValueSetAtMs = now;
                    }
                    return;
                }

                if (now - pendingCompanyChestNumericValueSetAtMs < 50)
                {
                    return;
                }

                if (!TrySetInputNumericToMax(inputNumeric, pendingNumericKind))
                {
                    LogInputNumericMismatch(inputNumeric, "aborted");
                    ClearPendingNumeric();
                    return;
                }

                try
                {
                    var toConfirm = pendingCompanyChestNumericDesired;
                    if (toConfirm == 0)
                    {
                        try
                        {
                            var maxVal = inputNumeric->AtkValues + 3;
                            if (maxVal->Type == AtkValueType.UInt)
                            {
                                toConfirm = maxVal->UInt;
                            }
                            else if (maxVal->Type == AtkValueType.Int)
                            {
                                toConfirm = (uint)Math.Max(0, maxVal->Int);
                            }
                        }
                        catch
                        {
                            // ignore
                        }
                        if (toConfirm == 0)
                        {
                            toConfirm = 1;
                        }
                    }
                    inputNumeric->FireCallbackInt((int)toConfirm);
                    if (Configuration.DebugMode)
                    {
                        Svc.Log.Information($"[QuickTransfer] Auto-confirmed InputNumeric (kind={pendingNumericKind}, FireCallbackInt={toConfirm}).");
                    }
                    ClearPendingNumeric(restoreInputNumericAlpha: true);
                }
                catch (Exception ex)
                {
                    ClearPendingNumeric(restoreInputNumericAlpha: true);
                    Svc.Log.Warning(ex, "[QuickTransfer] Failed to auto-confirm InputNumeric.");
                }
            }
            else if (Configuration.AutoConfirmVendorSell && pendingNumericKind == PendingNumericKind.Sell && InventoryHelpers.IsVendorOpen() &&
                     GenericHelpers.TryGetAddonMaster(QuickTransferConstants.SelectYesnoAddonName, out AddonMaster.SelectYesno selectYesno))
            {
                try
                {
                    selectYesno.Yes();
                    if (Configuration.DebugMode)
                    {
                        Svc.Log.Information("[QuickTransfer] Auto-confirmed vendor sell Yes/No dialog (SelectYesno).");
                    }
                }
                catch (Exception ex)
                {
                    if (Configuration.DebugMode)
                    {
                        Svc.Log.Warning(ex, "[QuickTransfer] Failed to auto-confirm SelectYesno.");
                    }
                }
                ClearPendingNumeric();
            }
        }
        else if (pendingCompanyChestNumericConfirmUntilMs > 0 && now > pendingCompanyChestNumericConfirmUntilMs)
        {
            ClearPendingNumeric(restoreInputNumericAlpha: true);
        }

        if (pendingMoveOutValuePtr != 0 || pendingMoveAtkValuesPtr != 0)
        {
            var inputVisible = InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out var _);
            if (inputVisible)
            {
                pendingMoveSawInputNumeric = true;
            }

            var graceExpired = pendingMoveCreatedAtMs > 0 && now - pendingMoveCreatedAtMs >= 1500;
            if (pendingMoveSawInputNumeric && !inputVisible || now >= pendingMoveOutValueFreeAtMs || !inputVisible && graceExpired)
            {
                try
                {
                    if (pendingMoveOutValuePtr != 0)
                    {
                        Marshal.FreeHGlobal(pendingMoveOutValuePtr);
                    }
                }
                catch
                {
                    /* ignore */
                }
                try
                {
                    if (pendingMoveAtkValuesPtr != 0)
                    {
                        Marshal.FreeHGlobal(pendingMoveAtkValuesPtr);
                    }
                }
                catch
                {
                    /* ignore */
                }
                pendingMoveOutValuePtr = 0;
                pendingMoveOutValueFreeAtMs = 0;
                pendingMoveAtkValuesPtr = 0;
                pendingMoveCreatedAtMs = 0;
                pendingMoveSawInputNumeric = false;
            }
        }

        if (Configuration.EnableCompanyChest)
        {
            ProcessCompanyChestDeposit(now);
        }

        if (Configuration is { EnableCompanyChest: true, EnableCompanyChestMiddleClickOrganize: true })
        {
            ProcessCompanyChestOrganize(now);
        }

        var mmb = pendingMiddleClickSortRequest;
        if (Configuration.EnableMiddleClickSort && mmb != null && now - mmb.Value.EnqueuedAtMs <= 1500)
        {
            if (InventoryHelpers.IsCompanyChestType(mmb.Value.Type) && Configuration is { EnableCompanyChest: true, EnableCompanyChestMiddleClickOrganize: true })
            {
                StartCompanyChestOrganize(now, mmb.Value.Type);
                pendingMiddleClickSortRequest = null;
                pendingMiddleClickSortUntilMs = 0;
            }
            else
            {
                if (!InventoryHelpers.IsPlayerInventoryType(mmb.Value.Type) && !InventoryHelpers.IsArmouryType(mmb.Value.Type) && !InventoryHelpers.IsSaddlebagType(mmb.Value.Type) &&
                    !InventoryHelpers.IsRetainerType(mmb.Value.Type) && !InventoryHelpers.IsCompanyChestType(mmb.Value.Type))
                {
                    if (Configuration.DebugMode)
                    {
                        Svc.Log.Information($"[QuickTransfer] (MMB) Refusing to call OpenForItemSlot for unrecognized inventory type={mmb.Value.Type} slot={mmb.Value.Slot} addonId={mmb.Value.AddonId} (crash-prevention).");
                    }
                    pendingMiddleClickSortRequest = null;
                    pendingMiddleClickSortUntilMs = 0;
                    return;
                }

                var agentModule = AgentModule.Instance();
                if (agentModule != null)
                {
                    var agent = agentModule->GetAgentByInternalId(AgentId.InventoryContext);
                    var invCtx = (AgentInventoryContext*)agent;
                    if (invCtx != null)
                    {
                        try
                        {
                            ArmSuppressContextMenu(now);
                            if (Configuration.DebugMode)
                            {
                                Svc.Log.Information($"[QuickTransfer] (MMB) Calling OpenForItemSlot: type={mmb.Value.Type} slot={mmb.Value.Slot} addonId={mmb.Value.AddonId}");
                            }

                            int[] candidates;
                            if (!observedContextA4.TryGetValue((mmb.Value.AddonId, (uint)mmb.Value.Type), out var observedA4))
                            {
                                candidates = InventoryHelpers.IsArmouryType(mmb.Value.Type) ? [1, 0, 2] : [0, 1, 2];
                            }
                            else
                            {
                                candidates = [observedA4, 0, 1, 2];
                            }

                            var opened = false;
                            var usedA4 = 0;
                            foreach (var a4 in candidates.Distinct())
                            {
                                invCtx->OpenForItemSlot(mmb.Value.Type, mmb.Value.Slot, a4, mmb.Value.AddonId);
                                usedA4 = a4;
                                if (invCtx->ContextItemCount > 0)
                                {
                                    opened = true;
                                    observedContextA4[(mmb.Value.AddonId, (uint)mmb.Value.Type)] = a4;
                                    break;
                                }
                            }

                            try
                            {
                                var cm = InventoryHelpers.GetAddonByName(QuickTransferConstants.ContextMenuAddonName);
                                pendingDeferredSortMenuClick = ((nint)invCtx, cm != null ? (nint)cm : 0, now);
                            }
                            catch
                            {
                                pendingDeferredSortMenuClick = ((nint)invCtx, 0, now);
                            }

                            if (Configuration.DebugMode)
                            {
                                try
                                {
                                    Svc.Log.Information(
                                        $"[QuickTransfer] (MMB) Post OpenForItemSlot: opened={(opened ? 1 : 0)} usedA4={usedA4} ContextItemCount={invCtx->ContextItemCount}, " +
                                        $"OwnerAddonId={invCtx->OwnerAddonId}, BlockingAddonId={invCtx->BlockingAddonId}, " +
                                        $"TargetInv={invCtx->TargetInventoryId}, TargetSlot={invCtx->TargetInventorySlotId}");
                                }
                                catch
                                {
                                    // ignore
                                }
                            }
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }

                pendingMiddleClickSortRequest = null;
            }
        }

        if (pendingCloseContextMenuAtMs > 0 && now >= pendingCloseContextMenuAtMs)
        {
            pendingCloseContextMenuAtMs = 0;
            try
            {
                var cm = InventoryHelpers.GetAddonByName(QuickTransferConstants.ContextMenuAddonName);
                if (cm != null)
                {
                    try { cm->Hide(false, true, 0); }
                    catch
                    {
                        /* ignore */
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        var pendingDefault = pendingDeferredDefaultMenu;
        if (pendingDefault != null)
        {
            pendingDeferredDefaultMenu = null;

            if (now - pendingDefault.Value.EnqueuedAtMs <= 1500 &&
                (pendingDefault.Value.Mode == ModifierMode.Shift || pendingDefault.Value.Mode == ModifierMode.Alt) &&
                pendingDefault.Value.AddonName.Equals(QuickTransferConstants.FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase) &&
                Configuration.EnableCompanyChest)
            {
                ArmSuppressContextMenu(now, 1500);
                if (TrySelectRemoveFromCompanyChestContextMenu())
                {
                    lastActionTickMs = now;
                    ArmPendingNumeric(
                        now,
                        PendingNumericKind.Remove,
                        Configuration.AutoConfirmCompanyChestQuantity ? 1500 : 0,
                        half: pendingDefault.Value.Mode == ModifierMode.Alt,
                        suppressMs: 1500);
                }
            }
        }

        var pending = pendingDeferredMenuClick;
        if (pending == null)
        {
            ProcessDeferredSortMenuClick(now);
            return;
        }

        if (now - pending.Value.EnqueuedAtMs > 1500)
        {
            pendingDeferredMenuClick = null;
            return;
        }

        if (now - pending.Value.EnqueuedAtMs < 50)
        {
            return;
        }

        pendingDeferredMenuClick = null;

        if (now - lastActionTickMs < Configuration.TransferCooldownMs)
        {
            return;
        }

        try
        {
            var agent = (AgentInventoryContext*)pending.Value.AgentPtr;
            if (InventoryHelpers.ShouldYieldQuickTransferForRetainerMarket(
                    Configuration,
                    pending.Value.Mode,
                    agent->TargetInventoryId))
            {
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information("[QuickTransfer] Skipping deferred quick transfer — retainer sell list open.");
                }

                ProcessDeferredSortMenuClick(now);
                return;
            }

            AtkUnitBase* addon = null;
            try
            {
                addon = InventoryHelpers.GetAddonByName(QuickTransferConstants.ContextMenuAddonName);
            }
            catch
            {
                // ignore
            }

            if (addon == null)
            {
                addon = (AtkUnitBase*)pending.Value.AddonPtr;
            }

            if (ContextMenuHandler.TryAutoSelectAndClose(
                agent,
                addon,
                pending.Value.Mode,
                Configuration,
                out var chosenText,
                out var chosenIndex,
                ref pendingCloseContextMenuAtMs))
            {
                lastActionTickMs = now;
                var suppressMs = (pending.Value.Mode == ModifierMode.Alt && chosenText.Length > 0 && ContextMenuHandler.ContextLabelMatches(AutoContextAction.Split, chosenText))
                    ? 3000
                    : 1500;
                ArmSuppressContextMenu(now, suppressMs);
                if (pending.Value.Mode == ModifierMode.Shift &&
                    chosenText.Length > 0 &&
                    ContextMenuHandler.ContextLabelMatches(AutoContextAction.Trade, chosenText))
                {
                    ArmPendingNumeric(now, PendingNumericKind.Trade, 1500, suppressMs: 1500);
                }
                if (Configuration.AutoConfirmVendorSell &&
                    pending.Value.Mode == ModifierMode.Shift &&
                    chosenText.Length > 0 &&
                    ContextMenuHandler.ContextLabelMatches(AutoContextAction.Sell, chosenText) &&
                    InventoryHelpers.IsVendorOpen())
                {
                    ArmPendingNumeric(now, PendingNumericKind.Sell, 1500, suppressMs: 1500);
                }
                if (Configuration.EnableCompanyChest &&
                    pending.Value.Mode == ModifierMode.Shift &&
                    chosenText.Length > 0 &&
                    ContextMenuHandler.ContextLabelMatches(AutoContextAction.RemoveFromCompanyChest, chosenText))
                {
                    ArmPendingNumeric(
                        now,
                        PendingNumericKind.Remove,
                        Configuration.AutoConfirmCompanyChestQuantity ? 1500 : 0,
                        suppressMs: 1500);
                }
                if (pending.Value.Mode == ModifierMode.Alt &&
                    chosenText.Length > 0 &&
                    ContextMenuHandler.ContextLabelMatches(AutoContextAction.Split, chosenText))
                {
                    ArmPendingNumeric(
                        now,
                        PendingNumericKind.Split,
                        Configuration.AutoConfirmCompanyChestQuantity ? 5000 : 0,
                        half: true,
                        suppressMs: 5000);

                    try
                    {
                        var srcType = agent->TargetInventoryId;
                        var srcSlot = agent->TargetInventorySlotId;
                        if (InventoryHelpers.TryGetItemInfo(srcType, srcSlot, out var _, out var _, out var qty) && qty > 1)
                        {
                            pendingSplitExpectedMax = qty - 1;
                            pendingSplitExpectedUntilMs = now + 5000;
                        }
                        else
                        {
                            pendingSplitExpectedMax = 0;
                            pendingSplitExpectedUntilMs = 0;
                        }
                    }
                    catch
                    {
                        pendingSplitExpectedMax = 0;
                        pendingSplitExpectedUntilMs = 0;
                    }
                }
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information($"[QuickTransfer] ({pending.Value.Mode} + RClick) Selected context action '{chosenText}' (idx={chosenIndex}) via deferred OnMenuOpened.");
                }
            }
            else if (Configuration.DebugMode && pending.Value.Mode == ModifierMode.Ctrl)
            {
                Svc.Log.Information("[QuickTransfer] (Ctrl + RClick) Deferred menu opened but no matching 'Place in Armoury Chest' action was found.");
                ContextMenuHandler.DebugDumpContextMenu(agent, 24);
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[QuickTransfer] Deferred menu select failed.");
        }

        ProcessDeferredSortMenuClick(now);
    }

    private void OnAddonPreDraw(AddonEvent type, AddonArgs args)
    {
        try
        {
            var name = args.AddonName;
            var now = Environment.TickCount64;

            if (string.Equals(name, QuickTransferConstants.ContextMenuAddonName, StringComparison.OrdinalIgnoreCase))
            {
                var addon = (AtkUnitBase*)args.Addon.Address;
                if (now <= suppressContextMenuUntilMs)
                {
                    AtkValueHelpers.MakeAddonInvisible(addon);
                }
                else
                {
                    AtkValueHelpers.MakeAddonVisible(addon);
                }
            }

            if (string.Equals(name, QuickTransferConstants.InputNumericAddonName, StringComparison.OrdinalIgnoreCase))
            {
                var addon = (AtkUnitBase*)args.Addon.Address;
                if (now <= suppressInputNumericUntilMs)
                {
                    AtkValueHelpers.MakeAddonInvisible(addon);
                }
                else
                {
                    AtkValueHelpers.MakeAddonVisible(addon);
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private void OnAddonReceiveEvent(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!Configuration.Enabled || !ModifierBindings.IsMiddleClickConfigured(Configuration))
            {
                return;
            }

            if (args is not AddonReceiveEventArgs recv)
            {
                return;
            }

            var now = Environment.TickCount64;
            if (Configuration.DebugMode && !debugPrintedReceiveEventHook)
            {
                debugPrintedReceiveEventHook = true;
                try { Svc.Chat.Print("[QuickTransfer] ReceiveEvent hook active (MMB debug)."); }
                catch
                {
                    /* ignore */
                }
                Svc.Log.Information("[QuickTransfer] ReceiveEvent hook active (MMB debug).");
            }

            var eventType = (AtkEventType)recv.AtkEventType;
            var eventData = (AtkEventData*)recv.AtkEventData;
            var mouseButtonId = eventData != null ? eventData->MouseData.ButtonId : (byte)255;
            var dragDropMouseButtonId = eventData != null ? eventData->DragDropData.MouseButtonId : (byte)255;

            var addonName = args.AddonName;
            var allowMouseOverCapture =
                addonName.Equals("Inventory", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("InventoryBuddy", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("InventoryBuddy2", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("RetainerGrid0", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("RetainerGrid", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("RetainerSellList", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals(QuickTransferConstants.FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase);

            if (eventType is AtkEventType.MouseOver or AtkEventType.MouseOut or AtkEventType.DragDropRollOver or AtkEventType.DragDropRollOut or
                AtkEventType.ListItemRollOver or AtkEventType.ListItemRollOut)
            {
                try
                {
                    var ab = (AtkUnitBase*)args.Addon.Address;
                    var id = ab != null ? ab->Id : 0u;
                    if (eventType is AtkEventType.MouseOut or AtkEventType.DragDropRollOut or AtkEventType.ListItemRollOut)
                    {
                        lastHoverAddon = null;
                    }
                    else if (id != 0)
                    {
                        lastHoverAddon = (addonName, id, now);
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (addonName.Equals(QuickTransferConstants.FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase) &&
                eventType == AtkEventType.ButtonClick)
            {
                try
                {
                    var ab = (AtkUnitBase*)args.Addon.Address;
                    var id = ab != null ? ab->Id : 0u;
                    if (id != 0 && TryMapCompanyChestTabParamToPage(recv.EventParam, out var selectedPage))
                    {
                        lastSelectedCompanyChestPage = (selectedPage, id, now);
                        ObserveCompanyChestTabFromAtkValues(ab, selectedPage);
                        if (Configuration.DebugMode && now - lastReceiveEventDebugLogMs >= 250)
                        {
                            Svc.Log.Information($"[QuickTransfer] FC Chest selected tab: param={recv.EventParam} -> {selectedPage} (addonId={id})");
                        }

                        if (companyChestOrganize.Active &&
                            (companyChestOrganize.OwnerAddonId == 0 || companyChestOrganize.OwnerAddonId == id) &&
                            companyChestOrganize.Pages is { Length: 1 } &&
                            companyChestOrganize.Pages[0] != selectedPage)
                        {
                            companyChestOrganize.Active = false;
                            companyChestOrganize.WaitingForApply = false;
                            companyChestOrganize.WaitObservedChangeAtMs = 0;
                            if (Configuration.DebugMode)
                            {
                                Svc.Log.Information($"[QuickTransfer] (MMB) Company Chest tab changed to {selectedPage}; stopping previous organize run.");
                            }
                        }

                        if (companyChestDeposit.Active &&
                            companyChestDeposit.DestPage != default &&
                            companyChestDeposit.DestPage != selectedPage)
                        {
                            companyChestDeposit.Active = false;
                            if (Configuration.DebugMode)
                            {
                                Svc.Log.Information($"[QuickTransfer] (Shift+RClick) Company Chest tab changed to {selectedPage}; stopping deposit run.");
                            }
                        }
                    }
                    else if (Configuration.DebugMode && id != 0 && now - lastFcChestTabUnmappedLogMs >= 250)
                    {
                        lastFcChestTabUnmappedLogMs = now;
                        Svc.Log.Information($"[QuickTransfer] FC Chest tab param unmapped: param={recv.EventParam} (addonId={id})");
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (eventType is AtkEventType.DragDropRollOut || allowMouseOverCapture && eventType is AtkEventType.MouseOut)
            {
                lastHoverDdi = null;
                lastHoverAddonName = string.Empty;
            }
            else if (eventType is AtkEventType.DragDropRollOver or AtkEventType.DragDropClick ||
                     allowMouseOverCapture && eventType is AtkEventType.MouseOver)
            {
                if (DragDropHelpers.TryGetDragDropInterfaceFromReceiveEvent(args, recv, eventType, eventData, out var hAddonId, out var hDdi) && hDdi != null)
                {
                    var ptr = (nint)hDdi;
                    if (ptr >= QuickTransferConstants.MinLikelyPointer)
                    {
                        lastHoverDdi = (ptr, hAddonId, now);
                        lastHoverAddonName = addonName;
                    }

                    if (addonName.Equals(QuickTransferConstants.FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            if (DragDropHelpers.TryGetSlotFromDragDropInterface(hDdi, out var hoverInvType, out var _))
                            {
                                if (InventoryHelpers.IsCompanyChestDestinationType(hoverInvType))
                                {
                                    lastHoverCompanyChestPage = (hoverInvType, hAddonId, now);
                                }
                            }
                        }
                        catch
                        {
                            // ignore
                        }
                    }

                    if (Configuration.DebugMode && now - lastReceiveEventDebugLogMs >= 250)
                    {
                        Svc.Log.Information($"[QuickTransfer] HoverCapture: Addon='{args.AddonName}', EventType={eventType}, Param={recv.EventParam}, DDI=0x{(nint)hDdi:X}");
                    }
                }
            }

            bool? middleDown = null;
            try
            {
                const VirtualKey vkMButton = (VirtualKey)0x04; // VK_MBUTTON
                middleDown = Svc.KeyState[vkMButton];
            }
            catch
            {
                // ignore
            }

            var asyncMiddleDown = ModifierBindings.IsConfiguredMiddleClickDown(Configuration);
            var isMiddleByMask = ModifierBindings.IsMiddleClickEventMask(mouseButtonId, dragDropMouseButtonId, Configuration);
            var isMiddle = ModifierBindings.IsMiddleClickPressed(Configuration, mouseButtonId, dragDropMouseButtonId, middleDown);

            if (Configuration.DebugMode && now - lastReceiveEventDebugLogMs >= 250)
            {
                lastReceiveEventDebugLogMs = now;
                Svc.Log.Information(
                    $"[QuickTransfer] PreReceiveEvent: Addon='{args.AddonName}', Type={eventType}, Param={recv.EventParam}, " +
                    $"MouseBtn={mouseButtonId} (0x{mouseButtonId:X2}), DragBtn={dragDropMouseButtonId} (0x{dragDropMouseButtonId:X2}), " +
                    $"MaskMiddle={(isMiddleByMask ? "1" : "0")}, AsyncMiddle={(asyncMiddleDown ? "1" : "0")}, KeyStateMiddle={middleDown?.ToString() ?? "n/a"}");
            }

            if (now - lastMiddleClickSortMs < 250)
            {
                return;
            }

            if (eventType is not AtkEventType.DragDropClick and
                not AtkEventType.MouseClick and
                not AtkEventType.MouseDown)
            {
                return;
            }

            if (!isMiddle)
            {
                return;
            }

            if (!DragDropHelpers.TryGetDragDropInterfaceFromReceiveEvent(args, recv, eventType, eventData, out var addonId, out var ddi))
            {
                return;
            }
            if (!DragDropHelpers.TryGetSlotFromDragDropInterface(ddi, out var invType, out var slot))
            {
                return;
            }

            pendingMiddleClickSortRequest = (invType, slot, addonId, now);
            pendingMiddleClickSortUntilMs = now + 1500;
            lastMiddleClickSortMs = now;

            var atkEvent2 = (AtkEvent*)recv.AtkEvent;
            if (atkEvent2 != null)
            {
                atkEvent2->SetEventIsHandled();
            }
        }
        catch
        {
            // ignore
        }
    }

    private ModifierMode? GetModifierModeLatched(long nowMs)
    {
        if (Configuration.EnableAltSplit && IsModifierActive(Configuration.AltActionModifier, nowMs))
        {
            return ModifierMode.Alt;
        }

        if (Configuration.EnableCtrlArmoury && IsModifierActive(Configuration.CtrlActionModifier, nowMs))
        {
            return ModifierMode.Ctrl;
        }

        return Configuration.EnableShiftQuickTransfer && IsModifierActive(Configuration.ShiftActionModifier, nowMs)
            ? ModifierMode.Shift
            : null;
    }

    private bool IsModifierActive(VirtualKey key, long nowMs)
    {
        if (Svc.KeyState[key])
        {
            return true;
        }

        var latchWindowMs = Configuration.ModifierLatchMs;
        return key switch
        {
            VirtualKey.SHIFT => nowMs - lastShiftSeenMs <= latchWindowMs,
            VirtualKey.CONTROL => nowMs - lastCtrlSeenMs <= latchWindowMs,
            VirtualKey.MENU => nowMs - lastAltSeenMs <= latchWindowMs,
            _ => false
        };
    }
}
