using System.Collections.Generic;
using Glamourer.Api.Enums;

namespace Dresser;

// Gear put on from the catalog, to step back and forward through. Dyes aren't steps. In memory, per character.
public class History(Pins pins)
{
    // One click in the catalog: its slots before and after it. After is taken again on each step back and before on each
    // step forward, so back then forward returns to the same look, dyes changed in between included.
    private record Step(string Name, ApiEquipSlot[] Slots, Look[] Before, Look[] After);

    // One character's steps and how many of them are done; the ones after can be done again.
    private class Line
    {
        public readonly List<Step> Steps = [];
        public int Done;
    }

    private readonly Dictionary<ulong, Line> lines = [];

    private Line Current
    {
        get
        {
            var character = Plugin.PlayerState.ContentId;
            if (!lines.TryGetValue(character, out var line))
                lines[character] = line = new Line();
            return line;
        }
    }

    // The item put on by the step back or forward, null if there is none.
    internal string? BackName => Current is { Done: > 0 } line ? line.Steps[line.Done - 1].Name : null;
    internal string? ForwardName => Current is var line && line.Done < line.Steps.Count ? line.Steps[line.Done].Name : null;

    // The slots as they are now, null without a state. Throws when Glamourer is not available.
    internal Look[]? Take(ApiEquipSlot[] slots)
    {
        var looks = new Look[slots.Length];
        for (var i = 0; i < slots.Length; i++)
        {
            if (pins.Get(slots[i]) is not { } look)
                return null;
            looks[i] = look;
        }
        return looks;
    }

    // After putting an item on: a new step in place of the ones stepped back from. After is an array of its own even without
    // a state to read: a step back keeps the look it leaves there, and in before's array it would overwrite what it goes back to.
    internal void Add(string name, ApiEquipSlot[] slots, Look[] before)
    {
        var line = Current;
        line.Steps.RemoveRange(line.Done, line.Steps.Count - line.Done);
        line.Steps.Add(new Step(name, slots, before, Take(slots) ?? [.. before]));
        line.Done = line.Steps.Count;
    }

    // Throws when Glamourer is not available. A step Glamourer refused is passed all the same, so it can't block the rest.
    internal GlamourerApiEc Back()
    {
        var line = Current;
        var step = line.Steps[line.Done - 1];
        var result = Move(step.Slots, step.After, step.Before);
        line.Done--;
        return result;
    }

    internal GlamourerApiEc Forward()
    {
        var line = Current;
        var step = line.Steps[line.Done];
        var result = Move(step.Slots, step.Before, step.After);
        line.Done++;
        return result;
    }

    // Keeps what the slots show now in `now`, then puts `to` on them.
    private GlamourerApiEc Move(ApiEquipSlot[] slots, Look[] now, Look[] to)
    {
        var result = GlamourerApiEc.Success;
        for (var i = 0; i < slots.Length; i++)
        {
            now[i] = pins.Get(slots[i]) ?? now[i];
            var set = pins.Set(slots[i], to[i].Id, to[i].Stains);
            if (set != GlamourerApiEc.Success)
                result = set;
        }
        return result;
    }
}
