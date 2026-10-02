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

public sealed unsafe partial class QuickTransferPlugin(IDalamudPluginInterface pluginInterface) : IAsyncDalamudPlugin
{
    private const int CurrentConfigVersion = 5;
    private const long DeferredRequestTimeoutMs = 1500;

    private readonly Dictionary<int, Dictionary<int, InventoryType>> companyChestSelectedTabCandidates = [];
    private readonly Dictionary<uint, (InventoryType Type, int Slot, int A4)> lastGoodContextTargetByAddonId = [];
    private readonly Dictionary<(uint OwnerAddonId, uint InventoryType), int> observedContextA4 = [];
    private readonly IDalamudPluginInterface pluginInterface = pluginInterface;
    private readonly WindowSystem windowSystem = new("QuickTransfer");
    private EzHook<AgentInventoryContext.Delegates.OpenForItemSlot>? openForItemSlotHook;
    private PluginUI pluginUi = null!;
    private bool ecommonsInitialized;

    private CompanyChestDepositState companyChestDeposit;
    private CompanyChestOrganizeState companyChestOrganize;
    private int companyChestBusyHits;
    private long companyChestBusyUntilMs;
    private int companyChestSelectedTabAtkValueIndex = -1;

    private long lastActionTickMs;
    private long lastShiftSeenMs;
    private long lastCtrlSeenMs;
    private long lastAltSeenMs;
    private bool lastVkMButtonDown;
    private bool lastVkX1ButtonDown;
    private bool lastVkX2ButtonDown;
    private long lastMiddleClickSortMs;

    private (string AddonName, uint AddonId, long SeenAtMs)? lastHoverAddon;
    private string lastHoverAddonName = string.Empty;
    private (nint DdiPtr, uint AddonId, long SeenAtMs)? lastHoverDdi;
    private (InventoryType Page, uint AddonId, long SeenAtMs)? lastHoverCompanyChestPage;
    private (InventoryType Page, uint AddonId, long SeenAtMs)? lastSelectedCompanyChestPage;

    private bool debugPrintedReceiveEventHook;
    private long lastCompanyChestOrganizeSkipLogMs;
    private string lastCompanyChestOrganizeSkipReason = string.Empty;
    private long lastCursorHitTestLogMs;
    private long lastFcChestTabUnmappedLogMs;
    private long lastObservedA4LogMs;
    private long lastReceiveEventDebugLogMs;

    private (string AddonName, long EnqueuedAtMs, ModifierMode Mode)? pendingDeferredDefaultMenu;
    private (nint AgentPtr, nint AddonPtr, long EnqueuedAtMs, ModifierMode Mode)? pendingDeferredMenuClick;
    private (nint AgentPtr, nint AddonPtr, long EnqueuedAtMs)? pendingDeferredSortMenuClick;
    private (InventoryType Type, int Slot, uint AddonId, long EnqueuedAtMs)? pendingMiddleClickSortRequest;
    private long pendingMiddleClickSortUntilMs;
    private long pendingCloseContextMenuAtMs;
    private long suppressContextMenuUntilMs;
    private long suppressInputNumericUntilMs;

    private PendingNumericKind pendingNumericKind;
    private long pendingCompanyChestNumericConfirmUntilMs;
    private uint pendingCompanyChestNumericDesired;
    private bool pendingCompanyChestNumericHalf;
    private bool pendingCompanyChestNumericValueSet;
    private long pendingCompanyChestNumericValueSetAtMs;
    private uint pendingSplitExpectedMax;
    private long pendingSplitExpectedUntilMs;

    // HandleItemMove buffers must outlive the call when the game opens an InputNumeric prompt for the move.
    private nint pendingMoveAtkValuesPtr;
    private nint pendingMoveOutValuePtr;
    private long pendingMoveCreatedAtMs;
    private long pendingMoveOutValueFreeAtMs;
    private bool pendingMoveSawInputNumeric;

