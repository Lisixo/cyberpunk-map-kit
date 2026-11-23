using WolvenKit.Common.Services;
using WolvenKit.Core.Interfaces;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.Types;

namespace CyberpunkMapExtractor;

internal sealed record DistrictMeta(
    ulong TweakIdHash,
    string DistrictId,
    string EnumName,
    string NamePl,
    string? ParentDistrictId,
    string? ParentNamePl,
    bool IsQuestDistrict,
    bool IsSubDistrict
);

internal sealed record FastTravelMeta(
    string TweakId,
    string ShortKey,
    string NamePl,
    string Description,
    string? DistrictId,
    string? DistrictNamePl,
    bool ShowOnMap,
    bool IsSubway
);

internal sealed class LocalizationHelper
{
    private readonly Dictionary<ulong, string> _plByPrimary = new();
    private readonly Dictionary<string, string> _plBySecondary = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, string> _enByPrimary = new();
    private readonly Dictionary<string, string> _enBySecondary = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<ulong, DistrictMeta> DistrictsByHash { get; } = new();
    public Dictionary<string, DistrictMeta> DistrictsById { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FastTravelMeta> FastTravelPoints { get; } = new();

    public async Task InitializeAsync(
        HashService hashService,
        IReadOnlyDictionary<ulong, IGameFile> grouped,
        Red4ParserService parser,
        string tweakDbPath)
    {
        var tdb = new TweakDBService(hashService);
        await tdb.LoadDB(tweakDbPath);

        // Load English first (fallback), then Polish (base + ep1 override)
        LoadOnscreens(grouped, parser, @"base\localization\en-us\onscreens\onscreens_final.json", _enByPrimary, _enBySecondary);
        LoadOnscreens(grouped, parser, @"ep1\localization\en-us\onscreens\onscreens_final.json", _enByPrimary, _enBySecondary);

        LoadOnscreens(grouped, parser, @"base\localization\pl-pl\onscreens\onscreens_final.json", _plByPrimary, _plBySecondary);
        LoadOnscreens(grouped, parser, @"ep1\localization\pl-pl\onscreens\onscreens_final.json", _plByPrimary, _plBySecondary);

        Console.WriteLine($"Localization loaded: PL primary={_plByPrimary.Count}, secondary={_plBySecondary.Count}; EN fallback={_enByPrimary.Count}");

        BuildDistrictCatalog();
        BuildFastTravelCatalog();

        foreach (var sampleId in new[] { "Districts.LittleChina", "Districts.CityCenter", "Districts.Dogtown" })
        {
            if (DistrictsById.TryGetValue(sampleId, out var dm))
                Console.WriteLine($"  Sample district: {sampleId} -> '{dm.NamePl}' (quest={dm.IsQuestDistrict})");
        }
        foreach (var ft in FastTravelPoints.Take(3))
        {
            Console.WriteLine($"  Sample FT: {ft.TweakId} -> '{ft.NamePl}'");
        }
    }

    private static void LoadOnscreens(
        IReadOnlyDictionary<ulong, IGameFile> grouped,
        Red4ParserService parser,
        string path,
        Dictionary<ulong, string> byPrimary,
        Dictionary<string, string> bySecondary)
    {
        var f = grouped.Values.FirstOrDefault(x => x.FileName.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (f == null) return;
        var cr2w = ArchiveHelpers.ReadCr2w(f, parser);
        localizationPersistenceOnScreenEntries? ose = cr2w?.RootChunk switch
        {
            localizationPersistenceOnScreenEntries direct => direct,
            JsonResource jr => jr.Root?.Chunk as localizationPersistenceOnScreenEntries,
            _ => null
        };
        if (ose == null) return;

        foreach (var e in ose.Entries)
        {
            string? text = (string?)e.MaleVariant;
            if (string.IsNullOrWhiteSpace(text)) text = (string?)e.FemaleVariant;
            if (string.IsNullOrWhiteSpace(text)) continue;

            ulong pk = e.PrimaryKey;
            if (pk != 0) byPrimary[pk] = text;

            string? sk = (string?)e.SecondaryKey;
            if (!string.IsNullOrWhiteSpace(sk)) bySecondary[sk] = text;
        }
    }

    public string ResolveLoc(string? keyOrLocKey, string? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(keyOrLocKey))
            return fallback ?? string.Empty;

        if (keyOrLocKey.StartsWith("LocKey#", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(keyOrLocKey.AsSpan(7), out ulong id))
        {
            if (_plByPrimary.TryGetValue(id, out var pl)) return pl;
            if (_enByPrimary.TryGetValue(id, out var en)) return en;
        }

        if (_plBySecondary.TryGetValue(keyOrLocKey, out var pls)) return pls;
        if (_enBySecondary.TryGetValue(keyOrLocKey, out var ens)) return ens;

        return fallback ?? keyOrLocKey;
    }

    public string ResolveLoc(LocalizationString? locStr, string? fallback = null)
    {
        if (locStr == null) return fallback ?? string.Empty;
        if (locStr.Unk1 != 0)
        {
            if (_plByPrimary.TryGetValue(locStr.Unk1, out var pl)) return pl;
            if (_enByPrimary.TryGetValue(locStr.Unk1, out var en)) return en;
        }
        return ResolveLoc(locStr.Value, fallback);
    }

    private void BuildDistrictCatalog()
    {
        var rawRecords = new List<(TweakDBID Id, gamedataDistrict_Record Rec)>();
        foreach (var tid in TweakDBService.GetRecords())
        {
            string? name = tid.GetResolvedText();
            if (name == null || !name.StartsWith("Districts.", StringComparison.Ordinal)) continue;
            if (TweakDBService.GetRecord(tid) is gamedataDistrict_Record rec)
                rawRecords.Add((tid, rec));
        }

        // First pass: resolve own names
        var namesById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tid, rec) in rawRecords)
        {
            string fullId = tid.GetResolvedText()!;
            string shortId = fullId["Districts.".Length..];
            string locKey = (string?)rec.LocalizedName ?? "";
            string enumName = (string?)rec.EnumName ?? shortId;
            string resolved = ResolveLoc(locKey, CleanDisplayName(shortId));
            namesById[fullId] = resolved;
        }

        // Second pass: resolve parent hierarchy
        foreach (var (tid, rec) in rawRecords)
        {
            string fullId = tid.GetResolvedText()!;
            string shortId = fullId["Districts.".Length..];
            string? parentFull = rec.ParentDistrict.GetResolvedText();
            if (string.IsNullOrWhiteSpace(parentFull) || parentFull == "None") parentFull = null;

            string? parentName = parentFull != null && namesById.TryGetValue(parentFull, out var pn) ? pn : null;
            string namePl = namesById[fullId];
            bool forcePlayable =
                fullId.Equals("Districts.Dogtown", StringComparison.OrdinalIgnoreCase) ||
                fullId.Equals("Districts.CityCenter", StringComparison.OrdinalIgnoreCase) ||
                fullId.Equals("Districts.Watson", StringComparison.OrdinalIgnoreCase) ||
                fullId.Equals("Districts.Heywood", StringComparison.OrdinalIgnoreCase) ||
                fullId.Equals("Districts.Westbrook", StringComparison.OrdinalIgnoreCase) ||
                fullId.Equals("Districts.SantoDomingo", StringComparison.OrdinalIgnoreCase) ||
                fullId.Equals("Districts.Pacifica", StringComparison.OrdinalIgnoreCase) ||
                fullId.Equals("Districts.Badlands", StringComparison.OrdinalIgnoreCase) ||
                fullId.Equals("Districts.SouthBadlands", StringComparison.OrdinalIgnoreCase);

            bool isQuest = !forcePlayable && (
                (bool)rec.IsQuestDistrict ||
                fullId.Contains("Cyberspace", StringComparison.OrdinalIgnoreCase) ||
                fullId.Contains("Unlisted", StringComparison.OrdinalIgnoreCase) ||
                fullId.Contains("VR_Tutorial", StringComparison.OrdinalIgnoreCase) ||
                fullId.Contains("SpaceStation", StringComparison.OrdinalIgnoreCase) ||
                fullId.Contains("LangleyClinic", StringComparison.OrdinalIgnoreCase) ||
                fullId.Contains("Cynosure", StringComparison.OrdinalIgnoreCase));

            var meta = new DistrictMeta(
                TweakIdHash: (ulong)tid,
                DistrictId: fullId,
                EnumName: (string?)rec.EnumName ?? shortId,
                NamePl: namePl,
                ParentDistrictId: parentFull,
                ParentNamePl: parentName,
                IsQuestDistrict: isQuest,
                IsSubDistrict: parentFull != null
            );

            DistrictsByHash[(ulong)tid] = meta;
            DistrictsById[fullId] = meta;
        }

        Console.WriteLine($"DistrictCatalog built: {DistrictsById.Count} districts");
    }

