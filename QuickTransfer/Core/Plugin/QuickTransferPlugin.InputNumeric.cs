using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace QuickTransfer;

public sealed unsafe partial class QuickTransferPlugin
{
    private void OnInputNumericPreSetup(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!Configuration.EnableCompanyChest || pendingNumericKind == PendingNumericKind.None)
            {
                return;
            }

            if (!string.Equals(args.AddonName, QuickTransferConstants.InputNumericAddonName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!InventoryHelpers.IsCompanyChestOpen())
            {
                return;
            }

            if (args is not AddonSetupArgs setup)
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

            var prompt = values[6].Type is AtkValueType.String or AtkValueType.ManagedString ? AtkValueHelpers.ReadAtkValueString(values[6]) : string.Empty;
            if (pendingNumericKind == PendingNumericKind.Store && !prompt.Contains("store", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (pendingNumericKind == PendingNumericKind.Remove && !prompt.Contains("remove", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (pendingNumericKind == PendingNumericKind.Sell && !prompt.Contains("sell", StringComparison.OrdinalIgnoreCase))
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
            var desired = max < min ? min : max;

            if (Configuration.DebugMode)
            {
                var curStr = values[5].Type == AtkValueType.UInt ? values[5].UInt.ToString() : "n/a";
                Svc.Log.Information($"[QuickTransfer] InputNumeric PreSetup: min={min}, max={max}, default={values[4].UInt}, current={curStr}");
            }

            values[4].UInt = desired;
            switch (values[5].Type)
            {
                case AtkValueType.UInt:
                    values[5].UInt = desired;
                    break;
                case AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString:
                    AtkValueHelpers.WriteUtf8InPlace(values[5].String, desired.ToString());
                    break;
            }

            if (Configuration.DebugMode)
            {
                Svc.Log.Information($"[QuickTransfer] InputNumeric PreSetup: prompt='{prompt}', min={min}, max={max}, setDefault={desired}");
            }
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
            if (inputNumeric == null)
            {
                return false;
            }
            if (inputNumeric->AtkValues == null || inputNumeric->AtkValuesCount < 7)
            {
                return false;
            }

            var minValue = inputNumeric->AtkValues + 2;
            var maxValue = inputNumeric->AtkValues + 3;
            var defaultValue = inputNumeric->AtkValues + 4;
            var currentValue = inputNumeric->AtkValuesCount > 5 ? (inputNumeric->AtkValues + 5) : null;
            var promptVal = inputNumeric->AtkValues + 6;
            var prompt = promptVal->Type is AtkValueType.String or AtkValueType.ManagedString ? AtkValueHelpers.ReadAtkValueString(*promptVal) : string.Empty;

            if (kind == PendingNumericKind.Store && !prompt.Contains("store", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (kind == PendingNumericKind.Remove && !prompt.Contains("remove", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (kind == PendingNumericKind.Trade && !prompt.Contains("trade", StringComparison.OrdinalIgnoreCase) && !InventoryHelpers.IsTradeOpen())
            {
                return false;
            }
            if (kind == PendingNumericKind.Sell && !prompt.Contains("sell", StringComparison.OrdinalIgnoreCase) && !InventoryHelpers.IsVendorOpen())
            {
                return false;
            }

            if (minValue->Type != AtkValueType.UInt || maxValue->Type != AtkValueType.UInt || defaultValue->Type != AtkValueType.UInt)
            {
                return false;
            }

            var min = minValue->UInt;
            var max = maxValue->UInt;

            if (kind == PendingNumericKind.Split && !prompt.Contains("split", StringComparison.OrdinalIgnoreCase))
            {
                var nowMs = Environment.TickCount64;
                var expectedMax = pendingSplitExpectedMax;
                var okByExpected = expectedMax != 0 && nowMs <= pendingSplitExpectedUntilMs && max == expectedMax;
                if (!okByExpected)
                {
                    return false;
                }
            }
            uint desired;
            if (pendingCompanyChestNumericHalf)
            {
                if (kind == PendingNumericKind.Remove && max <= 1)
                {
                    return false;
                }
                if (kind == PendingNumericKind.Split && max == 0)
                {
                    return false;
                }
                desired = kind == PendingNumericKind.Remove ? (max / 2) : ((max + 1) / 2);
                pendingCompanyChestNumericHalf = false;
            }
            else if (pendingCompanyChestNumericDesired != 0)
            {
                desired = pendingCompanyChestNumericDesired;
            }
            else
            {
                desired = max < min ? min : max;
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
            var beforeCurrentUInt = (currentValue != null && currentValue->Type == AtkValueType.UInt) ? currentValue->UInt : 0U;
            var beforeCurrentStr = (currentValue != null && currentValue->Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString)
                ? AtkValueHelpers.ReadAtkValueString(*currentValue)
                : string.Empty;

            defaultValue->UInt = desired;
            if (currentValue != null)
            {
                if (currentValue->Type == AtkValueType.UInt)
                {
                    currentValue->UInt = desired;
                }
                else if (currentValue->Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString)
                {
                    AtkValueHelpers.WriteUtf8InPlace(currentValue->String, desired.ToString());
                }
            }

            TrySetInputNumericComponentValue(inputNumeric, desired);

            if (Configuration.DebugMode)
            {
                var curType = currentValue != null ? currentValue->Type.ToString() : "n/a";
                var afterCurrentUInt = (currentValue != null && currentValue->Type == AtkValueType.UInt) ? currentValue->UInt : 0U;
                var afterCurrentStr = (currentValue != null && currentValue->Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString)
                    ? AtkValueHelpers.ReadAtkValueString(*currentValue)
                    : string.Empty;
                Svc.Log.Information($"[QuickTransfer] InputNumeric(Update): prompt='{prompt}', min={min}, max={max}, default {beforeDefault}->{defaultValue->UInt}, currentUInt {beforeCurrentUInt}->{afterCurrentUInt}, currentStr '{beforeCurrentStr}'->'{afterCurrentStr}' (idx5 type {curType})");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TrySetInputNumericComponentValue(AtkUnitBase* inputNumeric, uint desired)
    {
        try
        {
            if (inputNumeric == null)
            {
                return;
            }
            if (inputNumeric->UldManager.NodeList == null)
            {
                return;
            }

            var desiredStr = desired.ToString();

            for (var i = 0; i < inputNumeric->UldManager.NodeListCount; i++)
            {
                var node = inputNumeric->UldManager.NodeList[i];
                if (node == null)
                {
                    continue;
                }

                if ((int)node->Type < 1000)
                {
                    continue;
                }

                var compNode = (AtkComponentNode*)node;
                var comp = compNode->Component;
                if (comp == null)
                {
                    continue;
                }

                if (comp->GetComponentType() != ComponentType.NumericInput)
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
