using System.Text.Json;
using WolvenKit.Core.Interfaces;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.Types;

namespace CyberpunkMapExtractor;

internal sealed record ExtractedRegion(
    int Id,
    string DistrictId,
    string EnumName,
    string Name,
    string? ParentDistrictId,
    string? ParentDistrict,
    string TopDistrict,
    bool IsSubDistrict,
    bool IsQuestDistrict,
    List<(double X, double Y)> Ring,
    double AreaM2,
    double ZMin,
    double ZMax,
    double CenterX,
    double CenterY,
    double MinX,
    double MinY,
    double MaxX,
    double MaxY,
    string SourceSector
);

internal sealed class DistrictSpatialIndex
{
    private readonly List<ExtractedRegion> _dogtownRegions;
    private readonly List<ExtractedRegion> _surfaceRegions;
    private readonly LocalizationHelper _loc;

    public DistrictSpatialIndex(IEnumerable<ExtractedRegion> regions, LocalizationHelper loc)
    {
        _loc = loc;
        var validSurface = regions
            .Where(r => !r.IsQuestDistrict && r.AreaM2 >= 2500.0 && r.ZMax > -50.0 && r.ZMin < 400.0)
            .OrderBy(r => r.AreaM2)
            .ToList();

        _dogtownRegions = validSurface
            .Where(r => r.DistrictId.Equals("Districts.Dogtown", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Ring.Count)
            .ToList();

        _surfaceRegions = validSurface
            .Where(r => !r.DistrictId.Equals("Districts.Dogtown", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public (string? District, string? Subdistrict) Lookup(double x, double y)
    {
        foreach (var dt in _dogtownRegions)
        {
            if (x < dt.MinX || x > dt.MaxX || y < dt.MinY || y > dt.MaxY) continue;
            if (GeoMath.PointInPolygon(x, y, dt.Ring))
                return (dt.Name, null);
        }

        ExtractedRegion? smallestSub = null;
        ExtractedRegion? smallestTop = null;

        foreach (var r in _surfaceRegions)
        {
            if (x < r.MinX || x > r.MaxX || y < r.MinY || y > r.MaxY) continue;
            if (!GeoMath.PointInPolygon(x, y, r.Ring)) continue;

            if (!r.IsSubDistrict)
            {
                smallestTop ??= r;
            }
            else
            {
                smallestSub ??= r;
            }

            if (smallestSub != null && smallestTop != null) break;
        }

        if (smallestSub != null)
        {
            string topName = smallestSub.TopDistrict;
            string subName = smallestSub.Name;

            if (smallestSub.ParentDistrictId != null &&
                _loc.DistrictsById.TryGetValue(smallestSub.ParentDistrictId, out var parentMeta) &&
                parentMeta.ParentDistrictId != null)
            {
                subName = parentMeta.NamePl;
            }

            return (topName, subName);
        }

        if (smallestTop != null)
            return (smallestTop.Name, null);

        return (null, null);
    }

    public bool IsUrbanSurface(double x, double y)
    {
        foreach (var dt in _dogtownRegions)
        {
            if (x >= dt.MinX && x <= dt.MaxX && y >= dt.MinY && y <= dt.MaxY && GeoMath.PointInPolygon(x, y, dt.Ring))
                return true;
        }
        foreach (var r in _surfaceRegions)
        {
            if (x < r.MinX || x > r.MaxX || y < r.MinY || y > r.MaxY) continue;
            if (!GeoMath.PointInPolygon(x, y, r.Ring)) continue;
            if (r.DistrictId.Contains("Badlands", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.ParentDistrictId, "Districts.Badlands", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.ParentDistrictId, "Districts.SouthBadlands", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
        return false;
    }
}

internal static class RegionsExtractor
{
    public static (List<ExtractedRegion> Regions, DistrictSpatialIndex SpatialIndex) Extract(
        IReadOnlyList<(worldStreamingSectorDescriptor Desc, IGameFile File)> districtSectors,
        Red4ParserService parser,
        LocalizationHelper loc,
        string outputGeoJsonPath)
    {
        var raw = new List<ExtractedRegion>();
        int nextId = 1;

        foreach (var (desc, sf) in districtSectors)
        {
            var cr2w = ArchiveHelpers.ReadCr2w(sf, parser);
            if (cr2w?.RootChunk is not worldStreamingSector wss) continue;
            if (wss.NodeData?.Data is not worldNodeDataBuffer buf) continue;

            foreach (var nd in buf)
            {
                int ni = (int)(ulong)nd.NodeIdx;
                if (ni < 0 || ni >= wss.Nodes.Count) continue;
                if (wss.Nodes[ni]?.Chunk is not worldTriggerAreaNode tan) continue;
                var outline = tan.Outline?.Chunk;
                if (outline == null) continue;

                foreach (var nh in tan.Notifiers)
                {
                    if (nh?.Chunk is not worldLocationAreaNotifier locN) continue;

                    ulong distHash = (ulong)locN.DistrictID;
                    if (distHash == 0) continue;

                    var (ring, zMin, zMax) = GeoMath.DecodeAreaOutline(outline, nd.Transform, nd.Scale);
                    if (ring.Count < 4) continue;

                    double area = GeoMath.PolygonAreaM2(ring);
                    if (area < 1.0 || !double.IsFinite(area)) continue;

                    double minX = ring.Min(p => p.X);
                    double maxX = ring.Max(p => p.X);
                    double minY = ring.Min(p => p.Y);
                    double maxY = ring.Max(p => p.Y);

                    string distId = locN.DistrictID.GetResolvedText() ?? $"0x{distHash:X16}";
                    loc.DistrictsByHash.TryGetValue(distHash, out var meta);
                    if (meta == null && !string.IsNullOrEmpty(distId))
                        loc.DistrictsById.TryGetValue(distId, out meta);

                    string name = meta?.NamePl ?? LocalizationHelper.CleanDisplayName(distId.Replace("Districts.", ""));
                    string enumName = meta?.EnumName ?? distId.Replace("Districts.", "");
                    string? parentId = meta?.ParentDistrictId;
                    string? parentName = meta?.ParentNamePl;
                    string topDistrict = ResolveTopDistrictName(meta, name, loc);
                    bool isQuest = (meta?.IsQuestDistrict ?? distId.Contains("Cyberspace", StringComparison.OrdinalIgnoreCase))
                                   || zMax < -50.0 || zMin > 400.0;
                    bool isSub = meta?.IsSubDistrict ?? false;

                    // Centroid (excluding duplicate closing point)
                    int cnt = ring.Count - 1;
                    double cx = ring.Take(cnt).Average(p => p.X);
                    double cy = ring.Take(cnt).Average(p => p.Y);

                    raw.Add(new ExtractedRegion(
                        Id: nextId++,
                        DistrictId: meta?.DistrictId ?? distId,
                        EnumName: enumName,
                        Name: name,
                        ParentDistrictId: parentId,
                        ParentDistrict: parentName,
                        TopDistrict: topDistrict,
                        IsSubDistrict: isSub,
                        IsQuestDistrict: isQuest,
                        Ring: ring,
                        AreaM2: GeoMath.R2(area),
                        ZMin: zMin,
                        ZMax: zMax,
                        CenterX: GeoMath.R3(cx),
                        CenterY: GeoMath.R3(cy),
                        MinX: GeoMath.R3(minX),
                        MinY: GeoMath.R3(minY),
                        MaxX: GeoMath.R3(maxX),
                        MaxY: GeoMath.R3(maxY),
                        SourceSector: sf.FileName
                    ));
                }
            }
        }

        // Deduplicate overlapping triggers of the same DistrictId
        var deduped = Deduplicate(raw);
        for (int i = 0; i < deduped.Count; i++)
            deduped[i] = deduped[i] with { Id = i + 1 };

        WriteGeoJson(deduped, outputGeoJsonPath);
        var spatial = new DistrictSpatialIndex(deduped, loc);
        return (deduped, spatial);
    }

    private static string ResolveTopDistrictName(DistrictMeta? meta, string fallbackName, LocalizationHelper loc)
    {
        if (meta == null) return fallbackName;
        var cur = meta;
        for (int guard = 0; guard < 5 && cur.ParentDistrictId != null; guard++)
        {
            if (loc.DistrictsById.TryGetValue(cur.ParentDistrictId, out var p))
                cur = p;
            else
                break;
        }
        return cur.NamePl;
    }

    private static List<ExtractedRegion> Deduplicate(List<ExtractedRegion> raw)
    {
        var kept = new List<ExtractedRegion>();
        foreach (var r in raw.OrderByDescending(x => x.SourceSector.Contains(@"\ep1\", StringComparison.OrdinalIgnoreCase))
                             .ThenByDescending(x => x.Ring.Count)
                             .ThenByDescending(x => x.AreaM2))
        {
            bool dup = false;
            foreach (var k in kept)
            {
                if (!string.Equals(k.DistrictId, r.DistrictId, StringComparison.OrdinalIgnoreCase)) continue;
                double dist = GeoMath.Dist2D(k.CenterX - r.CenterX, k.CenterY - r.CenterY);
                double maxA = Math.Max(k.AreaM2, r.AreaM2);
                double relAreaDiff = Math.Abs(k.AreaM2 - r.AreaM2) / Math.Max(1.0, maxA);
                if (dist < 20.0 && relAreaDiff < 0.08)
                {
                    dup = true;
                    break;
                }

                // Also deduplicate overlapping base vs EP1-carved polygons of major districts (e.g. Coastview, WestWindEstate)
                double ix = Math.Min(k.MaxX, r.MaxX) - Math.Max(k.MinX, r.MinX);
                double iy = Math.Min(k.MaxY, r.MaxY) - Math.Max(k.MinY, r.MinY);
                if (ix > 0 && iy > 0)
                {
                    double interArea = ix * iy;
                    double minBoxArea = Math.Min((k.MaxX - k.MinX) * (k.MaxY - k.MinY), (r.MaxX - r.MinX) * (r.MaxY - r.MinY));
                    if (minBoxArea > 10000.0 && interArea / minBoxArea >= 0.60)
                    {
                        dup = true;
                        break;
                    }
                }
            }
            if (!dup) kept.Add(r);
        }
        return kept.OrderByDescending(x => x.AreaM2).ToList();
    }

    private static void WriteGeoJson(List<ExtractedRegion> regions, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var w = GeoJsonUtil.CreateWriter(fs, indented: false);

        GeoJsonUtil.BeginFeatureCollection(w, "cyberpunk2077_regions", "REDengine 4 World Coordinates (meters)");
        foreach (var r in regions)
        {
            w.WriteStartObject();
            w.WriteString("type", "Feature");
            w.WriteNumber("id", r.Id);

            w.WriteStartObject("geometry");
            w.WriteString("type", "Polygon");
            w.WriteStartArray("coordinates");
            w.WriteStartArray();
            foreach (var pt in r.Ring)
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
            w.WriteNumber("id", r.Id);
            w.WriteString("district_id", r.DistrictId);
            w.WriteString("enum_name", r.EnumName);
            w.WriteString("name", r.Name);
            if (r.ParentDistrictId != null) w.WriteString("parent_district_id", r.ParentDistrictId);
            else w.WriteNull("parent_district_id");
            if (r.ParentDistrict != null) w.WriteString("parent_district", r.ParentDistrict);
            else w.WriteNull("parent_district");
            w.WriteString("top_district", r.TopDistrict);
            w.WriteBoolean("is_subdistrict", r.IsSubDistrict);
            w.WriteBoolean("is_quest_district", r.IsQuestDistrict);
            w.WriteNumber("area_m2", r.AreaM2);
            w.WriteNumber("z_min", r.ZMin);
            w.WriteNumber("z_max", r.ZMax);
            w.WriteStartArray("center");
            w.WriteNumberValue(r.CenterX);
            w.WriteNumberValue(r.CenterY);
            w.WriteEndArray();
            w.WriteStartArray("bbox");
            w.WriteNumberValue(r.MinX);
            w.WriteNumberValue(r.MinY);
            w.WriteNumberValue(r.MaxX);
            w.WriteNumberValue(r.MaxY);
            w.WriteEndArray();
            w.WriteString("source_sector", r.SourceSector);
            w.WriteEndObject();

            w.WriteEndObject();
        }
        GeoJsonUtil.EndFeatureCollection(w);
    }
}
