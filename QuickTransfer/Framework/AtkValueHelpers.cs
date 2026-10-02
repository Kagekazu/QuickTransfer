using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Runtime.InteropServices;
using System.Text;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace QuickTransfer.Framework;

internal static unsafe class AtkValueHelpers
{
    public static bool IsString(AtkValueType type)
        => type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString;

    public static string ReadAtkValueString(AtkValue v)
    {
        if ((byte*)v.String == null)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUTF8(new(v.String)) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // Returns an empty string for non-string values.
    public static string ReadStringOrEmpty(AtkValue* v)
        => v != null && IsString(v->Type) ? ReadAtkValueString(*v) : string.Empty;

    public static void WriteUtf8InPlace(byte* dst, string value)
    {
        if (dst == null || string.IsNullOrEmpty(value))
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var max = Math.Min(bytes.Length, 255);
        for (var i = 0; i < max; i++)
        {
            dst[i] = bytes[i];
        }

        dst[max] = 0;
    }

    public static void WriteUtf8StringInPlace(Utf8String* s, string value)
    {
        if (s == null)
        {
            return;
        }

        WriteUtf8InPlace(s->StringPtr, value);
        s->StringLength = value.Length;
        s->BufUsed = value.Length + 1;
    }

    public static void GenerateCallback(AtkUnitBase* unitBase, params object[] values)
    {
        var atkValues = (AtkValue*)Marshal.AllocHGlobal(values.Length * sizeof(AtkValue));
        var stringAllocs = new List<nint>();
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                switch (values[i])
                {
                    case uint u:
                        atkValues[i].Type = AtkValueType.UInt;
                        atkValues[i].UInt = u;
                        break;
                    case int n:
                        atkValues[i].Type = AtkValueType.Int;
                        atkValues[i].Int = n;
                        break;
                    case bool b:
                        atkValues[i].Type = AtkValueType.Bool;
                        atkValues[i].Byte = (byte)(b ? 1 : 0);
                        break;
                    case string s:
                        var str = Marshal.StringToCoTaskMemUTF8(s);
                        stringAllocs.Add(str);
                        atkValues[i].Type = AtkValueType.String;
                        atkValues[i].String = (byte*)str;
                        break;
                    default:
                        throw new ArgumentException($"Unsupported AtkValue type {values[i].GetType()}");
                }
            }

            unitBase->FireCallback((uint)values.Length, atkValues);
        }
        finally
        {
            foreach (var str in stringAllocs)
            {
                Marshal.FreeCoTaskMem(str);
            }

            Marshal.FreeHGlobal((nint)atkValues);
        }
    }

    public static bool TryGetAtkValueInt(AtkValue* values, int count, int idx, out int value)
    {
        value = 0;
        if (values == null || idx < 0 || idx >= count)
        {
            return false;
        }

        var v = values + idx;
        switch (v->Type)
        {
            case AtkValueType.Int:
                value = v->Int;
                return true;
            case AtkValueType.UInt:
                value = unchecked((int)v->UInt);
                return true;
            default:
                return false;
        }
    }

    // Hides an addon without closing it, so it can still be driven via callbacks.
    public static void SetAddonAlpha(AtkUnitBase* addon, byte alpha)
    {
        if (addon == null || addon->RootNode == null)
        {
            return;
        }

        addon->RootNode->Color.A = alpha;
        addon->RootNode->Alpha_2 = alpha;
    }
}
