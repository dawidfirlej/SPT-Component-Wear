# Component Wear for SPT

Component Wear gives replaceable barrels, upper receivers, and muzzle devices their own condition. A part keeps its wear when moved between compatible weapons.

**The code on `main` is version 1.1.2 and targets SPT 4.1.6.** Install both the client and server modules. SPT's Configuration Manager provides the in-game settings menu.

## Installation

1. Close the game and server and back up your SPT profiles.
2. Extract a release ZIP into the SPT installation folder, merging the `BepInEx` and `SPT_Runtime` directories. To use the version on `main`, follow the build instructions below. [GitHub Releases](https://github.com/dawidfirlej/SPT-Component-Wear/releases)
3. Start the server and then the game. Inspect a supported part to see its condition bar.

Existing parts with no saved condition start at 100/100. Keep your existing server `config.json` when upgrading if you've customized it.

## Mechanics

- Firing wears the equipped parts. Sustained overheating accelerates wear and can permanently reduce maximum condition.
- Barrel wear increases spread. Barrel and upper receiver wear add a limited contribution to the game's malfunction roll. Muzzle devices lose a portion of their own accuracy and recoil bonuses as they wear.
- A worn suppressor changes playback volume. With Sound Engine Backport Live, a severely worn suppressor can use the unsuppressed sound bank when available.
- Supported parts use the game's repair window. Repair an individual part with a trader or weapon repair kit. Permanent wear limits how much condition a repair can restore. For each point restored, the kit uses 15% of the base weapon resource cost for original categories and 6% for optional categories.
- Generated Flea Market offers can include used parts. On bots, part condition is sampled around the generated weapon's condition. Condition already assigned by another mod is preserved.
- Optional parts include magazines, gas blocks, charging handles, other receivers, underbarrel launchers, handguards, stocks, pistol grips, foregrips, bipods, iron sights, optics, tactical devices, mounts, and auxiliary parts. They wear more slowly than muzzle devices. Wear can reduce up to 20% of a part's applicable stat bonus, subject to weapon-level limits.

## Configuration and compatibility

On a local SPT installation, open **F12 → Component Wear** to enable optional categories individually and change their Flea Market and bot settings. Changes apply after restarting the SPT server and game. All optional categories start disabled.

The server reads `SPT_Runtime/user/mods/ComponentWear/config.json`. Use the included configuration as a starting point for manual edits. [Default config](config.json)

- `UsedFleaParts` and `BotPartCondition` are the main switches for generated Flea Market wear and bot part wear.
- `UsedOfferChance` controls the base probability for original categories and defaults to 0.25.
- `ExtraUsedFleaParts` and `ExtraUsedOfferChance` control used offers for enabled optional categories. The default chance is 25%.
- `ExtraBotPartCondition` and `ExtraBotWearChance` control condition rolls for enabled optional categories on bots. The default chance is 65% per eligible installed part. A roll uses the bot weapon's condition and can yield a fresh part.

The optional **Sound Engine Backport 1.1.1** integration was observed in game logs. Server startup and targeted fixtures were checked with **BetterFlea 2.6.2** installed. Bot part generation was observed in an installation running SVM, APBS, ORBIT, and SAIN. Supported categories and slots also cover parts supplied by other mods, including WTT content. These checks cover one modded installation. Fika, full SPT Realism, and a fresh SPT installation have not been verified.

## Building

Use the .NET 10 SDK and a local SPT 4.1.6 installation. The projects reference assemblies from that installation.

~~~powershell
dotnet build src/ComponentWear.csproj -c Release "-p:SptRoot=C:/Games/SPT"
dotnet build src/ComponentWear.Server.csproj -c Release "-p:SptRoot=C:/Games/SPT" "-p:BaseIntermediateOutputPath=obj/server/"
~~~

Copy the client DLL from `src/bin/Release/netstandard2.1` to `BepInEx/plugins/ComponentWear` and the server DLL from `src/bin/Release/net10.0` to `SPT_Runtime/user/mods/ComponentWear` in your SPT installation. On a first install, copy the included `config.json` next to the server DLL.

## Removal

Close the client and server. Remove the `ComponentWear` folders from `BepInEx/plugins` and `SPT_Runtime/user/mods`. Back up the profiles and Component Wear configuration before removing the mod if you want to retain the option to restore part condition.