    public Configuration Configuration { get; private set; } = null!;

    public Task LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ECommonsMain.Init(pluginInterface, this);
        ecommonsInitialized = true;

        Configuration = Svc.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(Svc.PluginInterface);
        MigrateConfiguration();
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

        CreateOpenForItemSlotHook();

        Svc.ContextMenu.OnMenuOpened += OnContextMenuOpened;
        Svc.Framework.Update += OnFrameworkUpdate;

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, QuickTransferConstants.InputNumericAddonName, OnInputNumericPreSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, QuickTransferConstants.ContextMenuAddonName, OnAddonPreDraw);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, QuickTransferConstants.InputNumericAddonName, OnAddonPreDraw);
        foreach (var name in QuickTransferConstants.AllContainerAddonNames)
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
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[QuickTransfer] Failed to save configuration during dispose.");
        }

        try
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
            Svc.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
            Svc.Chat.ChatMessage -= OnChatMessage;
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreSetup, QuickTransferConstants.InputNumericAddonName, OnInputNumericPreSetup);
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, QuickTransferConstants.ContextMenuAddonName, OnAddonPreDraw);
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, QuickTransferConstants.InputNumericAddonName, OnAddonPreDraw);
            foreach (var name in QuickTransferConstants.AllContainerAddonNames)
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
            Svc.Log.Warning(ex, "[QuickTransfer] Error while unregistering during dispose.");
        }

        ReleasePendingMoveBuffers();
        ECommonsMain.Dispose();
        return ValueTask.CompletedTask;
    }

    private void CreateOpenForItemSlotHook()
    {
        try
        {
            var funcPtr = AgentInventoryContext.MemberFunctionPointers.OpenForItemSlot;
            if (funcPtr != null)
            {
                openForItemSlotHook = new((nint)funcPtr, OpenForItemSlotDetour);
                return;
            }

            Svc.Log.Warning("[QuickTransfer] AgentInventoryContext.MemberFunctionPointers.OpenForItemSlot is null - falling back to manual signature");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[QuickTransfer] Failed to hook OpenForItemSlot using ClientStructs delegate - falling back to manual signature");
        }

        try
        {
            openForItemSlotHook = new("83 B9 ?? ?? ?? ?? ?? 7E ?? 39 91", OpenForItemSlotDetour);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[QuickTransfer] Failed to hook OpenForItemSlot with fallback signature");
        }
    }

    private void MigrateConfiguration()
    {
        if (Configuration.Version >= CurrentConfigVersion)
        {
            return;
        }

        if (Configuration.Version < 3)
        {
            Configuration.DebugMode = false;
        }

        Configuration.Version = CurrentConfigVersion;
        try
        {
            Configuration.Save();
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[QuickTransfer] Failed to save migrated configuration.");
        }
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

    private void ReleasePendingMoveBuffers()
    {
        if (pendingMoveOutValuePtr != 0)
        {
            Marshal.FreeHGlobal(pendingMoveOutValuePtr);
        }

        if (pendingMoveAtkValuesPtr != 0)
        {
            Marshal.FreeHGlobal(pendingMoveAtkValuesPtr);
        }

        pendingMoveOutValuePtr = 0;
        pendingMoveAtkValuesPtr = 0;
        pendingMoveOutValueFreeAtMs = 0;
        pendingMoveCreatedAtMs = 0;
        pendingMoveSawInputNumeric = false;
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

        // Remember the a4 argument and last menu-producing target per addon so middle-click can replay OpenForItemSlot.
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
                Svc.Log.Information("[QuickTransfer] Yielding quick transfer - retainer sell list open.");
            }

            return;
        }

        var companyChestOpen = InventoryHelpers.IsCompanyChestOpen();
        if (mode == ModifierMode.Ctrl &&
            (!(InventoryHelpers.IsSaddlebagOpen() || InventoryHelpers.IsRetainerOpen() || companyChestOpen) ||
             InventoryHelpers.IsSaddlebagType(inventoryType) ||
             InventoryHelpers.IsRetainerType(inventoryType) ||
             InventoryHelpers.IsCompanyChestType(inventoryType)))
        {
            return;
        }

        var now = Environment.TickCount64;
        if (mode == ModifierMode.Alt || now - lastActionTickMs < Configuration.TransferCooldownMs)
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

            ArmTradeOrSellConfirm(now, mode.Value, chosenText, requireTradeWindow: true);
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

        if (!middleSortActive &&
            mode == ModifierMode.Ctrl &&
            !(InventoryHelpers.IsSaddlebagOpen() || InventoryHelpers.IsRetainerOpen() || InventoryHelpers.IsCompanyChestOpen()))
        {
            return;
        }

        if (Configuration.DebugMode)
        {
            Svc.Log.Information($"[QuickTransfer] OnMenuOpened: AddonName='{args.AddonName}', MenuType={args.MenuType}, AgentPtr=0x{args.AgentPtr.ToInt64():X}, AddonPtr=0x{args.AddonPtr.ToInt64():X}");
        }

        if (middleSortActive &&
            args.MenuType == ContextMenuType.Inventory &&
            args.AgentPtr != nint.Zero &&
            args.AddonPtr != nint.Zero)
        {
            pendingDeferredSortMenuClick = (args.AgentPtr, args.AddonPtr, now);
            return;
        }

        if (args.MenuType == ContextMenuType.Default &&
            mode is ModifierMode.Shift or ModifierMode.Alt &&
            Configuration.EnableCompanyChest &&
            string.Equals(args.AddonName, QuickTransferConstants.FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase))
        {
            pendingDeferredDefaultMenu = (args.AddonName ?? string.Empty, now, mode.Value);
            return;
        }

        if (args.MenuType != ContextMenuType.Inventory ||
            args.AgentPtr == nint.Zero ||
            args.AddonPtr == nint.Zero ||
            mode == null)
        {
            return;
        }

        try
        {
            var agent = (AgentInventoryContext*)args.AgentPtr;
            if (InventoryHelpers.ShouldYieldQuickTransferForRetainerMarket(Configuration, mode.Value, agent->TargetInventoryId))
            {
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information("[QuickTransfer] Yielding deferred quick transfer - retainer sell list open.");
                }

                return;
            }
        }
        catch
        {
        }

        // The menu entries are not populated yet; select on a later frame.
        pendingDeferredMenuClick = (args.AgentPtr, args.AddonPtr, now, mode.Value);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!Configuration.Enabled)
        {
            return;
        }

        var now = Environment.TickCount64;

        PollMiddleClickButtons(now);
        TrackModifierKeys(now);

        if (ProcessPendingNumeric(now))
        {
            return;
        }

        ReleasePendingMoveBuffersWhenDone(now);

        if (Configuration.EnableCompanyChest)
        {
            ProcessCompanyChestDeposit(now);
        }

        if (Configuration is { EnableCompanyChest: true, EnableCompanyChestMiddleClickOrganize: true })
        {
            ProcessCompanyChestOrganize(now);
        }

        if (ProcessMiddleClickSortRequest(now))
        {
            return;
        }

        ProcessPendingContextMenuClose(now);
        ProcessDeferredDefaultMenu(now);
        ProcessDeferredMenuClick(now);
    }

    private void PollMiddleClickButtons(long now)
    {
        var mDown = ModifierBindings.IsMouseButtonDown(ModifierBindings.VkMiddleButton);
        var x1Down = ModifierBindings.IsMouseButtonDown(ModifierBindings.VkXButton1);
        var x2Down = ModifierBindings.IsMouseButtonDown(ModifierBindings.VkXButton2);

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

        if (!ModifierBindings.IsMiddleClickEdge(mDown, prevM, x1Down, prevX1, x2Down, prevX2, Configuration))
        {
            return;
        }

        if (ModifierBindings.IsMiddleClickConfigured(Configuration))
        {
            TryQueueMiddleClickSortFromHover(now);
        }
        else if (Configuration.DebugMode)
        {
            Svc.Log.Information("[QuickTransfer] (MMB) Press detected, but middle-click sort is disabled or no mouse buttons are selected.");
        }
    }

    private void TrackModifierKeys(long now)
    {
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
    }

    // Returns true when the rest of this frame's processing should be skipped.
    private bool ProcessPendingNumeric(long now)
    {
        var shouldAutoConfirm = pendingNumericKind is PendingNumericKind.Trade or PendingNumericKind.Split ||
                                Configuration.AutoConfirmVendorSell && pendingNumericKind == PendingNumericKind.Sell ||
                                Configuration.AutoConfirmCompanyChestQuantity && pendingNumericKind != PendingNumericKind.None;

        var armed = pendingNumericKind != PendingNumericKind.None &&
                    pendingCompanyChestNumericConfirmUntilMs > 0 &&
                    now <= pendingCompanyChestNumericConfirmUntilMs;

        if (!shouldAutoConfirm || !armed)
        {
            if (pendingCompanyChestNumericConfirmUntilMs > 0 && now > pendingCompanyChestNumericConfirmUntilMs)
            {
                ClearPendingNumeric(restoreInputNumericAlpha: true);
            }

            return false;
        }

        if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out var inputNumeric))
        {
            return ConfirmInputNumeric(now, inputNumeric);
        }

        if (Configuration.AutoConfirmVendorSell &&
            pendingNumericKind == PendingNumericKind.Sell &&
            InventoryHelpers.IsVendorOpen() &&
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

        return false;
    }

    // Writes the value on one frame and confirms it on a later one; the game ignores a same-frame confirm.
    private bool ConfirmInputNumeric(long now, AtkUnitBase* inputNumeric)
    {
        ArmSuppressInputNumeric(now);
        if (!pendingCompanyChestNumericValueSet)
        {
            if (TrySetInputNumericToMax(inputNumeric, pendingNumericKind))
            {
                pendingCompanyChestNumericValueSet = true;
                pendingCompanyChestNumericValueSetAtMs = now;
            }
            else
            {
                LogInputNumericMismatch(inputNumeric, "skipped");
                ClearPendingNumeric(restoreInputNumericAlpha: true);
            }

            return true;
        }

        if (now - pendingCompanyChestNumericValueSetAtMs < 50)
        {
            return true;
        }

        if (!TrySetInputNumericToMax(inputNumeric, pendingNumericKind))
        {
            LogInputNumericMismatch(inputNumeric, "aborted");
            ClearPendingNumeric();
            return true;
        }

        try
        {
            var toConfirm = pendingCompanyChestNumericDesired;
            if (toConfirm == 0)
            {
                var maxVal = inputNumeric->AtkValues + 3;
                toConfirm = maxVal->Type switch
                {
                    AtkValueType.UInt => maxVal->UInt,
                    AtkValueType.Int => (uint)Math.Max(0, maxVal->Int),
                    var _ => 0
                };

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

        return false;
    }

    private void LogInputNumericMismatch(AtkUnitBase* inputNumeric, string verb)
    {
        if (!Configuration.DebugMode)
        {
            return;
        }

        try
        {
            var prompt = AtkValueHelpers.ReadStringOrEmpty(inputNumeric->AtkValues + 6);
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

    private void ReleasePendingMoveBuffersWhenDone(long now)
    {
        if (pendingMoveOutValuePtr == 0 && pendingMoveAtkValuesPtr == 0)
        {
            return;
        }

        var inputVisible = InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out var _);
        if (inputVisible)
        {
            pendingMoveSawInputNumeric = true;
        }

        var graceExpired = pendingMoveCreatedAtMs > 0 && now - pendingMoveCreatedAtMs >= 1500;
        if (pendingMoveSawInputNumeric && !inputVisible || now >= pendingMoveOutValueFreeAtMs || !inputVisible && graceExpired)
        {
            ReleasePendingMoveBuffers();
        }
    }

    // Returns true when the rest of this frame's processing should be skipped.
    private bool ProcessMiddleClickSortRequest(long now)
    {
        var request = pendingMiddleClickSortRequest;
        if (!Configuration.EnableMiddleClickSort || request == null || now - request.Value.EnqueuedAtMs > DeferredRequestTimeoutMs)
        {
            return false;
        }

        var (type, slot, addonId, _) = request.Value;

        if (InventoryHelpers.IsCompanyChestType(type) && Configuration is { EnableCompanyChest: true, EnableCompanyChestMiddleClickOrganize: true })
        {
            StartCompanyChestOrganize(now, type);
            pendingMiddleClickSortRequest = null;
            pendingMiddleClickSortUntilMs = 0;
            return false;
        }

        // Calling OpenForItemSlot with an inventory type the agent does not expect can crash the client.
        if (!InventoryHelpers.IsSortableContainerType(type))
        {
            if (Configuration.DebugMode)
            {
                Svc.Log.Information($"[QuickTransfer] (MMB) Refusing to call OpenForItemSlot for unrecognized inventory type={type} slot={slot} addonId={addonId}.");
            }

            pendingMiddleClickSortRequest = null;
            pendingMiddleClickSortUntilMs = 0;
            return true;
        }

        var agentModule = AgentModule.Instance();
        var invCtx = agentModule != null ? (AgentInventoryContext*)agentModule->GetAgentByInternalId(AgentId.InventoryContext) : null;
        if (invCtx != null)
        {
            try
            {
                OpenSortContextMenu(now, invCtx, type, slot, addonId);
            }
            catch
            {
            }
        }

        pendingMiddleClickSortRequest = null;
        return false;
    }

    private void OpenSortContextMenu(long now, AgentInventoryContext* invCtx, InventoryType type, int slot, uint addonId)
    {
        ArmSuppressContextMenu(now);
        if (Configuration.DebugMode)
        {
            Svc.Log.Information($"[QuickTransfer] (MMB) Calling OpenForItemSlot: type={type} slot={slot} addonId={addonId}");
        }

        // The meaning of a4 is unknown; try the value the game last used for this addon first, then common values.
        int[] candidates = observedContextA4.TryGetValue((addonId, (uint)type), out var observedA4)
            ? [observedA4, 0, 1, 2]
            : InventoryHelpers.IsArmouryType(type) ? [1, 0, 2] : [0, 1, 2];

        var opened = false;
        var usedA4 = 0;
        foreach (var a4 in candidates.Distinct())
        {
            invCtx->OpenForItemSlot(type, slot, a4, addonId);
            usedA4 = a4;
            if (invCtx->ContextItemCount > 0)
            {
                opened = true;
                observedContextA4[(addonId, (uint)type)] = a4;
                break;
            }
        }

        var cm = InventoryHelpers.GetAddonByName(QuickTransferConstants.ContextMenuAddonName);
        pendingDeferredSortMenuClick = ((nint)invCtx, (nint)cm, now);

        if (Configuration.DebugMode)
        {
            Svc.Log.Information(
                $"[QuickTransfer] (MMB) Post OpenForItemSlot: opened={(opened ? 1 : 0)} usedA4={usedA4} ContextItemCount={invCtx->ContextItemCount}, " +
                $"OwnerAddonId={invCtx->OwnerAddonId}, BlockingAddonId={invCtx->BlockingAddonId}, " +
                $"TargetInv={invCtx->TargetInventoryId}, TargetSlot={invCtx->TargetInventorySlotId}");
        }
    }

    private void ProcessPendingContextMenuClose(long now)
    {
        if (pendingCloseContextMenuAtMs <= 0 || now < pendingCloseContextMenuAtMs)
        {
            return;
        }

        pendingCloseContextMenuAtMs = 0;
        try
        {
            var cm = InventoryHelpers.GetAddonByName(QuickTransferConstants.ContextMenuAddonName);
            if (cm != null)
            {
                cm->Hide(false, true, 0);
            }
        }
        catch
        {
        }
    }

    private void ProcessDeferredDefaultMenu(long now)
    {
        var pending = pendingDeferredDefaultMenu;
        if (pending == null)
        {
            return;
        }

        pendingDeferredDefaultMenu = null;

        var (addonName, enqueuedAtMs, mode) = pending.Value;
        if (now - enqueuedAtMs > DeferredRequestTimeoutMs ||
            mode is not (ModifierMode.Shift or ModifierMode.Alt) ||
            !addonName.Equals(QuickTransferConstants.FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase) ||
            !Configuration.EnableCompanyChest)
        {
            return;
        }

        ArmSuppressContextMenu(now, 1500);
        if (TrySelectRemoveFromCompanyChestContextMenu())
        {
            lastActionTickMs = now;
            ArmPendingNumeric(
                now,
                PendingNumericKind.Remove,
                Configuration.AutoConfirmCompanyChestQuantity ? 1500 : 0,
                half: mode == ModifierMode.Alt,
                suppressMs: 1500);
        }
    }

    private void ProcessDeferredMenuClick(long now)
    {
        var pending = pendingDeferredMenuClick;
        if (pending == null)
        {
            ProcessDeferredSortMenuClick(now);
            return;
        }

        if (now - pending.Value.EnqueuedAtMs > DeferredRequestTimeoutMs)
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

        var mode = pending.Value.Mode;
        try
        {
            var agent = (AgentInventoryContext*)pending.Value.AgentPtr;
            if (InventoryHelpers.ShouldYieldQuickTransferForRetainerMarket(Configuration, mode, agent->TargetInventoryId))
            {
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information("[QuickTransfer] Skipping deferred quick transfer - retainer sell list open.");
                }

                ProcessDeferredSortMenuClick(now);
                return;
            }

            var addon = InventoryHelpers.GetAddonByName(QuickTransferConstants.ContextMenuAddonName);
            if (addon == null)
            {
                addon = (AtkUnitBase*)pending.Value.AddonPtr;
            }

            if (ContextMenuHandler.TryAutoSelectAndClose(
                agent,
                addon,
                mode,
                Configuration,
                out var chosenText,
                out var chosenIndex,
                ref pendingCloseContextMenuAtMs))
            {
                lastActionTickMs = now;
                ArmPendingNumericForDeferredSelection(now, agent, mode, chosenText);
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information($"[QuickTransfer] ({mode} + RClick) Selected context action '{chosenText}' (idx={chosenIndex}) via deferred OnMenuOpened.");
                }
            }
            else if (Configuration.DebugMode && mode == ModifierMode.Ctrl)
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

    private void ArmTradeOrSellConfirm(long now, ModifierMode mode, string chosenText, bool requireTradeWindow)
    {
        if (mode != ModifierMode.Shift)
        {
            return;
        }

        if (ContextMenuHandler.ContextLabelMatches(AutoContextAction.Trade, chosenText) &&
            (!requireTradeWindow || InventoryHelpers.IsTradeOpen()))
        {
            ArmPendingNumeric(now, PendingNumericKind.Trade, 1500, suppressMs: 1500);
        }

        if (Configuration.AutoConfirmVendorSell &&
            ContextMenuHandler.ContextLabelMatches(AutoContextAction.Sell, chosenText) &&
            InventoryHelpers.IsVendorOpen())
        {
            ArmPendingNumeric(now, PendingNumericKind.Sell, 1500, suppressMs: 1500);
        }
    }

    private void ArmPendingNumericForDeferredSelection(long now, AgentInventoryContext* agent, ModifierMode mode, string chosenText)
    {
        var isSplit = mode == ModifierMode.Alt && ContextMenuHandler.ContextLabelMatches(AutoContextAction.Split, chosenText);
        ArmSuppressContextMenu(now, isSplit ? 3000 : 1500);
        ArmTradeOrSellConfirm(now, mode, chosenText, requireTradeWindow: false);

        if (Configuration.EnableCompanyChest &&
            mode == ModifierMode.Shift &&
            ContextMenuHandler.ContextLabelMatches(AutoContextAction.RemoveFromCompanyChest, chosenText))
        {
            ArmPendingNumeric(
                now,
                PendingNumericKind.Remove,
                Configuration.AutoConfirmCompanyChestQuantity ? 1500 : 0,
                suppressMs: 1500);
        }

        if (!isSplit)
        {
            return;
        }

        ArmPendingNumeric(
            now,
            PendingNumericKind.Split,
            Configuration.AutoConfirmCompanyChestQuantity ? 5000 : 0,
            half: true,
            suppressMs: 5000);

        // The split prompt has no reliable label, so remember the expected max (stack - 1) to recognize it.
        pendingSplitExpectedMax = 0;
        pendingSplitExpectedUntilMs = 0;
        try
        {
            if (InventoryHelpers.TryGetItemInfo(agent->TargetInventoryId, agent->TargetInventorySlotId, out var _, out var _, out var qty) && qty > 1)
            {
                pendingSplitExpectedMax = qty - 1;
                pendingSplitExpectedUntilMs = now + 5000;
            }
        }
        catch
        {
        }
    }

    private void OnAddonPreDraw(AddonEvent type, AddonArgs args)
    {
        try
        {
            var now = Environment.TickCount64;
            long suppressUntilMs;
            if (string.Equals(args.AddonName, QuickTransferConstants.ContextMenuAddonName, StringComparison.OrdinalIgnoreCase))
            {
                suppressUntilMs = suppressContextMenuUntilMs;
            }
            else if (string.Equals(args.AddonName, QuickTransferConstants.InputNumericAddonName, StringComparison.OrdinalIgnoreCase))
            {
                suppressUntilMs = suppressInputNumericUntilMs;
            }
            else
            {
                return;
            }

            AtkValueHelpers.SetAddonAlpha((AtkUnitBase*)args.Addon.Address, now <= suppressUntilMs ? (byte)0 : (byte)255);
        }
        catch
        {
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
                catch { }

                Svc.Log.Information("[QuickTransfer] ReceiveEvent hook active (MMB debug).");
            }

            var eventType = (AtkEventType)recv.AtkEventType;
            var eventData = (AtkEventData*)recv.AtkEventData;
            var mouseButtonId = eventData != null ? eventData->MouseData.ButtonId : (byte)255;
            var dragDropMouseButtonId = eventData != null ? eventData->DragDropData.MouseButtonId : (byte)255;

            var addonName = args.AddonName;
            var allowMouseOverCapture = QuickTransferConstants.ContainerAddonNames.Contains(addonName, StringComparer.OrdinalIgnoreCase);
            var isCompanyChest = addonName.Equals(QuickTransferConstants.FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase);

            if (eventType is AtkEventType.MouseOver or AtkEventType.MouseOut or AtkEventType.DragDropRollOver or AtkEventType.DragDropRollOut or
                AtkEventType.ListItemRollOver or AtkEventType.ListItemRollOut)
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

            if (isCompanyChest && eventType == AtkEventType.ButtonClick)
            {
                OnCompanyChestButtonClick((AtkUnitBase*)args.Addon.Address, recv.EventParam, now);
            }

            if (eventType is AtkEventType.DragDropRollOut || allowMouseOverCapture && eventType is AtkEventType.MouseOut)
            {
                lastHoverDdi = null;
                lastHoverAddonName = string.Empty;
            }
            else if (eventType is AtkEventType.DragDropRollOver or AtkEventType.DragDropClick ||
                     allowMouseOverCapture && eventType is AtkEventType.MouseOver)
            {
                CaptureHoveredDragDrop(args, recv, eventType, eventData, isCompanyChest, now);
            }

            bool? middleDown = null;
            try
            {
                middleDown = Svc.KeyState[(VirtualKey)ModifierBindings.VkMiddleButton];
            }
            catch
            {
            }

            var isMiddle = ModifierBindings.IsMiddleClickPressed(Configuration, mouseButtonId, dragDropMouseButtonId, middleDown);

            if (Configuration.DebugMode && now - lastReceiveEventDebugLogMs >= 250)
            {
                lastReceiveEventDebugLogMs = now;
                var asyncMiddleDown = ModifierBindings.IsConfiguredMiddleClickDown(Configuration);
                var isMiddleByMask = ModifierBindings.IsMiddleClickEventMask(mouseButtonId, dragDropMouseButtonId, Configuration);
                Svc.Log.Information(
                    $"[QuickTransfer] PreReceiveEvent: Addon='{addonName}', Type={eventType}, Param={recv.EventParam}, " +
                    $"MouseBtn={mouseButtonId} (0x{mouseButtonId:X2}), DragBtn={dragDropMouseButtonId} (0x{dragDropMouseButtonId:X2}), " +
                    $"MaskMiddle={(isMiddleByMask ? "1" : "0")}, AsyncMiddle={(asyncMiddleDown ? "1" : "0")}, KeyStateMiddle={middleDown?.ToString() ?? "n/a"}");
            }

            if (now - lastMiddleClickSortMs < 250 ||
                eventType is not (AtkEventType.DragDropClick or AtkEventType.MouseClick or AtkEventType.MouseDown) ||
                !isMiddle)
            {
                return;
            }

            if (!DragDropHelpers.TryGetDragDropInterfaceFromReceiveEvent(args, recv, eventType, eventData, out var addonId, out var ddi) ||
                !DragDropHelpers.TryGetSlotFromDragDropInterface(ddi, out var invType, out var slot))
            {
                return;
            }

            QueueMiddleClickSort(invType, slot, addonId, now);

            var atkEvent = (AtkEvent*)recv.AtkEvent;
            if (atkEvent != null)
            {
                atkEvent->SetEventIsHandled();
            }
        }
        catch
        {
        }
    }

    private void CaptureHoveredDragDrop(
        AddonArgs args,
        AddonReceiveEventArgs recv,
        AtkEventType eventType,
        AtkEventData* eventData,
        bool isCompanyChest,
        long now)
    {
        if (!DragDropHelpers.TryGetDragDropInterfaceFromReceiveEvent(args, recv, eventType, eventData, out var hAddonId, out var hDdi) || hDdi == null)
        {
            return;
        }

        var ptr = (nint)hDdi;
        if (ptr >= QuickTransferConstants.MinLikelyPointer)
        {
            lastHoverDdi = (ptr, hAddonId, now);
            lastHoverAddonName = args.AddonName;
        }

        if (isCompanyChest &&
            DragDropHelpers.TryGetSlotFromDragDropInterface(hDdi, out var hoverInvType, out var _) &&
            InventoryHelpers.IsCompanyChestDestinationType(hoverInvType))
        {
            lastHoverCompanyChestPage = (hoverInvType, hAddonId, now);
        }

        if (Configuration.DebugMode && now - lastReceiveEventDebugLogMs >= 250)
        {
            Svc.Log.Information($"[QuickTransfer] HoverCapture: Addon='{args.AddonName}', EventType={eventType}, Param={recv.EventParam}, DDI=0x{ptr:X}");
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
