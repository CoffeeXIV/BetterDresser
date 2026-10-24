using System.Collections.Generic;
using Glamourer.Api.Enums;

namespace Dresser;

// Gear and dye changes to step back and forward through. In memory, per character.
public class History(Pins pins)
{
    // One change: its slots before and after. Each step re-reads the side it leaves, so back then forward keeps later dye changes.
    private record Step(string Name, ApiEquipSlot[] Slots, Look[] Before, Look[] After);

    // One character's steps and how many are done.
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

    // Names of the steps back and forward, null if none.
    internal string? BackName => Current is { Done: > 0 } line ? line.Steps[line.Done - 1].Name : null;
    internal string? ForwardName => Current is var line && line.Done < line.Steps.Count ? line.Steps[line.Done].Name : null;

    // The slots as they are now, null without a state. Throws when Glamourer is not available.
    internal Look[]? Take(ApiEquipSlot[] slots) => pins.Get(slots);

    // A new step in place of the ones stepped back from. After is always its own array: Move writes into it.
    internal void Add(string name, ApiEquipSlot[] slots, Look[] before)
    {
        var line = Current;
        line.Steps.RemoveRange(line.Done, line.Steps.Count - line.Done);
        line.Steps.Add(new Step(name, slots, before, Take(slots) ?? [.. before]));
        line.Done = line.Steps.Count;
    }

    // Throws when Glamourer is not available. A refused step is passed anyway, so it can't block the rest.
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

    // Saves the slots into `now` before any changes, then puts `to` on.
    private GlamourerApiEc Move(ApiEquipSlot[] slots, Look[] now, Look[] to)
    {
        if (pins.Get(slots) is { } current)
            current.CopyTo(now, 0);

        var result = GlamourerApiEc.Success;
        for (var i = 0; i < slots.Length; i++)
        {
            var set = pins.Set(slots[i], to[i].Id, to[i].Stains);
            if (set != GlamourerApiEc.Success)
                result = set;
        }
        return result;
    }
}
