using System.Collections.Concurrent;
using System.Text.Json;
using WolvenKit.Core.Interfaces;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.Types;

namespace CyberpunkMapExtractor;

internal sealed record BuildingCandidate(
    int Priority,
    string SourceType,
    string MeshPath,
    string Name,
    string? BuildingType,
    FootprintResult Footprint
);

internal sealed record ExtractedBuilding(
    int Id,
    string Name,
    string? BuildingType,
    string SourceType,
    string MeshPath,
    string? District,
    string? Subdistrict,
    FootprintResult Footprint
);

internal sealed record BuildingsExtractionSummary(
    int SectorsParsed,
    int RawCandidates,
    int ExportedBuildings,
    Dictionary<string, int> CountByType,
    Dictionary<string, int> CountBySource
);

internal static class BuildingsExtractor
{
    private static readonly string[] ExcludeKeywords =
    [
        "terrain", "road", "highway", "intersection", "splinenode", "water", "ocean", "sea",
        "foliage", "vegetation", "tree", "bush", "grass", "jungle", "ramon", "ficus", "niwaki", "palm",
        "rock", "cliff", "boulder", "decal", "billboard", "sign", "neon_sign", "cable", "wire", "pole", "lamp",
        "vehicle", "car_", "truck", "trash", "debris", "fence", "curb", "sidewalk", "pavement",
        "skybox", "cloud", "fog", "vfx", @"\fx\", "cyberspace", "mikoshi", "spotlight", "beam",
        "landfill", "border_wall", "waterbreak", "tunnel", "overpass", "bridge", "electrictowers",
        "combat_zone.mesh", "plane_occluder", "fake_occluder", "vending_machine", "cladding",
        "foundation", "exit_pipes", "rail", "stair", "catwalk", "scaffold", "antenna_wire",
        "vr_tutorial", "orbital_station", "_wall", "side_wall", "riverbed", "weapon_placeholder",
        "fountain", "_gate_", "cargo_markers", "advert", "roundabout", "balcony", "arch.mesh",
        "crossing_", "platform_exterior", "comm_tower", "decoset_"
    ];

    private static readonly string[] GenericIncludeKeywords =
    [
        "building", "megabuilding", "hotel", "tower", "apartment", "apart_", "house",
        "warehouse", "factory", "mall", "garage", "office", "clinic", "hospital",
        "church", "temple", "club", "casino", "depot", "hangar", "stadium",
        "plaza", "shack", "motel", "store", "market", "station", "skyscraper", "mproxy",
        "bldg", "roof", "penthouse", "estate", "block_"
    ];

