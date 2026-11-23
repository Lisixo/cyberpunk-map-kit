using System.Text.Json;
using WolvenKit.Core.Interfaces;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.Types;

namespace CyberpunkMapExtractor;

internal sealed record RoadsExtractionSummary(
    int TotalLanes,
    int ExportedLanes,
    int NeighborGroupCount,
    int BidirectionalGroups,
    int TotalConnections,
    double MinX,
    double MinY,
    double MaxX,
    double MaxY
);

internal static class RoadsExtractor
{
    public static RoadsExtractionSummary Extract(
        worldStreamingWorld wsw,
        IReadOnlyDictionary<ulong, IGameFile> grouped,
        Red4ParserService parser,
        DistrictSpatialIndex districts,
        string outputGeoJsonPath)
    {
        var pf = ArchiveHelpers.FindFile(wsw.TrafficPersistentResource.DepotPath, grouped)
            ?? throw new InvalidOperationException("TrafficPersistentResource not found.");
        var cf = ArchiveHelpers.FindFile(wsw.TrafficLaneConnectivityResource.DepotPath, grouped)
            ?? throw new InvalidOperationException("TrafficLaneConnectivityResource not found.");
        var polyf = ArchiveHelpers.FindFile(wsw.TrafficLanePolygonsResource.DepotPath, grouped)
            ?? throw new InvalidOperationException("TrafficLanePolygonsResource not found.");

        var tpr = (worldTrafficPersistentResource)ArchiveHelpers.ReadCr2w(pf, parser)!.RootChunk;
        var lcr = (worldTrafficPersistentLaneConnectionsResource)ArchiveHelpers.ReadCr2w(cf, parser)!.RootChunk;
        var lpr = (worldTrafficPersistentLanePolygonResource)ArchiveHelpers.ReadCr2w(polyf, parser)!.RootChunk;

        var lanes = tpr.Data?.Lanes ?? throw new InvalidOperationException("Traffic Data.Lanes is null.");
        var connByIdx = new Dictionary<int, worldTrafficPersistentLaneConnections>();
        if (lcr.Data != null)
        {
            foreach (var entry in lcr.Data)
            {
                if (entry.Value != null)
                    connByIdx[(ushort)entry.Index] = entry.Value;
            }
        }

        var polyByIdx = new Dictionary<int, worldTrafficLanePolygonRepresentation>();
        if (lpr.Data != null)
        {
            foreach (var entry in lpr.Data)
            {
                if (entry.Value != null)
                    polyByIdx[(ushort)entry.Index] = entry.Value;
            }
        }

        // Precompute NeighborGroup direction vectors to detect bidirectional multi-lane roads
        var groupDirs = new Dictionary<ushort, List<(double Dx, double Dy)>>();
        for (int i = 0; i < lanes.Count; i++)
        {
            if (!polyByIdx.TryGetValue(i, out var pr) || pr.Outline == null || pr.Outline.Count < 2) continue;
            var p0 = pr.Outline[0];
            var pN = pr.Outline[^1];
            double dx = (float)pN.X - (float)p0.X;
            double dy = (float)pN.Y - (float)p0.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.01) continue;

            ushort ng = lanes[i].NeighborGroupIndex;
            if (!groupDirs.TryGetValue(ng, out var dirs))
                groupDirs[ng] = dirs = new List<(double, double)>();
            dirs.Add((dx / len, dy / len));
        }

