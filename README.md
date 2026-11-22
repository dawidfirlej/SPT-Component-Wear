# Component Wear for SPT

Component Wear gives replaceable barrels, upper receivers, and muzzle devices their own condition. A part keeps its wear when moved between compatible weapons.

**Version 1.0.0 targets SPT 4.1.6.** Install both the included client and server modules. No other mods are required.

## Installation

1. Close the game and server and back up your SPT profiles.
2. Extract [ComponentWear-1.0.0.zip](https://github.com/dawidfirlej/SPT-Component-Wear/releases/download/v1.0.0/ComponentWear-1.0.0.zip) into the SPT installation folder, merging the `BepInEx` and `SPT_Runtime` directories.
3. Start the server and then the game. Inspect a supported part to see its condition bar.

Existing parts with no saved condition start at 100/100. Keep your existing `BepInEx/config/local.component.wear.cfg` and server `config.json` when upgrading if you have customized them.

## Mechanics

- Firing wears the equipped parts. Sustained overheating accelerates wear and can permanently reduce maximum condition.
- Barrel wear increases spread. Barrel and upper receiver wear add a limited contribution to the game's malfunction roll. Muzzle devices lose a portion of their own accuracy and recoil bonuses as they wear.
- A worn suppressor changes playback volume. With Sound Engine Backport Live, a severely worn suppressor can use the unsuppressed sound bank when available.
- Supported parts use the game's repair window. Repair an individual part with a trader or weapon repair kit. Permanent wear limits how much condition a repair can restore. By default, repairing a point of part condition consumes 15% of the weapon kit resource required for a point on the base weapon.
- Generated Flea Market offers can include used parts. On bots, part condition is sampled around the generated weapon's condition. Condition already assigned by another mod is preserved.

## Configuration and compatibility

Server settings are in `SPT_Runtime/user/mods/ComponentWear/config.json`. Set `UsedFleaParts` or `BotPartCondition` to `false` to disable those features. `UsedOfferChance` is the base chance for a generated offer to contain a used part and defaults to 0.25.

The optional **Sound Engine Backport 1.1.1** integration was observed in game logs. Server startup and targeted fixtures were checked with **BetterFlea 2.6.2** installed. Bot part generation was observed in an installation running SVM, APBS, ORBIT, and SAIN. Supported categories and slots also cover parts supplied by other mods, including WTT content. These checks cover one modded installation. Fika, full SPT Realism, and a fresh SPT installation have not been verified.

## Building

Use the .NET 10 SDK and a local SPT 4.1.6 installation. The projects reference assemblies from that installation.

~~~powershell
dotnet build src/ComponentWear.csproj -c Release "-p:SptRoot=C:/Games/SPT"
dotnet build src/ComponentWear.Server.csproj -c Release "-p:SptRoot=C:/Games/SPT" "-p:BaseIntermediateOutputPath=obj/server/"
~~~

Client output targets `netstandard2.1`; server output targets `net10.0`.

## Removal

Close the client and server. Remove the `ComponentWear` folders from `BepInEx/plugins` and `SPT_Runtime/user/mods`. Back up the profiles and Component Wear configuration before removing the mod if you want to retain the option to restore part condition.