    private void BuildFastTravelCatalog()
    {
        foreach (var tid in TweakDBService.GetRecords())
        {
            string? full = tid.GetResolvedText();
            if (full == null || !full.StartsWith("FastTravelPoints.", StringComparison.Ordinal)) continue;
            if (TweakDBService.GetRecord(tid) is not gamedataFastTravelPoint_Record rec) continue;

            string shortKey = full["FastTravelPoints.".Length..];
            string desc = (string?)rec.Description ?? shortKey;
            string namePl = ResolveLoc((string?)rec.DisplayName, desc);
            string? distId = rec.District.GetResolvedText();
            string? distName = distId != null && DistrictsById.TryGetValue(distId, out var dm) ? dm.NamePl : null;

            FastTravelPoints.Add(new FastTravelMeta(
                TweakId: full,
                ShortKey: shortKey,
                NamePl: namePl,
                Description: desc,
                DistrictId: distId,
                DistrictNamePl: distName,
                ShowOnMap: (bool)rec.ShowOnWorldMap,
                IsSubway: (bool)rec.SubwayStation
            ));
        }

        Console.WriteLine($"FastTravelCatalog built: {FastTravelPoints.Count} points");
    }

    public static string CleanDisplayName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(raw.Replace('_', ' '), "([a-z])([A-Z])", "$1 $2").Trim();
    }
}