        var groupBidirectional = new Dictionary<ushort, bool>();
        foreach (var (ng, dirs) in groupDirs)
        {
            bool bidir = false;
            if (dirs.Count >= 2)
            {
                var refDir = dirs[0];
                for (int j = 1; j < dirs.Count; j++)
                {
                    double dot = refDir.Dx * dirs[j].Dx + refDir.Dy * dirs[j].Dy;
                    if (dot < -0.5)
                    {
                        bidir = true;
                        break;
                    }
                }
            }
            groupBidirectional[ng] = bidir;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputGeoJsonPath)!);
        using var fs = File.Create(outputGeoJsonPath);
        using var w = GeoJsonUtil.CreateWriter(fs, indented: false);
        GeoJsonUtil.BeginFeatureCollection(w, "cyberpunk2077_roads", "REDengine 4 World Coordinates (meters)");

        int exported = 0;
        int totalConnections = 0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

        for (int i = 0; i < lanes.Count; i++)
        {
            var lane = lanes[i];
            if (!polyByIdx.TryGetValue(i, out var pr) || pr.Outline == null || pr.Outline.Count < 2) continue;

            var coords = new List<(double X, double Y, double Z)>(pr.Outline.Count);
            foreach (var pt in pr.Outline)
            {
                double x = (float)pt.X, y = (float)pt.Y, z = (float)pt.Z;
                if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) continue;
                coords.Add((GeoMath.R3(x), GeoMath.R3(y), GeoMath.R3(z)));
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            if (coords.Count < 2) continue;

            var mid = coords[coords.Count / 2];
            var (district, subdistrict) = districts.Lookup(mid.X, mid.Y);

            ushort ng = lane.NeighborGroupIndex;
            int groupCount = groupDirs.TryGetValue(ng, out var gd) ? gd.Count : 1;
            bool roadBidir = groupBidirectional.GetValueOrDefault(ng, false);

            connByIdx.TryGetValue(i, out var conn);
            var inLanes = conn?.InLanes?.Select(x => (int)(ushort)x.LaneIndex).ToArray() ?? [];
            var outLanes = conn?.Outlanes?.Select(x => (int)(ushort)x.LaneIndex).ToArray() ?? [];
            totalConnections += outLanes.Length;

            w.WriteStartObject();
            w.WriteString("type", "Feature");
            w.WriteNumber("id", i);

            w.WriteStartObject("geometry");
            w.WriteString("type", "LineString");
            w.WriteStartArray("coordinates");
            foreach (var c in coords)
            {
                w.WriteStartArray();
                w.WriteNumberValue(c.X);
                w.WriteNumberValue(c.Y);
                w.WriteNumberValue(c.Z);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject("properties");
            w.WriteNumber("id", i);
            w.WriteString("direction", "oneway");
            w.WriteBoolean("road_bidirectional", roadBidir);
            w.WriteNumber("group_lane_count", groupCount);
            w.WriteNumber("neighbor_group_index", ng);
            w.WriteNumber("lane_number", (ushort)lane.LaneNumber);
            w.WriteNumber("seq_number", (ushort)lane.SeqNumber);
            w.WriteBoolean("is_reversed", (bool)lane.IsReversed);
            w.WriteNumber("length_m", GeoMath.R2((float)lane.Length));
            w.WriteNumber("width_m", GeoMath.R2((float)lane.Width));
            w.WriteNumber("max_speed", GeoMath.R2((float)lane.MaxSpeed));
            w.WriteNumber("flags", (uint)lane.Flags);
            w.WriteNumber("sub_graph_id", (uint)lane.SubGraphId);
            w.WriteBoolean("dead_end_start", (float)lane.DeadEndStart > 0.0f);

            w.WriteStartArray("in_lanes");
            foreach (int il in inLanes) w.WriteNumberValue(il);
            w.WriteEndArray();

            w.WriteStartArray("out_lanes");
            foreach (int ol in outLanes) w.WriteNumberValue(ol);
            w.WriteEndArray();

            w.WriteStartArray("road_materials");
            if (lane.RoadMaterials != null)
            {
                foreach (var rm in lane.RoadMaterials)
                    w.WriteStringValue(rm.Material.ToString());
            }
            w.WriteEndArray();

            if (district != null) w.WriteString("district", district);
            else w.WriteNull("district");
            if (subdistrict != null) w.WriteString("subdistrict", subdistrict);
            else w.WriteNull("subdistrict");

            w.WriteEndObject();
            w.WriteEndObject();
            exported++;
        }

        GeoJsonUtil.EndFeatureCollection(w);

        return new RoadsExtractionSummary(
            TotalLanes: lanes.Count,
            ExportedLanes: exported,
            NeighborGroupCount: groupDirs.Count,
            BidirectionalGroups: groupBidirectional.Count(kv => kv.Value),
            TotalConnections: totalConnections,
            MinX: GeoMath.R2(minX),
            MinY: GeoMath.R2(minY),
            MaxX: GeoMath.R2(maxX),
            MaxY: GeoMath.R2(maxY)
        );
    }
}
