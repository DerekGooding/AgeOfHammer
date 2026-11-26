using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgeOfHammer.Core.Data;

namespace AgeOfHammer.Assets;

public enum AssetKind
{
    Icon,
    Sfx
}

/// <summary>
/// Game moments that can have a sound ("event:&lt;SoundEvent&gt;" in specs.json). Files land in
/// sfx/event_&lt;SoundEvent&gt;/play_N.ogg for the client to play when that happens in a battle.
/// </summary>
public enum SoundEvent
{
    BattleRoundStart,
    TurnStart,
    Move,
    Run,
    ChargeDeclared,
    ChargeSucceeded,
    ChargeFailed,
    RangedAttack,
    MeleeAttack,
    SaveMade,
    ModelSlain,
    UnitDestroyed,
    MortalDamage,
    CommandUsed,
    SpellCast,
    SpellUnbound,
    Miscast,
    ObjectiveCaptured,
    Victory,
    Defeat
}

public enum AssetState
{
    /// <summary>Never approved.</summary>
    Missing,

    /// <summary>Approved under a different spec, style, or backend (or its files are gone): regenerate.</summary>
    Stale,

    /// <summary>Approved and current.</summary>
    Approved
}

/// <summary>One asset to produce: a fully resolved spec (prompt, palette, seed, parameters) and where it lands.</summary>
public sealed record AssetJob
{
    public required string AssetId { get; init; }
    public required AssetKind Kind { get; init; }
    public required string Source { get; init; }
    public required string Slot { get; init; }
    public required string Prompt { get; init; }
    public required string NegativePrompt { get; init; }
    public required IReadOnlyList<string> Palette { get; init; }
    public required int Seed { get; init; }
    public required SortedDictionary<string, object> Params { get; init; }
    public required string Backend { get; init; }
    public required int Candidates { get; init; }

    /// <summary>Most files one approval can produce (1 for icons; sound variants for SFX).</summary>
    public required int Variants { get; init; }

    /// <summary>Output paths relative to the output root, one per variant.</summary>
    public required IReadOnlyList<string> OutputFiles { get; init; }

    public required string Hash { get; init; }
}

/// <summary>Everything under assets/ plus the game content, loaded together.</summary>
public sealed class AssetProject
{
    public required string RepoRoot { get; init; }
    public required string CandidatesRoot { get; init; }
    public required PipelineConfig Config { get; init; }
    public required StyleBible Style { get; init; }
    public required ModelRegistry Models { get; init; }
    public required SpecsFile Specs { get; init; }
    public required AssetManifest Manifest { get; init; }
    /// <summary>The factions in data/factions: icon specs must name one of their warscrolls.</summary>
    public required IReadOnlyList<FactionData> Factions { get; init; }

    public string AssetsDir => Path.Combine(RepoRoot, "assets");
    public string ManifestPath => Path.Combine(AssetsDir, "manifest.json");
    public string OutputRoot => Path.GetFullPath(Path.Combine(RepoRoot, Config.OutputRoot));

    public static string DefaultCandidatesRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgeOfHammer", "asset-candidates");

    public static AssetProject Load(string repoRoot, string? candidatesRoot = null, IReadOnlyList<FactionData>? factions = null)
    {
        string assets = Path.Combine(repoRoot, "assets");
        return new AssetProject
        {
            RepoRoot = repoRoot,
            CandidatesRoot = candidatesRoot ?? DefaultCandidatesRoot,
            Config = AssetJson.Read<PipelineConfig>(Path.Combine(assets, "pipeline.json")),
            Style = AssetJson.Read<StyleBible>(Path.Combine(assets, "style.json")),
            Models = AssetJson.Read<ModelRegistry>(Path.Combine(assets, "models.json")),
            Specs = AssetJson.Read<SpecsFile>(Path.Combine(assets, "specs.json")),
            Manifest = AssetJson.Read<AssetManifest>(Path.Combine(assets, "manifest.json")),
            Factions = factions ?? DataLoader.LoadAllFactions(Path.Combine(repoRoot, "data"))
        };
    }

