# YetAnotherTraderMod - Tony Trader

Server-side custom trader mod for SPT 4.0.11.

## Overview

YetAnotherTraderMod adds a new trader to SPT: **Tony Volkov**, a former BEAR operator turned back-room fixer with deep Russian underworld connections.

Tony operates through old criminal contacts, smugglers, ex-PMCs, and forgotten military supply routes. He deals in practical gear, weapons, armor, medical supplies, ammo, plates, and equipment for PMCs who can pay his price.

As of **v0.0.4**, Tony is no longer just a rename/rebrand pass. The mod now includes a working trader setup with four loyalty levels, purchasable inventory, barter offers, insurance support, and repair support.

## Current Trader Identity

* Name: Tony
* Surname: Volkov
* Nickname: Tony
* Location: A locked back room beneath Tarkov.
* Avatar: `data/Tony.jpg`
* Trader ID: `66a0f6b2c4d8e90123456789`

The trader ID is currently kept the same so existing assort, profile, and trader references do not break.

## Current Features

* Custom trader: Tony Volkov
* Four loyalty levels
* Mixed cash and barter inventory
* Purchasable weapons and weapon parts
* Armor, rigs, helmets, and armor plates
* Basic meds, food, and drinks
* Ammo options across progression
* Ruble purchase options
* Selected barter offers
* Insurance support
* Repair support
* Russian, scav, and black-market themed trader identity

## Loyalty Level Direction

### LL1

* Basic meds
* Food and drinks
* Low-tier ammo
* Low-tier armor and rigs
* Basic weapon parts
* Early PMC survival gear

### LL2

* Better ammo options
* Armor plates
* Improved weapon parts
* Mid-tier armor and equipment
* Practical attachments and supplies

### LL3

* Better weapons
* Mid-tier and higher-value gear
* Stronger armor options
* Rare barter items
* More specialized equipment

### LL4

* High-end ammo
* Better rigs and armor
* Stronger weapons
* Exclusive kits and late-game gear options
* Limited high-value equipment

## Requirements

* SPT 4.0.11 server
* .NET 9 SDK for building from source
* Windows recommended

## Build

```powershell
dotnet build -c Release
```

The build output will be placed in:

```text
bin\Release\
```

## Install

1. Close the SPT server.
2. Download or build the mod.
3. Copy the mod folder into:

```text
<SPT>\user\mods\YetAnotherTraderMod
```

The final installed path should look like:

```text
SPT/user/mods/YetAnotherTraderMod
```

The installed folder should contain the mod DLL, package file, data files, and config files, including:

```text
package.json
data\base.json
data\assort.json
data\Tony.jpg
config\settings.json
config\items.json
```

Depending on the build/package setup, the compiled DLL should also be present in the root of the mod folder.

## Planned Features

The following features are planned for future versions and are not current v0.0.4 content:

* Full Tony questline
* Quest-based trader progression
* Quest-locked inventory unlocks
* Side quests tied to cheaper deals
* Barter-to-cash unlocks after quest completion
* More custom quest items
* More Tony-themed weapon presets
* Special shady item unlocks
* Themed gear bundles
* Expanded lore and trader messages
* Better long-term inventory balance
* More custom weapons and modded gear support

## Planned Quest Direction

Tony’s future questline will focus on his criminal network, old BEAR routes, blackmail, dead drops, supply caches, and favors owed across Tarkov.

Planned quest themes include:

* Getting introduced to Tony through Fence
* Recovering Tony’s old ledger
* Moving blackmail through dead drops
* Planting Tony’s calling card
* Collecting street tax
* Following old BEAR trails
* Recovering buried military crates
* Marking forgotten supply caches
* Unlocking cheaper parts, ammo, suppressors, and special deals

## Notes

This is a **beta release**. Inventory, balance, prices, trader services, and progression are subject to change.

Quest unlocks, full quest progression, side quests, and advanced trader progression are planned for later versions.

## Credits

* Original Priscilu Origins foundation: Reis
* Update/contributor foundation: Anigx
* Tony concept, code, and current development: AlMightyTank
* Thanks to the SPT community for ongoing support
