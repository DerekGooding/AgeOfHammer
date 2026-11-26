# Ability scripts

The files here make faction abilities actually work in the engine. Each file is named `<faction-id>.json` and is applied to that faction's data **when it loads**, so there's no re-import step: edit a file, run the game or the tests, and the change takes effect.

The patch format is the same as `data/overrides` (see `AgeOfHammer.Core/Data/DataPatch.cs`):

```jsonc
{
  "abilities": {                     // every ability with this name in the faction (warscrolls, battle traits, rule groups)
    "Beast": { "modifiers": [ { "stat": "MaxControl", "value": 1 } ] }
  },
  "warscrolls": {                    // one warscroll's abilities or spells
    "krondys-son-of-dracothion": {
      "abilities": { "Battle Damaged": { "modifiers": [ ... ] } },
      "spells": { "Atavistic Tempest": { "effect": { ... } } }
    }
  }
}
```

If a script names an ability that doesn't exist, the faction loads with a warning, and `ScriptDataTests` fails on it. To see how much of each army is automated, run:

```
dotnet run --project tools/AgeOfHammer.Balance -- --coverage
```

Mark simplifications with an `// APPROX:` comment so they can be found and improved later.

## Passive modifiers: `"modifiers": [ ... ]`

| Field | Meaning |
|---|---|
| `stat` | See the stats table below. |
| `value` | The amount added. For Ward and MaxControl, the threshold or cap itself. |
| `multiply` | Multiply the stat instead of adding to it, e.g. `2` for "double the Damage characteristic". |
| `attackType` | `Any`, `Melee` or `Ranged`. |
| `weapon` | Only applies to weapons whose name contains this text, e.g. `"Thunderaxe"`. |
| `condition` | Condition terms joined with `&`; see the list below. |
| `aura` | Without an aura, the modifier affects this unit. With one, it affects every unit inside the aura: `{ "side": "friendly" or "enemy", "range": 13, "wholly": true, "keywords": [...], "keywordsAny": [...], "names": [...], "includeSelf": true }`. Leave out `range` to cover the whole battlefield. |

### Stats

| Stat | Effect |
|---|---|
| `Hit`, `Wound`, `Rend`, `Attacks`, `Damage`, `Save` | Modify that characteristic or roll. |
| `Move`, `Control`, `Charge`, `Cast` | Modify that characteristic or roll. |
| `Ward` | Gives a ward save of that value; the lowest wins. |
| `WardRoll` | Modifies ward rolls. |
| `HitAgainst`, `WoundAgainst` | Modify the hit or wound rolls of attacks that target this unit. |
| `UnmodifiedHitFailsAgainst` | Attacks that target this unit fail on an unmodified hit roll of this value or less. |
| `MaxControl` | Caps the control score. |
| `ChargeDice` | Extra charge dice, to a maximum of 3. |
| `CoherencyRange` | Coherency range in inches. |
| `StrikeFirst`, `StrikeLast` | Flags (value 1). |
| `NoCommands` | Flag: the unit can't use or receive commands. |
| `NoRetreatDamage` | Flag: retreating inflicts no mortal damage. |
| `ShootAfterRun`, `ChargeAfterRun` | Flags: can shoot or charge after running or retreating. |
| `SaveSixMortal` | Defensive: each unmodified save roll of 6 inflicts `value` mortal damage on the attacker (Extremis Chamber). |
| `SlainMortalOn` | Defensive: for each model slain by combat attacks, a dice roll of `value`+ inflicts 1 mortal damage on the attacker (Spreaders of Filth). |
| `HitMortal` | Weapon: each hit also inflicts `amount` mortal damage, e.g. `"D3"`. |
| `CritHits` | Weapon: each critical hit scores `amount` hits, e.g. `"D6"`. |
| `HitsAreMortal` | Flag, weapon: each hit inflicts 1 mortal damage and the attack sequence ends. |
| `ResistAbilities`, `ResistSpells` | Enemy abilities or spells that target this unit are ignored on a roll of `value`+. Add `"oncePerTurn": true` for Ruination Chamber. |
| `Chant` | Modifies chanting rolls. |
| `ChargeReroll` | Flag: a failed charge roll is re-rolled for free. |
| `ExtraFight` | Usually given as a buff: the unit fights once more this phase, then has STRIKE-LAST. |

### Conditions

In these terms, "self" is the unit that has the ability, "affected" is the unit being modified, and "target" is the other unit in the attack or ability.