    /// <summary>Walks up from <paramref name="start"/> to the directory holding AgeOfHammer.slnx.</summary>
    public static string FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AgeOfHammer.slnx")))
                return dir.FullName;
        throw new DirectoryNotFoundException($"No AgeOfHammer.slnx above '{start}'. Pass --repo <path>.");
    }

    public ModelEntry? FindModel(string backend) => Models.Models.FirstOrDefault(m => m.Backend == backend);

    public string CandidateDir(AssetJob job) =>
        Path.Combine(CandidatesRoot, Path.Combine(job.AssetId.Split('/')), job.Hash);
}

public sealed record AssetPlan(IReadOnlyList<AssetJob> Jobs, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>Resolves specs against the style bible and the game content, and checks them.</summary>
public static class AssetPlanner
{
    public const string EventSlot = "play";
    public const string EventPrefix = "event:";

    public static AssetPlan Plan(AssetProject project, string? imageBackend = null, string? audioBackend = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var jobs = new List<AssetJob>();
        var config = project.Config;
        imageBackend ??= config.ImageBackend;
        audioBackend ??= config.AudioBackend;

        CheckBackend(project, imageBackend, "image", errors);
        CheckBackend(project, audioBackend, "audio", errors);

        // Unit icons: one per warscroll, palette chosen by faction id (style.json categoryPalettes).
        foreach (var spec in project.Specs.Icons)
        {
            var faction = project.Factions.FirstOrDefault(f => f.Warscrolls.Any(w => w.Id == spec.Source));
            if (faction is null)
            {
                errors.Add($"icon '{spec.Source}': no warscroll in data/factions has this id.");
                continue;
            }

            string paletteName = spec.Palette ?? project.Style.CategoryPalettes.GetValueOrDefault(faction.Id) ?? "";
            if (!project.Style.Palettes.TryGetValue(paletteName, out var palette))
            {
                errors.Add($"icon '{spec.Source}': unknown palette '{paletteName}' (see assets/style.json).");
                continue;
            }

            string assetId = $"icon/{spec.Source}";
            var parameters = new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["size"] = config.Icon.Size,
                ["steps"] = config.Icon.Steps,
                ["guidance"] = config.Icon.Guidance,
                ["background"] = project.Style.Icon.Background ?? "#D0D0D0"
            };
            if (config.Icon.RenderSize is { } render)
                parameters["renderSize"] = render;

            jobs.Add(Finish(project, new AssetJob
            {
                AssetId = assetId,
                Kind = AssetKind.Icon,
                Source = spec.Source,
                Slot = "icon",
                Prompt = Fill(project.Style.Icon.Prompt, spec.Prompt, palette.Words),
                NegativePrompt = project.Style.Icon.NegativePrompt,
                Palette = palette.Colors,
                Seed = spec.Seed ?? DefaultSeed(assetId),
                Params = parameters,
                Backend = imageBackend,
                Candidates = config.Icon.Candidates,
                Variants = 1,
                OutputFiles = [$"icons/{spec.Source}.png"],
                Hash = ""
            }));
        }

        // Sounds: one set of variants per game event.
        foreach (var spec in project.Specs.Sfx)
        {
            if (!spec.Source.StartsWith(EventPrefix, StringComparison.Ordinal) ||
                !Enum.TryParse<SoundEvent>(spec.Source[EventPrefix.Length..], out _))
            {
                errors.Add($"sfx '{spec.Source}': expected '{EventPrefix}<SoundEvent>', one of: {string.Join(", ", Enum.GetNames<SoundEvent>())}.");
                continue;
            }

            if (spec.Slots.Count == 0)
                errors.Add($"sfx '{spec.Source}': no slots.");

            foreach (var (slot, subject) in spec.Slots)
            {
                if (slot != EventSlot)
                {
                    errors.Add($"sfx '{spec.Source}': slot '{slot}' must be '{EventSlot}'.");
                    continue;
                }

                string key = SoundKey(spec.Source);
                string assetId = $"sfx/{key}/{slot}";
                var parameters = new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    ["durationSeconds"] = spec.DurationSeconds ?? config.Sfx.DurationSeconds,
                    ["steps"] = config.Sfx.Steps,
                    ["guidance"] = config.Sfx.Guidance,
                    ["targetLufs"] = config.Sfx.TargetLufs,
                    ["maxPeakDb"] = config.Sfx.MaxPeakDb
                };

                jobs.Add(Finish(project, new AssetJob
                {
                    AssetId = assetId,
                    Kind = AssetKind.Sfx,
                    Source = spec.Source,
                    Slot = slot,
                    Prompt = Fill(project.Style.Sfx.Prompt, subject, ""),
                    NegativePrompt = project.Style.Sfx.NegativePrompt,
                    Palette = [],
                    Seed = spec.Seed ?? DefaultSeed(assetId),
                    Params = parameters,
                    Backend = audioBackend,
                    Candidates = Math.Max(config.Sfx.Candidates, config.Sfx.Variants),
                    Variants = config.Sfx.Variants,
                    OutputFiles = Enumerable.Range(1, config.Sfx.Variants).Select(n => $"sfx/{key}/{slot}_{n}.ogg").ToList(),
                    Hash = ""
                }));
            }
        }

        foreach (var duplicate in jobs.GroupBy(j => j.AssetId).Where(g => g.Count() > 1))
            errors.Add($"'{duplicate.Key}' is specified {duplicate.Count()} times.");

        // Coverage: warscrolls without an icon and events without a sound.
        var iconSources = project.Specs.Icons.Select(s => s.Source).ToHashSet();
        var missingIcons = project.Factions.SelectMany(f => f.Warscrolls).Select(w => w.Id)
            .Where(id => !iconSources.Contains(id)).Distinct().ToList();
        if (missingIcons.Count > 0)
            warnings.Add($"no icon spec for warscrolls: {string.Join(", ", missingIcons)}");

        var soundedEvents = project.Specs.Sfx.Select(s => s.Source).ToHashSet();
        var silentEvents = Enum.GetNames<SoundEvent>().Where(e => !soundedEvents.Contains(EventPrefix + e)).ToList();
        if (silentEvents.Count > 0)
            warnings.Add($"no sound for events: {string.Join(", ", silentEvents)}");

        return new AssetPlan(jobs, errors, warnings);
    }

    public static AssetState StateOf(AssetProject project, AssetJob job)
    {
        if (!project.Manifest.Assets.TryGetValue(job.AssetId, out var entry))
            return AssetState.Missing;
        if (entry.Hash != job.Hash)
            return AssetState.Stale;
        return entry.Files.All(f => File.Exists(Path.Combine(project.OutputRoot, f))) ? AssetState.Approved : AssetState.Stale;
    }

    /// <summary>Folder name for a sound source: "event_&lt;SoundEvent&gt;".</summary>
    public static string SoundKey(string source) =>
        source.StartsWith(EventPrefix, StringComparison.Ordinal) ? "event_" + source[EventPrefix.Length..] : source;

    /// <summary>A stable seed derived from the asset id (FNV-1a), so specs don't need hand-picked seeds.</summary>
    public static int DefaultSeed(string assetId)
    {
        uint hash = 2166136261;
        foreach (char c in assetId)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return (int)(hash % 1_000_000_000);
    }

    private static AssetJob Finish(AssetProject project, AssetJob job) => job with { Hash = ComputeHash(project, job) };

    /// <summary>
    /// Everything that changes the output: kind, prompts, palette, seed, parameters, backend, and the style version.
    /// </summary>
    public static string ComputeHash(AssetProject project, AssetJob job)
    {
        var model = project.FindModel(job.Backend);
        string canonical = JsonSerializer.Serialize(new
        {
            kind = job.Kind.ToString(),
            prompt = job.Prompt,
            negative = job.NegativePrompt,
            palette = job.Palette,
            seed = job.Seed,
            variants = job.Variants,
            parameters = job.Params,
            backend = job.Backend,
            style = project.Style.Version,
            source = model?.Source
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    private static void CheckBackend(AssetProject project, string backend, string kind, List<string> errors)
    {
        var model = project.FindModel(backend);
        if (model is null)
            errors.Add($"{kind} backend '{backend}' is not in assets/models.json.");
        else if (model.Kind != kind)
            errors.Add($"backend '{backend}' is a {model.Kind} model, not {kind}.");
        else if (!model.Usable)
            errors.Add($"backend '{backend}' has license status '{model.Status}': only approved or capped models may generate.");
    }

    private static string Fill(string template, string subject, string paletteWords) =>
        template.Replace("{subject}", subject).Replace("{palette}", paletteWords);
}
