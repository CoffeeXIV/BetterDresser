# BetterDresser

BetterDresser is a catalog of every piece of gear in FFXIV. Browse it by slot, see a preview of each item's model,
and click to try it on your character with [Glamourer](https://github.com/Ottermandias/Glamourer). It is made for putting
glamours together: no more digging through the armoury chest, the dresser or wiki pages to see what something looks like.

## Installation

BetterDresser is a Dalamud plugin, so the game has to be started with [XIVLauncher](https://goatcorp.github.io/).

**BetterDresser doesn't work on its own: it needs [Penumbra](https://github.com/xivdev/Penumbra) and
[Glamourer](https://github.com/Ottermandias/Glamourer) installed.** Glamourer is what puts the items on your character,
and Penumbra is the mod loader Glamourer runs on. The easiest way to get both is the
[Sea of Stars](https://github.com/Ottermandias/SeaOfStars) repository, which has them and other customization plugins
in one place: add it together with the BetterDresser repository.

1. Type `/xlsettings` in the chat box to open the Dalamud settings.
2. Go to the "Experimental" tab and scroll down to "Custom Plugin Repositories".
3. Paste each repo URL below into an empty field, and press the "+" button next to it. Make sure both are ticked as enabled.
4. Press "Save and Close".
5. Open the plugin installer with `/xlplugins`, then search for and install Penumbra, Glamourer and BetterDresser.

#### Repo URLs

BetterDresser:

`https://raw.githubusercontent.com/CoffeeXIV/BetterDresser/main/repo.json`

Sea of Stars, for Penumbra and Glamourer (skip it if you have them already):

`https://raw.githubusercontent.com/Ottermandias/SeaOfStars/main/repo.json`

#### What it looks like

<p align="center"><img src="images/preview.jpg" width="600" alt="The BetterDresser catalog: the Body tab with previews of the items"></p>

## Usage

Open the catalog with `/bdresser` or `/betterdresser`.

- Gear is sorted into tabs by slot, newest items first. Weapons show only the ones your current job can use.
- Click an item to put it on. Right click copies its name, Shift+right click opens the game's "Search for Item" for it.
- Save preset to Glamourer saves what you wear as a new Glamourer design and opens it in Glamourer. Only the gear
  is applied from it, not your character's appearance, so it can go on any character.
- Works in GPose too.

### Filters

- Search by name.
- Owned: only the items this character has, in the bags, armoury chest, equipped, with retainers, in the saddlebags,
  the glamour dresser and the armoire. Retainers, saddlebags and the dresser count once you have opened them;
  the armoire, once you have opened it at an inn.
- Hide store: hides Online Store items, which are marked with a yellow `$`. The list comes from the game's
  Dream Fitting, which misses some store items.
- Filter by patch: only items added in a given patch or expansion.

### Previews

The previews come in packs: male clothes, female clothes, accessories and weapons. They are downloaded from the releases
of [BetterDresser-Packs](https://github.com/CoffeeXIV/BetterDresser-Packs) in the packs window.

Game patches bring new gear, and new previews with it. The bottom bar tells you when an update or a new pack is out,
and only the changed previews are downloaded.

### Network

BetterDresser asks GitHub for the list of preview pack releases when it loads and when you open the packs window
(at most once in five minutes), and downloads packs only when you press Download.

## Acknowledgements

- [Glamourer](https://github.com/Ottermandias/Glamourer) by Ottermandias, which does all the dressing.
- [Penumbra](https://github.com/xivdev/Penumbra), the mod loader Glamourer runs on.
- [xivapi/ffxiv-datamining-patches](https://github.com/xivapi/ffxiv-datamining-patches) for the patch each item came with.
- [Dalamud](https://github.com/goatcorp/Dalamud) and [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs).