| Applies to | Terms |
|---|---|
| Self | `charged`, `notCharged`, `chargedThisPhase`, `damaged`, `damagePoints>=N`, `damagePoints<N`, `inCombat`, `notInCombat`, `notMoved`, `engagedWithCharger`, `contesting`, `contestingFriendlyTerritory` |
| Affected unit | `keyword:X`, `keywordAny:A\|B` |
| Target | `targetModels>=N`, `targetKeyword:X`, `targetHealth>=N`, `targetCharged`, `targetDamaged`, `targetNotFly`, `targetWithin:N` (the target is within N" of self) |

An unknown term is treated as false, so a typo never silently grants a bonus.

## Activated abilities: `"activate": { ... }`

The ability's timing (phase, and whose turn) and how often it can be used come from the imported data, e.g. "Once Per Turn (Army)". The active player uses abilities during their own phases. In the combat phase, whoever is picking can use their units' abilities. At the end of each phase, the other player gets a window for their abilities. Abilities marked `auto` are resolved by the engine when their phase starts.

| Field | Meaning |
|---|---|
| `target` | `self`, `enemy` or `friendly`. |
| `range`, `wholly`, `inCombat` | Where targets must be. |
| `targetKeywords`, `targetKeywordsAny`, `targetExcludeKeywords`, `targetNames` | Filters on which units can be targets. |
| `other` | Excludes the user from friendly targets. |
| `maxTargets` | Up to N targets; `0` means every eligible unit. The engine fills in extra targets itself. |
| `requires` | Conditions on the user, e.g. `"chargedThisPhase"`. |
| `targetRequires` | Conditions on each target, e.g. `"targetCharged"`. |
| `roll` and `success` | A gate, e.g. `"D6"` and `3` means "roll a dice; on a 3+...". |
| `perTarget` | Roll the gate separately for each target. |
| `rollCount` | `"models"` rolls one gate dice per model in the user's unit, and the best roll counts ("if any roll is a 6"). |

**Prayers.** Abilities imported with a prayer cost, such as Lightning Tempest (chanting value 5), are chanted by Priests. The engine makes a chanting roll of D6 plus `Chant` modifiers against the chanting value, and an answered prayer's effects use that roll as `"roll"`. Each prayer can be used once per turn per army, and a Priest can chant up to their PRIEST (N) level per phase.
*APPROX: ritual points are not modelled yet; check against the core rules.*
| `effects` | The list of effects; see below. |
| `auto` | The engine uses the ability itself, e.g. for end-of-turn heals. |

### Effects

These are also used by spells (`"effect"` plus `"effects"`).

| `type` | Fields | Effect |
|---|---|---|
| `mortal` | `amount` (`"D3"`, `"2"` or `"roll"`) | Mortal damage. `"roll"` uses the gate roll's result. |
| `mortalPerModel` | `threshold` | Roll a dice per model in the recipient; each roll at or above the threshold inflicts 1 mortal damage. |
| `mortalDice` | `amount`, `threshold` | Roll `amount` dice; each roll at or above the threshold inflicts 1 mortal damage. |
| `slayRoll` | `amount`, `healthOffset` | Roll `amount` dice; each roll of at least Health + offset slays a model. |
| `heal` | `amount` | Heal that many damage points. |
| `returnModels` | `amount` | Return slain models. |
| `buff` | `stat`, `value`, `attackType`, `weapon`, `expiry` | Add `value` to `stat`. `expiry` is `EndOfPhase`, `EndOfTurn` (the default), `EndOfRound`, `UntilYourNextTurn` or `Permanent`. |

| `table` | `dice`, `table` | Roll `dice` and apply the matching row: `[ { "min": 1, "max": 1, "label": "...", "effects": [...] }, ... ]`. |
| `move` | `amount` (dice, or `"Move"`), `moveKind` | The recipient may move: `normal`, or `retreat` (may leave combat, no mortal damage). |
| `setUp` | `minEnemyDistance`, `withinOfUser` | The recipient is removed and set up again. |

Any effect can include `"to": "self"` to apply to the user instead of the target. An effect with `"area": 13` (and `"side": "enemy"` or `"friendly"`) applies to every unit of that side within 13" of the user instead of the target.

## Follow-up fights: `"followUp": { ... }`

This covers "pick a friendly ... unit within this unit's combat range that has not fought; it can fight immediately after":

```jsonc
"Furious Retribution": { "followUp": { "keywords": ["WARRIOR CHAMBER", "INFANTRY"], "excludeKeywords": ["HERO"],
                                       "effects": [ { "type": "buff", "stat": "Wound", "value": 1 } ] } }
```

After the unit fights, its player chooses a candidate to fight next, or declines. The `effects` are applied to the unit that fights next. Normal alternation then resumes.

## Reserves: `"reserve": { ... }`

Deep strike, tunnels and similar rules are defined per pool:

```jsonc
"The Celestial Realm": { "reserve": { "pool": "Celestial Realm", "declare": true } },                // deployment: may go into the pool
"Scions Of The Storm": { "reserve": { "pool": "Celestial Realm", "arrive": { "minEnemyDistance": 9 } } },
"Gnawhole Ambush":     { "reserve": { "pool": "tunnels below",
                         "arrive": { "minEnemyDistance": 9, "marker": "Gnawhole", "markerRange": 6 } } }  // wholly within 6" of a friendly Gnawhole
```

**Declare fields:**
- `keywords`: only units with all of these keywords may enter the pool.

**Arrive fields:**
- `minEnemyDistance`: how far every model must be from all enemy units.
- `marker` and `markerRange`: every model must be wholly within that range of a friendly marker with that name.
- `fromRound`: the first battle round units can arrive. The default is 2.

**Core rules applied by the engine** (taken from memory of the AoS4 core rules; check them against the rulebook):
- At most half of an army's units can start in reserve.
- Units can't arrive in battle round 1.
- Arriving uses the unit's move for that phase.
- Units still in reserve at the start of battle round 4 are destroyed.

An arriving unit counts as "set up this turn", so the `setUpThisTurn` and `targetSetUpThisTurn` conditions apply (e.g. Blazing Impact).

## Markers: `"marker": { ... }`

Placeable battlefield markers, such as Skaven Gnawholes:

```jsonc
"Splinters Of The Vermindoom": { "marker": { "name": "Gnawhole", "minEnemyDistance": 9, "minObjectiveDistance": 3,
                                             "minTerrainDistance": 1, "radius": 1.5 } }
```

If the ability's timing is "Start of Battle Round", the player is asked to place one marker each battle round. The engine checks the distance limits.

## Not yet scriptable

These still need engine support and stay as text for now:
- tokens
- keywords gained during the battle (HUNTED, SOULBURNED, questmarked)
- conditions on what happened earlier in the turn (e.g. "damaged by this weapon this turn")
- re-rolls other than charges
- triggers when a unit is destroyed
- moves that pass through models or combat ranges and strike units they cross
- prayers' ritual points
- faction rule groups (formations, heroic traits, artefacts and lores), which wait on the list builder
