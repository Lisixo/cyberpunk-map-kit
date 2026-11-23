using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using WolvenKit.Core.Interfaces;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.Types;
using RedQuaternion = WolvenKit.RED4.Types.Quaternion;
using RedTransform = WolvenKit.RED4.Types.Transform;
using RedVec3 = WolvenKit.RED4.Types.Vector3;

namespace CyberpunkMapExtractor;

internal sealed record FootprintResult(
    (double X, double Y)[] Ring,
    double CenterX,
    double CenterY,
    double BaseZ,
    double TopZ,
    double WidthM,
    double LengthM,
    double HeightM,
    double AreaM2,
    double RotationDeg,
    double MinX,
    double MinY,
    double MaxX,
    double MaxY
);

internal static class GeoMath
{
    public static double R3(double v) => Math.Round(v, 3);
    public static double R2(double v) => Math.Round(v, 2);
    public static double Dist2D(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

    public static Vector3d ToVec3(WorldPosition wp) => new((float)wp.X, (float)wp.Y, (float)wp.Z);
    public static Vector3d ToVec3(RedVec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    public static Vector3d TransformPoint(Vector3d local, RedTransform wt, RedVec3 scale)
    {
        double sx = local.X * (float)scale.X;
        double sy = local.Y * (float)scale.Y;
        double sz = local.Z * (float)scale.Z;

        var q = wt.Orientation;
        var qn = System.Numerics.Quaternion.Normalize(
            new System.Numerics.Quaternion((float)q.I, (float)q.J, (float)q.K, (float)q.R));
        var rotated = System.Numerics.Vector3.Transform(new System.Numerics.Vector3((float)sx, (float)sy, (float)sz), qn);

        var p = wt.Position;
        return new Vector3d(
            (double)(float)p.X + rotated.X,
            (double)(float)p.Y + rotated.Y,
            (double)(float)p.Z + rotated.Z);
    }

    public static FootprintResult? ComputeObbFootprint(Box localBox, RedTransform wt, RedVec3 scale)
    {
        float minX = localBox.Min.X, minY = localBox.Min.Y, minZ = localBox.Min.Z;
        float maxX = localBox.Max.X, maxY = localBox.Max.Y, maxZ = localBox.Max.Z;

        if (!IsFinite(minX) || !IsFinite(minY) || !IsFinite(minZ) ||
            !IsFinite(maxX) || !IsFinite(maxY) || !IsFinite(maxZ))
            return null;

        Span<Vector3d> corners = stackalloc Vector3d[8];
        corners[0] = TransformPoint(new(minX, minY, minZ), wt, scale);
        corners[1] = TransformPoint(new(maxX, minY, minZ), wt, scale);
        corners[2] = TransformPoint(new(maxX, maxY, minZ), wt, scale);
        corners[3] = TransformPoint(new(minX, maxY, minZ), wt, scale);
        corners[4] = TransformPoint(new(minX, minY, maxZ), wt, scale);
        corners[5] = TransformPoint(new(maxX, minY, maxZ), wt, scale);
        corners[6] = TransformPoint(new(maxX, maxY, maxZ), wt, scale);
        corners[7] = TransformPoint(new(minX, maxY, maxZ), wt, scale);

        double wMinZ = double.MaxValue, wMaxZ = double.MinValue;
        double wMinX = double.MaxValue, wMaxX = double.MinValue;
        double wMinY = double.MaxValue, wMaxY = double.MinValue;

        var pts2d = new List<(double X, double Y)>(8);
        for (int i = 0; i < 8; i++)
        {
            var c = corners[i];
            if (!double.IsFinite(c.X) || !double.IsFinite(c.Y) || !double.IsFinite(c.Z)) return null;
            if (c.Z < wMinZ) wMinZ = c.Z;
            if (c.Z > wMaxZ) wMaxZ = c.Z;
            if (c.X < wMinX) wMinX = c.X;
            if (c.X > wMaxX) wMaxX = c.X;
            if (c.Y < wMinY) wMinY = c.Y;
            if (c.Y > wMaxY) wMaxY = c.Y;
            pts2d.Add((c.X, c.Y));
        }

        var hull = ConvexHull2D(pts2d);
        if (hull.Count < 3) return null;

        var (rect, width, length, angleDeg) = MinimumAreaBoundingRect(hull);
        if (width <= 0 || length <= 0 || !double.IsFinite(width) || !double.IsFinite(length))
            return null;

        // Ensure width <= length consistently (or keep width/length clear)
        if (width > length)
        {
            (width, length) = (length, width);
            angleDeg = NormalizeDeg(angleDeg + 90.0);
        }

        double cx = 0.25 * (rect[0].X + rect[1].X + rect[2].X + rect[3].X);
        double cy = 0.25 * (rect[0].Y + rect[1].Y + rect[2].Y + rect[3].Y);

        var ring = new (double X, double Y)[5]
        {
            (R3(rect[0].X), R3(rect[0].Y)),
            (R3(rect[1].X), R3(rect[1].Y)),
            (R3(rect[2].X), R3(rect[2].Y)),
            (R3(rect[3].X), R3(rect[3].Y)),
            (R3(rect[0].X), R3(rect[0].Y)),
        };

        return new FootprintResult(
            Ring: ring,
            CenterX: R3(cx),
            CenterY: R3(cy),
            BaseZ: R3(wMinZ),
            TopZ: R3(wMaxZ),
            WidthM: R2(width),
            LengthM: R2(length),
            HeightM: R2(wMaxZ - wMinZ),
            AreaM2: R2(width * length),
            RotationDeg: R2(NormalizeDeg(angleDeg)),
            MinX: wMinX,
            MinY: wMinY,
            MaxX: wMaxX,
            MaxY: wMaxY
        );
    }

    private static double NormalizeDeg(double deg)
    {
        deg %= 180.0;
        if (deg < 0) deg += 180.0;
        return deg;
    }

    private static List<(double X, double Y)> ConvexHull2D(List<(double X, double Y)> pts)
    {
        var sorted = pts
            .OrderBy(p => p.X)
            .ThenBy(p => p.Y)
            .ToList();

        // Deduplicate near-identical points
        var uniq = new List<(double X, double Y)>(sorted.Count);
        foreach (var p in sorted)
        {
            if (uniq.Count == 0 || Math.Abs(p.X - uniq[^1].X) > 1e-4 || Math.Abs(p.Y - uniq[^1].Y) > 1e-4)
                uniq.Add(p);
        }
        if (uniq.Count < 3) return uniq;

        var lower = new List<(double X, double Y)>();
        foreach (var p in uniq)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }

        var upper = new List<(double X, double Y)>();
        for (int i = uniq.Count - 1; i >= 0; i--)
        {
            var p = uniq[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], p) <= 0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
        (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    private static ((double X, double Y)[] Corners, double Width, double Length, double AngleDeg)
        MinimumAreaBoundingRect(List<(double X, double Y)> hull)
    {
        double bestArea = double.MaxValue;
        double bestW = 0, bestL = 0, bestAngle = 0;
        (double X, double Y)[] bestCorners = new (double, double)[4];

        for (int i = 0; i < hull.Count; i++)
        {
            var p1 = hull[i];
            var p2 = hull[(i + 1) % hull.Count];
            double dx = p2.X - p1.X;
            double dy = p2.Y - p1.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) continue;

            double ux = dx / len, uy = dy / len;
            double vx = -uy, vy = ux;

            double minU = double.MaxValue, maxU = double.MinValue;
            double minV = double.MaxValue, maxV = double.MinValue;

            foreach (var p in hull)
            {
                double u = p.X * ux + p.Y * uy;
                double v = p.X * vx + p.Y * vy;
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }

            double w = maxU - minU;
            double h = maxV - minV;
            double area = w * h;
            if (area < bestArea)
            {
                bestArea = area;
                bestW = w;
                bestL = h;
                bestAngle = Math.Atan2(uy, ux) * 180.0 / Math.PI;
                bestCorners[0] = (minU * ux + minV * vx, minU * uy + minV * vy);
                bestCorners[1] = (maxU * ux + minV * vx, maxU * uy + minV * vy);
                bestCorners[2] = (maxU * ux + maxV * vx, maxU * uy + maxV * vy);
                bestCorners[3] = (minU * ux + maxV * vx, minU * uy + maxV * vy);
            }
        }

        return (bestCorners, bestW, bestL, bestAngle);
    }

    public static (List<(double X, double Y)> Ring, double ZMin, double ZMax) DecodeAreaOutline(
        AreaShapeOutline outline, RedTransform wt, RedVec3 scale)
    {
        var localPts = new List<Vector3d>();
        float height = outline.Height;

        byte[] bufBytes = (byte[])outline.Buffer;
        if (bufBytes != null && bufBytes.Length >= 4 + 3 * 16)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(bufBytes.AsSpan(0, 4));
            if (count >= 3 && 4 + count * 16 <= bufBytes.Length)
            {
                for (int i = 0; i < count; i++)
                {
                    int off = 4 + i * 16;
                    float x = BinaryPrimitives.ReadSingleLittleEndian(bufBytes.AsSpan(off, 4));
                    float y = BinaryPrimitives.ReadSingleLittleEndian(bufBytes.AsSpan(off + 4, 4));
                    float z = BinaryPrimitives.ReadSingleLittleEndian(bufBytes.AsSpan(off + 8, 4));
                    localPts.Add(new Vector3d(x, y, z));
                }
                if (4 + count * 16 + 4 <= bufBytes.Length)
                {
                    float bufH = BinaryPrimitives.ReadSingleLittleEndian(bufBytes.AsSpan(4 + count * 16, 4));
                    if (float.IsFinite(bufH) && bufH > 0.01f) height = bufH;
                }
            }
        }

        if (localPts.Count < 3 && outline.Points != null && outline.Points.Count >= 3)
        {
            foreach (var p in outline.Points)
                localPts.Add(new Vector3d(p.X, p.Y, p.Z));
        }

        var ring = new List<(double X, double Y)>(localPts.Count + 1);
        double zMin = double.MaxValue, zMax = double.MinValue;
        foreach (var lp in localPts)
        {
            var wp = TransformPoint(lp, wt, scale);
            if (!double.IsFinite(wp.X) || !double.IsFinite(wp.Y) || !double.IsFinite(wp.Z)) continue;
            ring.Add((R3(wp.X), R3(wp.Y)));
            if (wp.Z < zMin) zMin = wp.Z;
            double top = wp.Z + height * Math.Abs((float)scale.Z);
            if (top > zMax) zMax = top;
        }

        if (ring.Count >= 3 && ring[0] != ring[^1])
            ring.Add(ring[0]);

        if (zMin == double.MaxValue) zMin = (float)wt.Position.Z;
        if (zMax == double.MinValue) zMax = zMin + height * Math.Abs((float)scale.Z);

        return (ring, R3(zMin), R3(zMax));
    }

    public static double PolygonAreaM2(IReadOnlyList<(double X, double Y)> ring)
    {
        if (ring.Count < 3) return 0;
        double sum = 0;
        for (int i = 0; i < ring.Count - 1; i++)
            sum += ring[i].X * ring[i + 1].Y - ring[i + 1].X * ring[i].Y;
        return Math.Abs(sum) * 0.5;
    }

    public static bool PointInPolygon(double x, double y, IReadOnlyList<(double X, double Y)> ring)
    {
        bool inside = false;
        int n = ring.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[i].X, yi = ring[i].Y;
            double xj = ring[j].X, yj = ring[j].Y;
            if (((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / ((yj - yi) + 1e-18) + xi))
                inside = !inside;
        }
        return inside;
    }

    private static bool IsFinite(float f) => float.IsFinite(f);
}

internal readonly record struct Vector3d(double X, double Y, double Z);

internal static class ArchiveHelpers
{
    public static CR2WFile? ReadCr2w(IGameFile file, Red4ParserService parser)
    {
        using var ms = new MemoryStream((int)Math.Min(file.Size + 64, 16 * 1024 * 1024));
        file.Extract(ms);
        ms.Position = 0;
        return parser.TryReadRed4File(ms, out var cr2w) ? cr2w : null;
    }

    public static IGameFile? FindFile(ResourcePath path, IReadOnlyDictionary<ulong, IGameFile> grouped)
    {
        if (grouped.TryGetValue(path.GetRedHash(), out var f)) return f;
        string? txt = path.GetResolvedText();
        return string.IsNullOrEmpty(txt)
            ? null
            : grouped.Values.FirstOrDefault(x => x.FileName.Equals(txt, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class GeoJsonUtil
{
    public static Utf8JsonWriter CreateWriter(Stream fs, bool indented = false) =>
        new(fs, new JsonWriterOptions { Indented = indented });

    public static void BeginFeatureCollection(Utf8JsonWriter w, string name, string crsDescription)
    {
        w.WriteStartObject();
        w.WriteString("type", "FeatureCollection");
        w.WriteString("name", name);
        w.WriteStartObject("metadata");
        w.WriteString("coordinate_system", crsDescription);
        w.WriteString("units", "meters");
        w.WriteString("generated_at", DateTimeOffset.UtcNow.ToString("O"));
        w.WriteEndObject();
        w.WriteStartArray("features");
    }

    public static void EndFeatureCollection(Utf8JsonWriter w)
    {
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
    }
}
