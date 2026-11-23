using System.Text.Json;
using WolvenKit.Core.Interfaces;
using WolvenKit.Core.Murmur3;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.Types;

namespace CyberpunkMapExtractor;

internal sealed record ExtractedPoi(
    int Id,
    string Name,
    string Category,
    string Variant,
    double X,
    double Y,
    double Z,
    string? District,
    string? Subdistrict,
    string Source,
    string? JournalOrTweakId
);

internal sealed record PoiExtractionSummary(
    int TotalPois,
    int FastTravelCount,
    int MetroCount,
    Dictionary<string, int> CountByCategory
);

internal static class PoiExtractor
{
    public static (PoiExtractionSummary Summary, List<ExtractedPoi> Pois) Extract(
        worldStreamingWorld wsw,
        IReadOnlyList<(worldStreamingSectorDescriptor Desc, IGameFile File)> alwaysLoadedSectors,
        IReadOnlyDictionary<ulong, IGameFile> grouped,
        Red4ParserService parser,
        LocalizationHelper loc,
        DistrictSpatialIndex districts,
        string outputGeoJsonPath)
    {
        var pois = new List<ExtractedPoi>();
        int nextId = 1;

        // 1. Index journal entries
        var journalByHash = new Dictionary<uint, (string Path, gameJournalEntry Entry, string? ParentTitle)>();
        foreach (var jf in grouped.Values.Where(f => f.FileName.EndsWith(".journal", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.FileName))
        {
            var cr2w = ArchiveHelpers.ReadCr2w(jf, parser);
            if (cr2w?.RootChunk is gameJournalResource jr && jr.Entry?.Chunk is gameJournalEntry root)
                TraverseJournal(root, "", null, loc, journalByHash);
        }

        // 2. Extract PoiMappinResource (service points, shops, apartments, tarot, gigs, cyberpsychos, courier)
        var poiFile = ArchiveHelpers.FindFile(wsw.PoiMappinResource.DepotPath, grouped);
        if (poiFile != null && ArchiveHelpers.ReadCr2w(poiFile, parser)?.RootChunk is gamePointOfInterestMappinResource pmr)
        {
            foreach (var c in pmr.CookedData)
            {
                double x = GeoMath.R3((float)c.Position.X);
                double y = GeoMath.R3((float)c.Position.Y);
                double z = GeoMath.R3((float)c.Position.Z);
                if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) continue;

                uint hash = (uint)c.JournalPathHash;
                string journalPath = "";
                string variant = "Unknown";
                string? parentTitle = null;
                string entryId = "";

                if (journalByHash.TryGetValue(hash, out var j))
                {
                    journalPath = j.Path;
                    parentTitle = j.ParentTitle;
                    entryId = (string?)j.Entry.Id ?? "";
                    if (j.Entry is gameJournalPointOfInterestMappin poiEntry)
                    {
                        var tv = poiEntry.MappinData.TypedVariant?.Chunk;
                        if (tv is gamemappinsCommonVariant cv) variant = cv.Variant.ToString();
                        else if (tv is gamemappinsPhaseVariant pv) variant = pv.Variant.ToString();
                    }
                }

                var (category, plLabel) = MapVariantToPolishCategory(variant, journalPath);
                var (dist, sub) = districts.Lookup(x, y);

                string? resolvedEntryName = !string.IsNullOrEmpty(entryId)
                    ? loc.ResolveLoc(entryId, "")
                    : null;

                string name = !string.IsNullOrWhiteSpace(parentTitle)
                    ? parentTitle
                    : (!string.IsNullOrWhiteSpace(resolvedEntryName)
                        ? resolvedEntryName
                        : (sub ?? dist) != null ? $"{plLabel} ({sub ?? dist})" : plLabel);

                pois.Add(new ExtractedPoi(
                    Id: nextId++,
                    Name: name,
                    Category: category,
                    Variant: variant,
                    X: x,
                    Y: y,
                    Z: z,
                    District: dist,
                    Subdistrict: sub,
                    Source: "PoiMappinResource",
                    JournalOrTweakId: string.IsNullOrEmpty(journalPath) ? $"0x{hash:X8}" : journalPath
                ));
            }
        }

        // 3. Extract Fast Travel Points + Metro Stations via AlwaysLoaded NodeRefs <-> GlobalNodeId
        var ftByShortKey = new Dictionary<string, FastTravelMeta>(StringComparer.OrdinalIgnoreCase);
        foreach (var ft in loc.FastTravelPoints)
        {
            ftByShortKey[ft.ShortKey] = ft;
        }

        var matchedFtIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, sf) in alwaysLoadedSectors)
        {
            var cr2w = ArchiveHelpers.ReadCr2w(sf, parser);
            if (cr2w?.RootChunk is not worldStreamingSector wss) continue;
            if (wss.NodeData?.Data is not worldNodeDataBuffer buf) continue;

            // Map GlobalNodeId.Hash -> Transform.Position
            var posByGlobalHash = new Dictionary<ulong, Vector4>(buf.Count);
            foreach (var nd in buf)
            {
                ulong gid = (ulong)nd.GlobalNodeId.Hash;
                if (gid != 0 && !posByGlobalHash.ContainsKey(gid))
                    posByGlobalHash[gid] = nd.Transform.Position;
            }

            foreach (var nr in wss.NodeRefs)
            {
                string? nrStr = nr.GetResolvedText();
                if (string.IsNullOrEmpty(nrStr)) continue;
                if (!nrStr.Contains("dataterm", StringComparison.OrdinalIgnoreCase) &&
                    !nrStr.Contains("data_term", StringComparison.OrdinalIgnoreCase) &&
                    !nrStr.Contains("_ftp_", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!posByGlobalHash.TryGetValue(nr.GetRedHash(), out var wp)) continue;

                // Try matching NodeRef segments against FastTravelPoints short keys
                FastTravelMeta? matchedFt = MatchFastTravelMeta(nrStr, ftByShortKey);
                if (matchedFt == null) continue;
                if (!matchedFtIds.Add(matchedFt.TweakId)) continue;

                double x = GeoMath.R3((float)wp.X);
                double y = GeoMath.R3((float)wp.Y);
                double z = GeoMath.R3((float)wp.Z);
                if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) continue;

                var (dist, sub) = districts.Lookup(x, y);
                dist ??= matchedFt.DistrictNamePl;

                string cat = matchedFt.IsSubway ? "metro_station" : "fast_travel";
                string variant = matchedFt.IsSubway ? "MetroStation" : "FastTravelPoint";

                pois.Add(new ExtractedPoi(
                    Id: nextId++,
                    Name: matchedFt.NamePl,
                    Category: cat,
                    Variant: variant,
                    X: x,
                    Y: y,
                    Z: z,
                    District: dist,
                    Subdistrict: sub,
                    Source: "FastTravelPoint",
                    JournalOrTweakId: matchedFt.TweakId
                ));
            }
        }

        // Deduplicate POIs (keep unique within 2m of same category & name)
        var deduped = new List<ExtractedPoi>(pois.Count);
        foreach (var p in pois)
        {
            bool dup = deduped.Any(k =>
                k.Category == p.Category &&
                string.Equals(k.Name, p.Name, StringComparison.OrdinalIgnoreCase) &&
                GeoMath.Dist2D(k.X - p.X, k.Y - p.Y) < 2.5);
            if (!dup)
                deduped.Add(p with { Id = deduped.Count + 1 });
        }

        WriteGeoJson(deduped, outputGeoJsonPath);

        var byCat = deduped.GroupBy(p => p.Category).ToDictionary(g => g.Key, g => g.Count());
        var summary = new PoiExtractionSummary(
            TotalPois: deduped.Count,
            FastTravelCount: deduped.Count(p => p.Category == "fast_travel"),
            MetroCount: deduped.Count(p => p.Category == "metro_station"),
            CountByCategory: byCat
        );
        return (summary, deduped);
    }

    private static FastTravelMeta? MatchFastTravelMeta(string nodeRef, Dictionary<string, FastTravelMeta> byKey)
    {
        // NodeRef looks like $/03_night_city/.../wat_kab_dataterm_12/#wat_kab_dataterm_12_tp
        var parts = nodeRef.Split(['/', '#'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            string seg = parts[i];
            if (byKey.TryGetValue(seg, out var exact)) return exact;
            if (seg.EndsWith("_tp", StringComparison.OrdinalIgnoreCase) && byKey.TryGetValue(seg[..^3], out var noTp)) return noTp;
            if (seg.EndsWith("_exit", StringComparison.OrdinalIgnoreCase) && byKey.TryGetValue(seg[..^5], out var noExit)) return noExit;
            if (seg.Contains("_prefab", StringComparison.OrdinalIgnoreCase))
            {
                int idx = seg.IndexOf("_prefab", StringComparison.OrdinalIgnoreCase);
                string trimmed = seg[..idx];
                if (byKey.TryGetValue(trimmed, out var fromPrefab)) return fromPrefab;
            }
        }
        return null;
    }

    private static void TraverseJournal(
        gameJournalEntry entry,
        string prefix,
        string? inheritedTitle,
        LocalizationHelper loc,
        Dictionary<uint, (string Path, gameJournalEntry Entry, string? ParentTitle)> map)
    {
        string id = (string?)entry.Id ?? "";
        string currentPath = entry is gameJournalRootFolderEntry
            ? id
            : (string.IsNullOrEmpty(prefix) ? id : $"{prefix}/{id}");

        string? ownTitle = inheritedTitle;
        var titleProp = entry.GetType().GetProperty("Title");
        if (titleProp != null && titleProp.GetValue(entry) is LocalizationString ls)
        {
            string resolved = loc.ResolveLoc(ls, "");
            if (!string.IsNullOrWhiteSpace(resolved))
                ownTitle = resolved;
        }

        if (!string.IsNullOrEmpty(currentPath))
        {
            uint h1 = Murmur32.Hash(currentPath, 0x5EEDBA5E);
            map[h1] = (currentPath, entry, ownTitle);
            if (currentPath.StartsWith("ep1/", StringComparison.OrdinalIgnoreCase))
            {
                string stripped = currentPath[4..];
                uint h2 = Murmur32.Hash(stripped, 0x5EEDBA5E);
                map.TryAdd(h2, (currentPath, entry, ownTitle));
            }
        }

        if (entry is gameJournalContainerEntry container)
        {
            foreach (var ch in container.Entries)
            {
                if (ch?.Chunk is gameJournalEntry child)
                    TraverseJournal(child, currentPath, ownTitle, loc, map);
            }
        }
    }

    private static (string Category, string LabelPl) MapVariantToPolishCategory(string variant, string journalPath)
    {
        return variant switch
        {
            "ServicePointRipperdocVariant" => ("ripperdoc", "Ripperdoc"),
            "ServicePointGunsVariant" => ("shop_weapons", "Sklep z bronią"),
            "ServicePointMeleeTrainerVariant" => ("shop_melee", "Broń biała"),
            "ServicePointNetTrainerVariant" => ("shop_netrunner", "Netrunner"),
            "ServicePointClothesVariant" => ("shop_clothes", "Sklep z ubraniami"),
            "ServicePointFoodVariant" => ("shop_food", "Jedzenie"),
            "ServicePointBarVariant" => ("bar", "Bar"),
            "ServicePointMedsVariant" => ("shop_meds", "Apteka"),
            "ServicePointJunkVariant" => ("shop_junk", "Sklep z gratami"),
            "Zzz21_ServicePointGlamdocVariant" => ("ripperdoc_style", "Salon stylu"),
            "ApartmentVariant" => ("apartment", "Mieszkanie V"),
            "FixerVariant" => ("fixer", "Fixer"),
            "TarotVariant" => ("tarot", "Karta tarota"),
            "Zzz01_CarForPurchaseVariant" => ("autofixer", "Autofixer — pojazd"),
            "HuntForPsychoVariant" => ("cyberpsycho", "Cyberpsychopata"),
            "GangWatchVariant" or "OutpostVariant" or "ResourceVariant" or "HiddenStashVariant" => ("ncpd_scanner", "Aktywność NCPD"),
            "Zzz09_CourierSandboxActivityVariant" => ("dogtown_courier", "Zlecenie kurierskie (Dogtown)"),
            "Zzz12_WorldEncounterVariant" => ("world_encounter", "Zdarzenie w świecie"),
            "ThieveryVariant" or "ClientInDistressVariant" or "BountyHuntVariant" or "RetrievingVariant" or "SabotageVariant" or "GettingWarmerVariant" or "Zzz02_StealthVariant" => ("gig", "Kontrakt (Gig)"),
            "DefaultQuestVariant" => ("side_job", "Zadanie poboczne"),
            _ when journalPath.Contains("street_race", StringComparison.OrdinalIgnoreCase) => ("street_race", "Wyścig uliczny"),
            _ when journalPath.Contains("safehouses", StringComparison.OrdinalIgnoreCase) => ("apartment", "Mieszkanie"),
            _ when journalPath.Contains("service_points", StringComparison.OrdinalIgnoreCase) => ("service_point", "Punkt usługowy"),
            _ when journalPath.Contains("tarot", StringComparison.OrdinalIgnoreCase) => ("tarot", "Karta tarota"),
            _ => ("poi_other", "Punkt zainteresowania")
        };
    }

    private static void WriteGeoJson(List<ExtractedPoi> pois, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var w = GeoJsonUtil.CreateWriter(fs, indented: false);
        GeoJsonUtil.BeginFeatureCollection(w, "cyberpunk2077_poi", "REDengine 4 World Coordinates (meters)");

        foreach (var p in pois)
        {
            w.WriteStartObject();
            w.WriteString("type", "Feature");
            w.WriteNumber("id", p.Id);

            w.WriteStartObject("geometry");
            w.WriteString("type", "Point");
            w.WriteStartArray("coordinates");
            w.WriteNumberValue(p.X);
            w.WriteNumberValue(p.Y);
            w.WriteNumberValue(p.Z);
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject("properties");
            w.WriteNumber("id", p.Id);
            w.WriteString("name", p.Name);
            w.WriteString("category", p.Category);
            w.WriteString("variant", p.Variant);
            if (p.District != null) w.WriteString("district", p.District);
            else w.WriteNull("district");
            if (p.Subdistrict != null) w.WriteString("subdistrict", p.Subdistrict);
            else w.WriteNull("subdistrict");
            w.WriteString("source", p.Source);
            if (p.JournalOrTweakId != null) w.WriteString("ref_id", p.JournalOrTweakId);
            else w.WriteNull("ref_id");
            w.WriteEndObject();

            w.WriteEndObject();
        }

        GeoJsonUtil.EndFeatureCollection(w);
    }
}
