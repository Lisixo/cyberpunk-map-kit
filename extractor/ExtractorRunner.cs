using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using WolvenKit.Common.Services;
using WolvenKit.Core.Interfaces;
using WolvenKit.RED4.Archive;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.CR2W.Archive;
using WolvenKit.RED4.Types;

namespace CyberpunkMapExtractor;

internal static class ExtractorRunner
{
    public const string DefaultGameDir = @"Q:\SteamLibrary\steamapps\common\Cyberpunk 2077";

    private static readonly string[] BuildingNodeTypeMarkers =
    [
        "worldBuildingProxyMeshNode",
        "worldCookedBuildingProxyMeshNode",
        "worldPrefabProxyMeshNode",
        "worldCookedPrefabProxyMeshNode",
        "worldCookedGenericProxyMeshNode",
        "worldStaticOccluderMeshNode",
        "worldCookedStaticMeshNode",
        "worldInstancedMeshNode"
    ];

    public static async Task<int> RunExtractAsync(string gameDir, string outputDir, bool verbose)
    {
        var totalSw = Stopwatch.StartNew();
        string exePath = Path.Combine(gameDir, "bin", "x64", "Cyberpunk2077.exe");
        string tweakDbEp1 = Path.Combine(gameDir, "r6", "cache", "tweakdb_ep1.bin");
        if (!File.Exists(exePath))
        {
            Console.Error.WriteLine($"Game executable not found: {exePath}");
            return 1;
        }
        if (!File.Exists(tweakDbEp1))
        {
            Console.Error.WriteLine($"TweakDB not found: {tweakDbEp1}");
            return 1;
        }

        Directory.CreateDirectory(outputDir);
        Console.WriteLine("=== Cyberpunk 2077 Map Extractor ===");
        Console.WriteLine($"Game dir : {gameDir}");
        Console.WriteLine($"Output   : {outputDir}");

        var logger = new ConsoleLogger(verbose);
        var hashService = new HashService();
        hashService.Load();
        var parser = new Red4ParserService(hashService, logger, new HookService());
        var am = new ArchiveManager(hashService, parser, logger, new PercentProgressService());
        am.LoadGameArchives(new FileInfo(exePath));

        var baseArchives = am.GetBaseArchives().ToList();
        var ep1Archives = am.GetEp1Archives().ToList();
        var grouped = new Dictionary<ulong, IGameFile>();
        foreach (var arch in baseArchives.Concat(ep1Archives))
        {
            foreach (var kvp in arch.Files)
                grouped[kvp.Key] = kvp.Value; // EP1 after Base overrides matching hashes
        }

        Console.WriteLine($"Loaded archives: base={baseArchives.Count}, ep1={ep1Archives.Count}, uniqueFiles={grouped.Count}");

        var loc = new LocalizationHelper();
        await loc.InitializeAsync(hashService, grouped, parser, tweakDbEp1);

        var ncSwFile = grouped.Values.First(f =>
            f.FileName.Equals(@"base\worlds\03_night_city\_compiled\default\03_night_city.streamingworld", StringComparison.OrdinalIgnoreCase));
        var wsw = (worldStreamingWorld)ArchiveHelpers.ReadCr2w(ncSwFile, parser)!.RootChunk;

        var allDescriptors = new List<worldStreamingSectorDescriptor>();
        foreach (var blockRef in wsw.BlockRefs)
        {
            var bf = ArchiveHelpers.FindFile(blockRef.DepotPath, grouped);
            if (bf != null && ArchiveHelpers.ReadCr2w(bf, parser)?.RootChunk is worldStreamingBlock wsb)
                allDescriptors.AddRange(wsb.Descriptors);
        }

        var uniqueDescriptors = allDescriptors
            .Where(d => d.Category.ToString() != "Navigation")
            .GroupBy(d => d.Data.DepotPath.GetRedHash())
            .Select(g => g.First())
            .ToList();

        // Single unified header scan across non-navigation sectors
        Console.WriteLine($"[1/5] Scanning {uniqueDescriptors.Count} sector headers (districts + buildings + always_loaded)...");
        var scanSw = Stopwatch.StartNew();
        var districtSectors = new List<(worldStreamingSectorDescriptor Desc, IGameFile File)>();
        var buildingSectors = new List<(worldStreamingSectorDescriptor Desc, IGameFile File)>();
        var alwaysLoadedSectors = new List<(worldStreamingSectorDescriptor Desc, IGameFile File)>();

        using (var ms = new MemoryStream(1024 * 1024))
        {
            int scanned = 0;
            foreach (var d in uniqueDescriptors)
            {
                var sf = ArchiveHelpers.FindFile(d.Data.DepotPath, grouped);
                if (sf == null) continue;
                scanned++;
                if (scanned % 5000 == 0)
                    Console.WriteLine($"  Headers {scanned}/{uniqueDescriptors.Count} ({scanSw.ElapsedMilliseconds}ms)");

                string cat = d.Category.ToString();
                if (cat == "AlwaysLoaded")
                {
                    alwaysLoadedSectors.Add((d, sf));
                }

                ms.SetLength(0);
                sf.Extract(ms);
                ms.Position = 0;
                if (!parser.TryReadRed4FileHeaders(ms, out var info)) continue;

                var strings = new HashSet<string>(info.StringDict.Values, StringComparer.Ordinal);
                if (strings.Contains("worldLocationAreaNotifier"))
                    districtSectors.Add((d, sf));

                if (cat == "Exterior")
                {
                    byte lvl = (byte)d.Level;
                    bool hasBuildingCandidate = lvl == 0
                        ? (strings.Contains("worldBuildingProxyMeshNode") || strings.Contains("worldCookedBuildingProxyMeshNode"))
                        : (strings.Contains("worldBuildingProxyMeshNode") ||
                           strings.Contains("worldCookedBuildingProxyMeshNode") ||
                           strings.Contains("worldPrefabProxyMeshNode") ||
                           strings.Contains("worldCookedPrefabProxyMeshNode") ||
                           strings.Contains("worldCookedGenericProxyMeshNode") ||
                           (lvl <= 4 && strings.Contains("worldStaticOccluderMeshNode")) ||
                           strings.Contains("worldCookedStaticMeshNode") ||
                           strings.Contains("worldInstancedMeshNode"));

                    if (hasBuildingCandidate)
                        buildingSectors.Add((d, sf));
                }
            }
        }
        Console.WriteLine($"  Header scan complete in {scanSw.ElapsedMilliseconds}ms: districtSectors={districtSectors.Count}, buildingSectors={buildingSectors.Count}, alwaysLoaded={alwaysLoadedSectors.Count}");

        // 2. Regions
        string regionsPath = Path.Combine(outputDir, "regions.geojson");
        Console.WriteLine("[2/5] Extracting districts & regions...");
        var (regions, spatialIndex) = RegionsExtractor.Extract(districtSectors, parser, loc, regionsPath);
        Console.WriteLine($"  Exported {regions.Count} regions -> {regionsPath}");

        // 3. Roads
        string roadsPath = Path.Combine(outputDir, "roads.geojson");
        Console.WriteLine("[3/5] Extracting road network & traffic graph...");
        var roadSummary = RoadsExtractor.Extract(wsw, grouped, parser, spatialIndex, roadsPath);
        Console.WriteLine($"  Exported {roadSummary.ExportedLanes} road lanes ({roadSummary.NeighborGroupCount} groups, {roadSummary.TotalConnections} connections) -> {roadsPath}");

        // 4. Buildings
        string buildingsPath = Path.Combine(outputDir, "buildings.geojson");
        Console.WriteLine("[4/5] Extracting buildings & dimensions...");
        var (bldgSummary, buildings) = BuildingsExtractor.Extract(buildingSectors, grouped, parser, spatialIndex, buildingsPath);
        Console.WriteLine($"  Exported {bldgSummary.ExportedBuildings} buildings (from {bldgSummary.RawCandidates} raw candidates) -> {buildingsPath}");

        // 5. POIs
        string poiPath = Path.Combine(outputDir, "poi.geojson");
        Console.WriteLine("[5/5] Extracting POIs & Fast Travel points...");
        var (poiSummary, pois) = PoiExtractor.Extract(wsw, alwaysLoadedSectors, grouped, parser, loc, spatialIndex, poiPath);
        Console.WriteLine($"  Exported {poiSummary.TotalPois} POIs (fast_travel={poiSummary.FastTravelCount}, metro={poiSummary.MetroCount}) -> {poiPath}");

        // 6. Write index_stats.json
        string statsPath = Path.Combine(outputDir, "index_stats.json");
        WriteStats(statsPath, gameDir, baseArchives, ep1Archives, regions, roadSummary, bldgSummary, buildings, poiSummary, pois, logger.ErrorCount, totalSw.Elapsed);
        Console.WriteLine($"Done in {totalSw.Elapsed.TotalSeconds:F1}s! Stats -> {statsPath}");
        return 0;
    }

