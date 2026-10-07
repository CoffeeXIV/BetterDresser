using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace Dresser;

[Serializable]
public class Config : IPluginConfiguration
{
    public int Version { get; set; }

    // Show model screenshots instead of game icons.
    public bool Screenshots { get; set; } = true;
    // The screenshot packs window has been shown once, on the first open of the main window.
    public bool PacksOffered { get; set; }
    // Screenshot tile width; the height follows the shots' shape.
    public int ShotWidth { get; set; } = 160;
    public int IconSize { get; set; } = 48;
    internal const int MinShotWidth = 80, MaxShotWidth = 500, MinIconSize = 32, MaxIconSize = 128;
    // Show only the items the current character has somewhere.
    public bool OwnedOnly { get; set; }
    // Hide items sold in the Online Store (as far as the game's Dream Fitting list knows).
    public bool HideStore { get; set; }
    // Show only the items added in this patch version or expansion (by name); empty for all.
    public string Patch { get; set; } = string.Empty;
    // Items put on from the catalog keep the slot's current dyes instead of dropping them.
    public bool KeepDyes { get; set; } = true;
    // Armoire item ids per character content id: the game loads the armoire only when it is opened.
    public Dictionary<ulong, HashSet<uint>> Armoire { get; set; } = [];

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
