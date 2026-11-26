# Asset pipeline

This pipeline makes unit icons and sound effects for the Godot client from versioned specs. Every approved file is recorded in a manifest with its prompt, seed, model and license.

| File | Purpose |
|---|---|
| `pipeline.json` | Generator service URLs, the default backends, and size/loudness settings. `outputRoot` is where approved files go (`client/AgeOfHammer.Client/assets`). |
| `style.json` | Shared prompt templates and colour palettes. `categoryPalettes` maps a faction id to a palette. Bumping `version` makes every asset stale. |
| `models.json` | Each backend's model and license. Only `approved` and `capped` models are allowed to generate. |
| `specs.json` | The assets to make. Icons name a warscroll id from `data/factions`. Sounds name `event:<SoundEvent>`; the list of events is in `tools/AgeOfHammer.Assets/AssetPlanner.cs`. |
| `manifest.json` | What has been approved, and how it was made. The tool writes this file; don't edit it by hand. |

## Workflow

1. **Start the generator services.** The setup is in [tools/asset-services/README.md](../tools/asset-services/README.md). The procedural placeholder backends need no GPU.
2. **Generate candidates:** run `dotnet run --project tools/AgeOfHammer.Assets -- generate`.
   - Candidates are stored outside the repo, in `%LOCALAPPDATA%/AgeOfHammer/asset-candidates`.
   - The command also writes a `review.html` that shows candidates side by side.
3. **Approve or reject.**
   - Approve with `approve <assetId> <n[,n...]>`, for example `approve icon/liberators 2` or `approve sfx/event_ModelSlain/play 3,1,5`. Approved files are copied into the client and recorded in the manifest.
   - Discard an asset's candidates with `reject <assetId>`.

**Other commands:**
- `status`: what is approved, stale, missing, or waiting for review.
- `validate`: checks specs, palettes, backends and approved files, and warns about warscrolls or events that have no asset.
- `health`: shows which backends each service can run.

**Common options:** `--filter <text>`, `--force`, `--limit <n>` and `--auto-approve`. `--auto-approve` takes the first passing candidates, which suits placeholders.

An asset becomes **stale** whenever something that affects its output changes. That covers its prompt, palette, seed, parameters, backend, model source or the style version. `generate` then remakes it.

## Automatic checks

The services report metrics for each candidate, and the tool turns them into warnings and errors:
- **Icons:** the image size, whether a subject was found, whether it touches the edge, and its coverage.
- **Sounds:** the length, loudness compared with `targetLufs` (±3 LU), and the peak compared with `maxPeakDb`.

Any candidate with errors is marked on the review page, and `--auto-approve` skips it.