    private static void WriteStats(
        string statsPath,
        string gameDir,
        List<IGameArchive> baseArchives,
        List<IGameArchive> ep1Archives,
        List<ExtractedRegion> regions,
        RoadsExtractionSummary roadSummary,
        BuildingsExtractionSummary bldgSummary,
        List<ExtractedBuilding> buildings,
        PoiExtractionSummary poiSummary,
        List<ExtractedPoi> pois,
        int rttiWarningsOrErrors,
        TimeSpan elapsed)
    {
        double minX = roadSummary.MinX, minY = roadSummary.MinY, maxX = roadSummary.MaxX, maxY = roadSummary.MaxY;
        foreach (var b in buildings)
        {
            minX = Math.Min(minX, b.Footprint.MinX);
            minY = Math.Min(minY, b.Footprint.MinY);
            maxX = Math.Max(maxX, b.Footprint.MaxX);
            maxY = Math.Max(maxY, b.Footprint.MaxY);
        }

        var buildingsByDistrict = buildings
            .GroupBy(b => b.District ?? "Poza dzielnicami / Badlands")
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());

        var poisByDistrict = pois
            .GroupBy(p => p.District ?? "Poza dzielnicami / Badlands")
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());

        var statsObj = new
        {
            generated_at = DateTimeOffset.UtcNow.ToString("O"),
            elapsed_seconds = Math.Round(elapsed.TotalSeconds, 2),
            game_directory = gameDir,
            coordinate_system = "REDengine 4 World Coordinates (meters)",
            language = "pl-pl (fallback en-us)",
            scope = "Base + Phantom Liberty (EP1)",
            archives_loaded = new
            {
                @base = baseArchives.Select(a => a.Name).ToArray(),
                ep1 = ep1Archives.Select(a => a.Name).ToArray()
            },
            world_bbox_2d = new[] { GeoMath.R2(minX), GeoMath.R2(minY), GeoMath.R2(maxX), GeoMath.R2(maxY) },
            counts = new
            {
                regions = regions.Count,
                surface_regions = regions.Count(r => !r.IsQuestDistrict && r.AreaM2 >= 2500),
                road_lanes = roadSummary.ExportedLanes,
                road_groups = roadSummary.NeighborGroupCount,
                road_bidirectional_groups = roadSummary.BidirectionalGroups,
                road_connections = roadSummary.TotalConnections,
                buildings = bldgSummary.ExportedBuildings,
                building_raw_candidates = bldgSummary.RawCandidates,
                poi = poiSummary.TotalPois,
                poi_fast_travel = poiSummary.FastTravelCount,
                poi_metro = poiSummary.MetroCount,
                non_fatal_rtti_errors = rttiWarningsOrErrors
            },
            buildings_by_type = bldgSummary.CountByType.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
            buildings_by_source = bldgSummary.CountBySource.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
            buildings_by_district = buildingsByDistrict,
            poi_by_category = poiSummary.CountByCategory.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
            poi_by_district = poisByDistrict,
            files = new
            {
                regions = "regions.geojson",
                roads = "roads.geojson",
                buildings = "buildings.geojson",
                poi = "poi.geojson",
                stats = "index_stats.json"
            }
        };

        var jsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        File.WriteAllText(statsPath, JsonSerializer.Serialize(statsObj, jsonOpts));
    }
}
