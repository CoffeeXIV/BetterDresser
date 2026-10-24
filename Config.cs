using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace Dresser;

[Serializable]
public class Config : IPluginConfiguration
{
    public int Version { get; set; }

    // Show model previews instead of game icons.
    public bool Previews { get; set; } = true;
    // The packs window has opened by itself once.
    public bool PacksOffered { get; set; }
    // Packs the packs window has listed, null before any list; any other is new.
    public HashSet<string>? KnownPacks { get; set; }
    // Preview tile width; the height follows the previews' shape.
    public int PreviewWidth { get; set; } = 200;
    public int IconSize { get; set; } = 48;
    internal const int MinPreviewWidth = 80, MaxPreviewWidth = 500, MinIconSize = 32, MaxIconSize = 128;
    public bool OwnedOnly { get; set; }
    // Online Store items, as the game's Dream Fitting list has them.
    public bool HideStore { get; set; }
    // A patch version or an expansion name; empty for all.
    public string Patch { get; set; } = string.Empty;
    // Items put on from the catalog keep the slot's dyes.
    public bool KeepDyesOnGearChange { get; set; }
    // Armoire item ids per character: the game loads the armoire only when it's opened.
    public Dictionary<ulong, HashSet<uint>> Armoire { get; set; } = [];

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
