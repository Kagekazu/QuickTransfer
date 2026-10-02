using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace QuickTransfer;

// InputNumeric AtkValues: [2]=min, [3]=max, [4]=default, [5]=current (UInt or string), [6]=prompt.
public sealed unsafe partial class QuickTransferPlugin
{
    private static bool PromptMatchesKind(PendingNumericKind kind, string prompt)
    {
        var expected = kind switch
        {
            PendingNumericKind.Store => "store",
            PendingNumericKind.Remove => "remove",
            PendingNumericKind.Trade => "trade",
            PendingNumericKind.Sell => "sell",
            PendingNumericKind.Split => "split",
            var _ => null
        };

        return expected == null || prompt.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteInputNumericValue(AtkValue* defaultValue, AtkValue* currentValue, uint value)
    {
        defaultValue->UInt = value;
        if (currentValue == null)
        {
            return;
        }

        if (currentValue->Type == AtkValueType.UInt)
        {
            currentValue->UInt = value;
        }
        else if (AtkValueHelpers.IsString(currentValue->Type))
        {
            AtkValueHelpers.WriteUtf8InPlace(currentValue->String, value.ToString());
        }
    }

    // Pre-fills FC chest prompts with the max before they are first drawn.
    private void OnInputNumericPreSetup(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!Configuration.EnableCompanyChest ||
                pendingNumericKind == PendingNumericKind.None ||
                !string.Equals(args.AddonName, QuickTransferConstants.InputNumericAddonName, StringComparison.OrdinalIgnoreCase) ||
                !InventoryHelpers.IsCompanyChestOpen() ||
                args is not AddonSetupArgs setup)
            {
                return;
            }

            var values = (AtkValue*)setup.AtkValues;
            var count = (int)setup.AtkValueCount;
            if (values == null || count < 7)
            {
                return;
            }

            if (Configuration.DebugMode)
            {
                Svc.Log.Information($"[QuickTransfer] InputNumeric PreSetup (armed): AtkValueCount={count}");
            }

            var prompt = AtkValueHelpers.ReadStringOrEmpty(values + 6);
            if (pendingNumericKind is PendingNumericKind.Store or PendingNumericKind.Remove or PendingNumericKind.Sell &&
                !PromptMatchesKind(pendingNumericKind, prompt))
            {
                return;
            }

            if (values[2].Type != AtkValueType.UInt || values[3].Type != AtkValueType.UInt || values[4].Type != AtkValueType.UInt)
            {
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information($"[QuickTransfer] InputNumeric PreSetup: unexpected types: [2]={values[2].Type}, [3]={values[3].Type}, [4]={values[4].Type}");
                }

                return;
            }

            var min = values[2].UInt;
            var max = values[3].UInt;
            var desired = Math.Max(min, max);

            if (Configuration.DebugMode)
            {
                var curStr = values[5].Type == AtkValueType.UInt ? values[5].UInt.ToString() : "n/a";
                Svc.Log.Information($"[QuickTransfer] InputNumeric PreSetup: prompt='{prompt}', min={min}, max={max}, default={values[4].UInt}, current={curStr}, setDefault={desired}");
            }

            WriteInputNumericValue(values + 4, values + 5, desired);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[QuickTransfer] InputNumeric PreSetup failed.");
        }
    }

    private bool TrySetInputNumericToMax(AtkUnitBase* inputNumeric, PendingNumericKind kind)
    {
        try
        {
            if (inputNumeric == null || inputNumeric->AtkValues == null || inputNumeric->AtkValuesCount < 7)
            {
                return false;
            }

            var values = inputNumeric->AtkValues;
            var defaultValue = values + 4;
            var currentValue = values + 5;
            if (values[2].Type != AtkValueType.UInt || values[3].Type != AtkValueType.UInt || defaultValue->Type != AtkValueType.UInt)
            {
                return false;
            }

            var min = values[2].UInt;
            var max = values[3].UInt;
            var prompt = AtkValueHelpers.ReadStringOrEmpty(values + 6);

            // Trade/sell prompts are accepted while their window is open; split prompts are recognized by
            // the expected max (stack - 1) since their label is unreliable.
            if (!PromptMatchesKind(kind, prompt) && !(kind switch
            {
                PendingNumericKind.Trade => InventoryHelpers.IsTradeOpen(),
                PendingNumericKind.Sell => InventoryHelpers.IsVendorOpen(),
                PendingNumericKind.Split => pendingSplitExpectedMax != 0 &&
                                            Environment.TickCount64 <= pendingSplitExpectedUntilMs &&
                                            max == pendingSplitExpectedMax,
                var _ => false
            }))
            {
                return false;
            }

            uint desired;
            if (pendingCompanyChestNumericHalf)
            {
                if (kind == PendingNumericKind.Remove && max <= 1 || kind == PendingNumericKind.Split && max == 0)
                {
                    return false;
                }

                desired = kind == PendingNumericKind.Remove ? max / 2 : (max + 1) / 2;
                pendingCompanyChestNumericHalf = false;
            }
            else
            {
                desired = pendingCompanyChestNumericDesired != 0 ? pendingCompanyChestNumericDesired : Math.Max(min, max);
            }

            if (desired < min)
            {
                desired = min;
            }

            if (desired > max)
            {
                desired = max;
            }

            if (desired == 0 && min > 0)
            {
                desired = min;
            }

            pendingCompanyChestNumericDesired = desired;

            var beforeDefault = defaultValue->UInt;
            var beforeCurrent = DescribeInputNumericCurrent(currentValue);

            WriteInputNumericValue(defaultValue, currentValue, desired);
            TrySetInputNumericComponentValue(inputNumeric, desired);

            if (Configuration.DebugMode)
            {
                Svc.Log.Information($"[QuickTransfer] InputNumeric(Update): prompt='{prompt}', min={min}, max={max}, default {beforeDefault}->{defaultValue->UInt}, current {beforeCurrent}->{DescribeInputNumericCurrent(currentValue)} (idx5 type {currentValue->Type})");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeInputNumericCurrent(AtkValue* currentValue)
        => currentValue->Type == AtkValueType.UInt
            ? currentValue->UInt.ToString()
            : $"'{AtkValueHelpers.ReadStringOrEmpty(currentValue)}'";

    // Updating AtkValues alone does not refresh the visible text box, so write the component directly too.
    private static void TrySetInputNumericComponentValue(AtkUnitBase* inputNumeric, uint desired)
    {
        try
        {
            if (inputNumeric->UldManager.NodeList == null)
            {
                return;
            }

            var desiredStr = desired.ToString();
            for (var i = 0; i < inputNumeric->UldManager.NodeListCount; i++)
            {
                var node = inputNumeric->UldManager.NodeList[i];
                if (node == null || (int)node->Type < 1000)
                {
                    continue;
                }

                var comp = ((AtkComponentNode*)node)->Component;
                if (comp == null || comp->GetComponentType() != ComponentType.NumericInput)
                {
                    continue;
                }

                var ni = (AtkComponentNumericInput*)comp;
                AtkValueHelpers.WriteUtf8StringInPlace(&ni->RawString, desiredStr);
                AtkValueHelpers.WriteUtf8StringInPlace(&ni->EvaluatedString, desiredStr);
                ni->SetValue((int)desired);
                ni->CursorPos = (ushort)desiredStr.Length;
                ni->SelectionStart = ni->CursorPos;
                ni->SelectionEnd = ni->CursorPos;
                return;
            }
        }
        catch
        {
        }
    }
}
