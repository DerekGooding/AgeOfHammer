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

Any effect can include `"to": "self"` to apply to the user instead of the target.

## Not yet scriptable

These still need engine support and stay as text for now:
- deep strike and reserves (Scions of the Storm, tunnels, Gnawholes)
- tokens
- extra fight activations
- reactions such as Ruination Chamber or "after a save roll of 6"
- re-rolls
- abilities with results tables
- abilities that move or set up units
- faction rule groups (formations, heroic traits, artefacts and lores), which wait on the list builder
