using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc.Exceptions;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace Dresser;

// A slot as SetItem takes it: the id and the dyes.
// A list, not byte[]: Dalamud IPC passes it through JSON, where byte[] turns into a base64 string that SetItem can't read.
internal record Look(ulong Id, List<byte> Stains);

// Gear put on from Dresser is fixed in Glamourer while the catalog is open, so a job or gearset change keeps it.
// As the catalog closes it turns temporary (Once) again: the look stays, and the next gear change in the slot takes it off as usual.
public class Pins
{
    // Glamourer's CustomItemId flag: the id is a model (primary | secondary << 16 | variant << 32), not an item row.
    private const ulong CustomItemFlag = 1ul << 48;
    // Any error but Glamourer missing: the details are in the log.
    private const string Failed = "Something went wrong, see /xllog.";

    private readonly SetItem setItem = new(Plugin.PluginInterface);
    private readonly GetState getState = new(Plugin.PluginInterface);
    // Fixed slots: the id given to Glamourer and the item id it shows in its state (it turns a custom model id into an item).
    private readonly Dictionary<(int Actor, ApiEquipSlot Slot), (ulong Id, ulong ItemId)> pinned = [];
    private long stateAt;

    // The catalog is open: set by its window.
    internal bool Active { get; set; }

    // The character's state for the windows to show, one read for all of them; null when it can't be read, and why.
    internal JObject? State { get; private set; }
    internal string StateError { get; private set; } = string.Empty;

    // Twice a second is enough to pick up changes from the windows, the game or Glamourer: each read is the whole state.
    internal void RefreshState()
    {
        if (Environment.TickCount64 - stateAt < 500)
            return;

        stateAt = Environment.TickCount64;
        try
        {
            var (result, data) = getState.Invoke(Plugin.Actor);
            State = result == GlamourerApiEc.Success ? data : null;
            StateError = result == GlamourerApiEc.Success ? string.Empty : $"Glamourer: {result}";
        }
        catch (IpcNotReadyError)
        {
            State = null;
            StateError = "Glamourer is not available.";
        }
        // Logged once, not on each read twice a second.
        catch (Exception ex)
        {
            State = null;
            if (StateError != Failed)
                Plugin.Log.Warning(ex, "GetState failed");
            StateError = Failed;
        }
    }

    // After a change from a window: the state is read again on the next frame.
    internal void ReadStateSoon() => stateAt = 0;

    // The status for a call to Glamourer that threw: Glamourer missing, or anything else, which goes to the log.
    internal static string ErrorText(Exception ex, string what)
    {
        if (ex is IpcNotReadyError)
            return "Glamourer is not available.";

        Plugin.Log.Warning(ex, what);
        return Failed;
    }

    // Throws when Glamourer is not available.
    internal GlamourerApiEc Set(ApiEquipSlot slot, ulong id, List<byte> stains)
    {
        var actor = Plugin.Actor;
        // Without Once, Glamourer keeps the item over the game's gear changes.
        var result = setItem.Invoke(actor, slot, id, stains, 0, Active ? default : ApplyFlag.Once);
        if (result == GlamourerApiEc.Success && Active && (ulong?)Equipment(actor, slot)?["ItemId"] is { } item)
            pinned[(actor, slot)] = (id, item);
        return result;
    }

    // The slots as Glamourer shows them now, from one read of its state: a read is the whole state, whatever is taken from it.
    // Null without a state or a slot in it. Throws when Glamourer is not available.
    internal Look[]? Get(ApiEquipSlot[] slots)
    {
        var (result, state) = getState.Invoke(Plugin.Actor);
        if (result != GlamourerApiEc.Success)
            return null;

        var looks = new Look[slots.Length];
        for (var i = 0; i < slots.Length; i++)
        {
            if (state?["Equipment"]?[slots[i].ToString()] is not { } equip || (ulong?)equip["ItemId"] is not { } item)
                return null;
            looks[i] = new Look(IdOf(slots[i], item), Stains(equip));
        }
        return looks;
    }

    // The id SetItem takes for an item id from Glamourer's state. Glamourer resolves off-hand item ids only among secondary models,
    // so off-hand-only items (shields, tools) go as a custom model id. Its own ids (empty slot, custom model) go as they are.
    internal static ulong IdOf(ApiEquipSlot slot, ulong item) =>
        slot == ApiEquipSlot.OffHand && item <= uint.MaxValue
        && Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault((uint)item) is { } row && row.EquipSlotCategory.Value.OffHand == 1
            ? row.ModelMain | CustomItemFlag
            : item;

    // The same items again with their current dyes, now with Once. Slots changed since are left alone:
    // in Glamourer by hand, or a weapon the new job can't use, which Glamourer swaps back to the game's.
    internal void Release()
    {
        foreach (var ((actor, slot), (id, item)) in pinned)
        {
            try
            {
                if (Equipment(actor, slot) is { } equip && (ulong?)equip["ItemId"] == item)
                    setItem.Invoke(actor, slot, id, Stains(equip), 0, ApplyFlag.Once);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Releasing a fixed item failed");
            }
        }
        pinned.Clear();
    }

    // Glamourer's state keys for slots match ApiEquipSlot names.
    private JToken? Equipment(int actor, ApiEquipSlot slot)
    {
        var (result, state) = getState.Invoke(actor);
        return result == GlamourerApiEc.Success ? state?["Equipment"]?[slot.ToString()] : null;
    }

    // The dyes of a slot in Glamourer's state, as SetItem takes them.
    internal static List<byte> Stains(JToken equip) => [(byte?)equip["Stain"] ?? 0, (byte?)equip["Stain2"] ?? 0];
}
