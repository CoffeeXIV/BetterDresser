using System;
using System.Collections.Generic;
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

    private readonly SetItem setItem = new(Plugin.PluginInterface);
    private readonly GetState getState = new(Plugin.PluginInterface);
    // Fixed slots: the id given to Glamourer and the item id it shows in its state (it turns a custom model id into an item).
    private readonly Dictionary<(int Actor, ApiEquipSlot Slot), (ulong Id, ulong ItemId)> pinned = [];

    // The catalog is open: set by its window.
    internal bool Active { get; set; }

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

    // The slot as Glamourer shows it now, null without a state. Throws when Glamourer is not available.
    internal Look? Get(ApiEquipSlot slot) =>
        Equipment(Plugin.Actor, slot) is { } equip && (ulong?)equip["ItemId"] is { } item ? new Look(IdOf(slot, item), Stains(equip)) : null;

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