    public static (BuildingsExtractionSummary Summary, List<ExtractedBuilding> Buildings) Extract(
        IReadOnlyList<(worldStreamingSectorDescriptor Desc, IGameFile File)> buildingSectors,
        IReadOnlyDictionary<ulong, IGameFile> grouped,
        Red4ParserService parser,
        DistrictSpatialIndex districts,
        string outputGeoJsonPath)
    {
        var meshBoxCache = new Dictionary<ulong, Box?>();
        var candidates = new List<BuildingCandidate>();
        int sectorsParsed = 0;

        Console.WriteLine($"BuildingsExtractor: parsing {buildingSectors.Count} exterior candidate sectors...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var (desc, sf) in buildingSectors)
        {
            sectorsParsed++;
            if (sectorsParsed % 1000 == 0)
                Console.WriteLine($"  Buildings sector {sectorsParsed}/{buildingSectors.Count}, rawCandidates={candidates.Count} ({sw.ElapsedMilliseconds}ms)");

            var cr2w = ArchiveHelpers.ReadCr2w(sf, parser);
            if (cr2w?.RootChunk is not worldStreamingSector wss) continue;
            if (wss.NodeData?.Data is not worldNodeDataBuffer buf) continue;

            byte level = (byte)desc.Level;

            foreach (var nd in buf)
            {
                int ni = (int)(ulong)nd.NodeIdx;
                if (ni < 0 || ni >= wss.Nodes.Count) continue;
                var node = wss.Nodes[ni]?.Chunk;
                if (node == null) continue;

                ResourcePath? meshPath = null;
                int priority = 0;
                Type nodeType = node.GetType();
                string sourceType = nodeType.Name;

                if (nodeType == typeof(worldBuildingProxyMeshNode))
                {
                    meshPath = ((worldBuildingProxyMeshNode)node).Mesh.DepotPath;
                    priority = 4;
                }
                else if (nodeType == typeof(worldCookedBuildingProxyMeshNode) &&
                         ((worldCookedBuildingProxyMeshNode)node).Buffer.Data is worldCookedGenericProxyMeshNode_Buffer cbpb)
                {
                    int ii = (int)(uint)nd.InstanceIndex;
                    if (ii >= 0 && ii < cbpb.InnerNodes.Count)
                    {
                        meshPath = cbpb.InnerNodes[ii].Mesh.DepotPath;
                        priority = 4;
                    }
                }
                else if (nodeType == typeof(worldPrefabProxyMeshNode))
                {
                    meshPath = ((worldPrefabProxyMeshNode)node).Mesh.DepotPath;
                    priority = 3;
                }
                else if (nodeType == typeof(worldCookedPrefabProxyMeshNode) &&
                         ((worldCookedPrefabProxyMeshNode)node).Buffer.Data is worldCookedGenericProxyMeshNode_Buffer cppb)
                {
                    int ii = (int)(uint)nd.InstanceIndex;
                    if (ii >= 0 && ii < cppb.InnerNodes.Count)
                    {
                        meshPath = cppb.InnerNodes[ii].Mesh.DepotPath;
                        priority = 3;
                    }
                }
                else if (nodeType == typeof(worldCookedGenericProxyMeshNode) &&
                         ((worldCookedGenericProxyMeshNode)node).Buffer.Data is worldCookedGenericProxyMeshNode_Buffer gpb)
                {
                    int ii = (int)(uint)nd.InstanceIndex;
                    if (ii >= 0 && ii < gpb.InnerNodes.Count)
                    {
                        meshPath = gpb.InnerNodes[ii].Mesh.DepotPath;
                        priority = 2;
                    }
                }
                else if (level <= 4 && nodeType == typeof(worldStaticOccluderMeshNode))
                {
                    meshPath = ((worldStaticOccluderMeshNode)node).Mesh.DepotPath;
                    priority = 1;
                }
                else if (level >= 1 && nodeType == typeof(worldCookedStaticMeshNode) &&
                         ((worldCookedStaticMeshNode)node).Buffer.Data is worldCookedStaticMeshNodeBuffer smb)
                {
                    int ii = (int)(uint)nd.InstanceIndex;
                    if (ii >= 0 && ii < smb.InnerNodes.Count)
                    {
                        meshPath = smb.InnerNodes[ii].Mesh.DepotPath;
                        priority = 2;
                    }
                }
                else if (level >= 1 && nodeType == typeof(worldInstancedMeshNode) &&
                         ((worldInstancedMeshNode)node).Buffer.Data is worldInstancedMeshNodeBuffer imb)
                {
                    int ii = (int)(uint)nd.InstanceIndex;
                    if (ii >= 0 && ii < imb.InnerNodes.Count)
                    {
                        meshPath = imb.InnerNodes[ii].Mesh.DepotPath;
                        priority = 2;
                    }
                }

                if (meshPath == null) continue;
                ulong mh = meshPath.Value.GetRedHash();
                if (mh == 0) continue;

                string mstr = meshPath.Value.GetResolvedText() ?? $"0x{mh:X16}";
                bool isBoxOccluder = mstr.EndsWith("box_occluder.w2mesh", StringComparison.OrdinalIgnoreCase);

                if (priority == 1)
                {
                    if (!isBoxOccluder) continue; // skip plane_occluder etc.
                }
                else
                {
                    if (ContainsAny(mstr, ExcludeKeywords)) continue;

                    if (sourceType == nameof(worldCookedGenericProxyMeshNode) && !ContainsAny(mstr, GenericIncludeKeywords))
                        continue;

                    if ((sourceType == nameof(worldCookedStaticMeshNode) || sourceType == nameof(worldInstancedMeshNode)) &&
                        !mstr.Contains(@"\architecture\", StringComparison.OrdinalIgnoreCase) &&
                        !mstr.Contains(@"\buildings\", StringComparison.OrdinalIgnoreCase) &&
                        !mstr.Contains("megabuilding", StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                var pos = nd.Transform.Position;
                double px = (float)pos.X, py = (float)pos.Y, pz = (float)pos.Z;
                if (!double.IsFinite(px) || !double.IsFinite(py) || !double.IsFinite(pz)) continue;
                if (pz < -120 || pz > 900) continue; // exclude underground cyberspace/mikoshi test cells

                var box = GetMeshBoundingBox(meshPath.Value, mh, grouped, parser, meshBoxCache);
                if (box == null) continue;

                var fp = GeoMath.ComputeObbFootprint(box, nd.Transform, nd.Scale);
                if (fp == null) continue;

                // Reasonable building dimension filter
                double minDim = priority == 1 ? 6.0 : 5.0;
                double minH = priority == 1 ? 4.5 : 3.5;
                if (fp.WidthM < minDim || fp.LengthM < minDim || fp.HeightM < minH || fp.AreaM2 < 30.0)
                    continue;
                if (priority == 1)
                {
                    if (fp.WidthM > 120.0 || fp.LengthM > 120.0 || fp.HeightM > 130.0)
                        continue;
                    if (!districts.IsUrbanSurface(fp.CenterX, fp.CenterY))
                        continue;
                }
                else if (fp.WidthM > 280.0 || fp.LengthM > 280.0 || fp.HeightM > 600.0)
                {
                    continue;
                }

                string? btype = ClassifyBuilding(mstr, fp, isBoxOccluder);
                string bname = isBoxOccluder
                    ? $"Building ({Math.Round(fp.WidthM)}x{Math.Round(fp.LengthM)}x{Math.Round(fp.HeightM)}m)"
                    : CleanMeshName(mstr);

                candidates.Add(new BuildingCandidate(
                    Priority: priority,
                    SourceType: sourceType,
                    MeshPath: mstr,
                    Name: bname,
                    BuildingType: btype,
                    Footprint: fp
                ));
            }
        }

        Console.WriteLine($"Deduplicating {candidates.Count} building candidates...");
        var deduped = Deduplicate(candidates, districts);
        Console.WriteLine($"Exporting {deduped.Count} deduplicated buildings to {outputGeoJsonPath}...");
        WriteGeoJson(deduped, outputGeoJsonPath);

        var byType = deduped
            .GroupBy(b => b.BuildingType ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());
        var bySource = deduped
            .GroupBy(b => b.SourceType)
            .ToDictionary(g => g.Key, g => g.Count());

        var summary = new BuildingsExtractionSummary(
            SectorsParsed: sectorsParsed,
            RawCandidates: candidates.Count,
            ExportedBuildings: deduped.Count,
            CountByType: byType,
            CountBySource: bySource
        );
        return (summary, deduped);
    }

    private static Box? GetMeshBoundingBox(
        ResourcePath path,
        ulong hash,
        IReadOnlyDictionary<ulong, IGameFile> grouped,
        Red4ParserService parser,
        Dictionary<ulong, Box?> cache)
    {
        if (cache.TryGetValue(hash, out var cached)) return cached;

        Box? result = null;
        var mf = ArchiveHelpers.FindFile(path, grouped);
        if (mf != null)
        {
            using var ms = new MemoryStream((int)Math.Min(mf.Size + 64, 4 * 1024 * 1024));
            mf.Extract(ms);
            ms.Position = 0;
            using var br = new BinaryReader(ms);
            if (parser.TryReadRed4File(br, false, out var cr2w) && cr2w?.RootChunk is CMesh cm)
            {
                result = cm.BoundingBox;
            }
        }
        cache[hash] = result;
        return result;
    }

    private static List<ExtractedBuilding> Deduplicate(List<BuildingCandidate> candidates, DistrictSpatialIndex districts)
    {
        // Sort highest priority first, then larger footprint
        var ordered = candidates
            .OrderByDescending(c => c.Priority)
            .ThenByDescending(c => c.Footprint.AreaM2)
            .ToList();

        const double cellSize = 40.0;
        var grid = new Dictionary<(int, int), List<int>>();
        var kept = new List<BuildingCandidate>(ordered.Count / 2);
        var neighborSeen = new HashSet<int>();

        foreach (var cand in ordered)
        {
            var fp = cand.Footprint;
            int gx0 = (int)Math.Floor(fp.MinX / cellSize);
            int gx1 = (int)Math.Floor(fp.MaxX / cellSize);
            int gy0 = (int)Math.Floor(fp.MinY / cellSize);
            int gy1 = (int)Math.Floor(fp.MaxY / cellSize);

            neighborSeen.Clear();
            bool isDuplicate = false;

            for (int gx = gx0; gx <= gx1 && !isDuplicate; gx++)
            {
                for (int gy = gy0; gy <= gy1 && !isDuplicate; gy++)
                {
                    if (!grid.TryGetValue((gx, gy), out var indices)) continue;
                    foreach (int idx in indices)
                    {
                        if (!neighborSeen.Add(idx)) continue;
                        var existing = kept[idx];
                        if (OverlapsSignificantly(fp, existing.Footprint))
                        {
                            isDuplicate = true;
                            // If kept was an anonymous box_occluder and candidate had a real name/type (rare given priority sort), upgrade metadata
                            if (existing.Priority == 1 && cand.Priority > 1 && existing.BuildingType == null && cand.BuildingType != null)
                            {
                                kept[idx] = existing with { BuildingType = cand.BuildingType };
                            }
                            break;
                        }
                    }
                }
            }

            if (!isDuplicate)
            {
                int newIdx = kept.Count;
                kept.Add(cand);
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    for (int gy = gy0; gy <= gy1; gy++)
                    {
                        var key = (gx, gy);
                        if (!grid.TryGetValue(key, out var list))
                            grid[key] = list = new List<int>(4);
                        list.Add(newIdx);
                    }
                }
            }
        }

        var result = new List<ExtractedBuilding>(kept.Count);
        for (int i = 0; i < kept.Count; i++)
        {
            var c = kept[i];
            var (dist, sub) = districts.Lookup(c.Footprint.CenterX, c.Footprint.CenterY);
            string name = c.Name;
            if (c.Priority == 1 && (sub ?? dist) != null)
                name = $"{sub ?? dist} — Budynek #{i + 1}";

            result.Add(new ExtractedBuilding(
                Id: i + 1,
                Name: name,
                BuildingType: c.BuildingType,
                SourceType: c.SourceType,
                MeshPath: c.MeshPath,
                District: dist,
                Subdistrict: sub,
                Footprint: c.Footprint
            ));
        }

        return result;
    }

    private static bool OverlapsSignificantly(FootprintResult a, FootprintResult b)
    {
        // Z check: must overlap vertically or be within 6m vertically
        double zOverlap = Math.Min(a.TopZ, b.TopZ) - Math.Max(a.BaseZ, b.BaseZ);
        if (zOverlap < -6.0) return false;

        double ix = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
        if (ix <= 0) return false;
        double iy = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
        if (iy <= 0) return false;

        double interArea = ix * iy;
        double aabbA = (a.MaxX - a.MinX) * (a.MaxY - a.MinY);
        double aabbB = (b.MaxX - b.MinX) * (b.MaxY - b.MinY);
        double minAabb = Math.Min(aabbA, aabbB);
        if (minAabb <= 0) return false;

        double overlapRatio = interArea / minAabb;
        if (overlapRatio >= 0.50) return true;

        double dist = GeoMath.Dist2D(a.CenterX - b.CenterX, a.CenterY - b.CenterY);
        return dist < 7.0 && overlapRatio >= 0.25;
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        foreach (var kw in keywords)
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string? ClassifyBuilding(string meshPath, FootprintResult fp, bool isBoxOccluder)
    {
        string lower = meshPath.ToLowerInvariant();
        if (lower.Contains("megabuilding")) return "megabuilding";
        if (fp.HeightM >= 130 && fp.AreaM2 >= 3500) return "megabuilding";
        if (fp.HeightM >= 80 || lower.Contains("skyscraper") || lower.Contains("tower")) return "skyscraper";

        if (isBoxOccluder)
        {
            if (fp.HeightM >= 45) return "highrise";
            return null; // type unknown for anonymous low/mid occluder
        }

        if (lower.Contains("arasaka") || lower.Contains("militech") || lower.Contains("kangtao") ||
            lower.Contains("biotechnica") || lower.Contains("corpo") || lower.Contains("cpz") ||
            lower.Contains("konpeki") || lower.Contains("ncpd") || lower.Contains("trauma") ||
            lower.Contains("netwatch") || lower.Contains("office"))
            return "corporate";

        if (lower.Contains("industrial") || lower.Contains("factory") || lower.Contains("warehouse") ||
            lower.Contains("depot") || lower.Contains("refinery") || lower.Contains("tank_") ||
            lower.Contains("silo") || lower.Contains("hangar") || lower.Contains("dock") ||
            lower.Contains("nid_"))
            return "industrial";

        if (lower.Contains("hotel") || lower.Contains("mall") || lower.Contains("shop") ||
            lower.Contains("store") || lower.Contains("club") || lower.Contains("bar_") ||
            lower.Contains("casino") || lower.Contains("motel") || lower.Contains("diner") ||
            lower.Contains("market") || lower.Contains("clinic") || lower.Contains("hospital") ||
            lower.Contains("stadium") || lower.Contains("garage"))
            return "commercial";

        if (lower.Contains("apart") || lower.Contains("house") || lower.Contains("residential") ||
            lower.Contains("villa") || lower.Contains("estate") || lower.Contains("shack") ||
            lower.Contains("trailer") || lower.Contains("hab_"))
            return "residential";

        if (lower.Contains("station") || lower.Contains("metro") || lower.Contains("substation") ||
            lower.Contains("dam") || lower.Contains("checkpoint") || lower.Contains("border"))
            return "infrastructure";

        if (fp.HeightM >= 45) return "highrise";
        return null;
    }

    private static string CleanMeshName(string meshPath)
    {
        string fn = Path.GetFileNameWithoutExtension(meshPath);
        return LocalizationHelper.CleanDisplayName(fn);
    }

    private static void WriteGeoJson(List<ExtractedBuilding> buildings, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var w = GeoJsonUtil.CreateWriter(fs, indented: false);
        GeoJsonUtil.BeginFeatureCollection(w, "cyberpunk2077_buildings", "REDengine 4 World Coordinates (meters)");

        foreach (var b in buildings)
        {
            var fp = b.Footprint;
            w.WriteStartObject();
            w.WriteString("type", "Feature");
            w.WriteNumber("id", b.Id);

            w.WriteStartObject("geometry");
            w.WriteString("type", "Polygon");
            w.WriteStartArray("coordinates");
            w.WriteStartArray();
            foreach (var pt in fp.Ring)
            {
                w.WriteStartArray();
                w.WriteNumberValue(pt.X);
                w.WriteNumberValue(pt.Y);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject("properties");
            w.WriteNumber("id", b.Id);
            w.WriteString("name", b.Name);
            if (b.BuildingType != null) w.WriteString("type", b.BuildingType);
            else w.WriteNull("type");

            w.WriteStartArray("location");
            w.WriteNumberValue(fp.CenterX);
            w.WriteNumberValue(fp.CenterY);
            w.WriteNumberValue(fp.BaseZ);
            w.WriteEndArray();

            w.WriteNumber("width_m", fp.WidthM);
            w.WriteNumber("length_m", fp.LengthM);
            w.WriteNumber("height_m", fp.HeightM);
            w.WriteNumber("area_m2", fp.AreaM2);
            w.WriteNumber("base_z", fp.BaseZ);
            w.WriteNumber("top_z", fp.TopZ);
            w.WriteNumber("rotation_deg", fp.RotationDeg);

            if (b.District != null) w.WriteString("district", b.District);
            else w.WriteNull("district");
            if (b.Subdistrict != null) w.WriteString("subdistrict", b.Subdistrict);
            else w.WriteNull("subdistrict");

            w.WriteString("source_type", b.SourceType);
            w.WriteString("mesh_path", b.MeshPath);
            w.WriteEndObject();

            w.WriteEndObject();
        }

        GeoJsonUtil.EndFeatureCollection(w);
    }
}
