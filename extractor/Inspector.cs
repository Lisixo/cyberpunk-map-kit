using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using WolvenKit.Common.Services;
using WolvenKit.Core.Interfaces;
using WolvenKit.Core.Murmur3;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.CR2W.Archive;
using WolvenKit.RED4.Types;

namespace CyberpunkMapExtractor;

internal static class Inspector
{
    private const string GameExePath = @"Q:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe";
    private const string TweakDbEp1Path = @"Q:\SteamLibrary\steamapps\common\Cyberpunk 2077\r6\cache\tweakdb_ep1.bin";

    public static async Task RunAsync(bool verbose = false)
    {
        var sw = Stopwatch.StartNew();
        var logger = new ConsoleLogger(verbose);
        var hashService = new HashService();
        hashService.Load();

        var hookService = new HookService();
        var parser = new Red4ParserService(hashService, logger, hookService);
        var progress = new PercentProgressService();
        var archiveManager = new ArchiveManager(hashService, parser, logger, progress);
        archiveManager.LoadGameArchives(new FileInfo(GameExePath));

        var grouped = new Dictionary<ulong, IGameFile>();
        foreach (var arch in archiveManager.GetBaseArchives().Concat(archiveManager.GetEp1Archives()))
            foreach (var kvp in arch.Files)
                grouped[kvp.Key] = kvp.Value;

        var tdb = new TweakDBService(hashService);
        await tdb.LoadDB(TweakDbEp1Path);

        var ncSwFile = grouped.Values.First(f => f.FileName.Equals(@"base\worlds\03_night_city\_compiled\default\03_night_city.streamingworld", StringComparison.OrdinalIgnoreCase));
        var wsw = (worldStreamingWorld)ReadCr2w(ncSwFile, parser)!.RootChunk;

        var allDescriptors = new List<worldStreamingSectorDescriptor>();
        foreach (var blockRef in wsw.BlockRefs)
        {
            var bf = FindFile(blockRef.DepotPath, grouped);
            if (bf != null && ReadCr2w(bf, parser)?.RootChunk is worldStreamingBlock wsb)
                allDescriptors.AddRange(wsb.Descriptors);
        }

        InspectLocationResource(wsw.LocationResource.DepotPath, grouped, parser);
        InspectTraffic(wsw, grouped, parser);
        InspectMappinsAndJournal(wsw, grouped, parser);
        InspectSampleSectors(allDescriptors, grouped, parser);
        Console.WriteLine($"Inspect finished in {sw.ElapsedMilliseconds}ms");
    }

    public static async Task ScanAllSectorsAsync(bool verbose = false)
    {
        var sw = Stopwatch.StartNew();
        var logger = new ConsoleLogger(verbose);
        var hashService = new HashService();
        hashService.Load();

        var hookService = new HookService();
        var parser = new Red4ParserService(hashService, logger, hookService);
        var progress = new PercentProgressService();
        var archiveManager = new ArchiveManager(hashService, parser, logger, progress);
        archiveManager.LoadGameArchives(new FileInfo(GameExePath));

        var grouped = new Dictionary<ulong, IGameFile>();
        foreach (var arch in archiveManager.GetBaseArchives().Concat(archiveManager.GetEp1Archives()))
            foreach (var kvp in arch.Files)
                grouped[kvp.Key] = kvp.Value;

        var ncSwFile = grouped.Values.First(f => f.FileName.Equals(@"base\worlds\03_night_city\_compiled\default\03_night_city.streamingworld", StringComparison.OrdinalIgnoreCase));
        var wsw = (worldStreamingWorld)ReadCr2w(ncSwFile, parser)!.RootChunk;

        var allDescriptors = new List<worldStreamingSectorDescriptor>();
        foreach (var blockRef in wsw.BlockRefs)
        {
            var bf = FindFile(blockRef.DepotPath, grouped);
            if (bf != null && ReadCr2w(bf, parser)?.RootChunk is worldStreamingBlock wsb)
                allDescriptors.AddRange(wsb.Descriptors);
        }

        var uniqueDescriptors = allDescriptors
            .Where(d => d.Category.ToString() != "Navigation")
            .GroupBy(d => d.Data.DepotPath.GetRedHash())
            .Select(g => g.First())
            .ToList();

        Console.WriteLine($"Scanning {uniqueDescriptors.Count} non-navigation sectors (init={sw.ElapsedMilliseconds}ms)...");

        string[] watchTypes =
        [
            "worldLocationAreaNotifier",
            "worldLocationAreaNode",
            "worldBuildingProxyMeshNode",
            "worldCookedBuildingProxyMeshNode",
            "worldPrefabProxyMeshNode",
            "worldCookedPrefabProxyMeshNode",
            "worldGenericProxyMeshNode",
            "worldCookedGenericProxyMeshNode",
            "worldStaticOccluderMeshNode",
            "worldInstancedOccluderNode",
            "worldStaticMeshNode",
            "worldCookedStaticMeshNode",
            "worldInstancedMeshNode",
            "gameFastTravelPointData"
        ];

        var sectorHitsByCatLvl = new Dictionary<string, int>();
        var districtSectors = new List<(string CatLvl, IGameFile File)>();
        var ftSectors = new List<(string CatLvl, IGameFile File)>();
        int processed = 0;

        using var ms = new MemoryStream(1024 * 1024);
        foreach (var d in uniqueDescriptors)
        {
            var sf = FindFile(d.Data.DepotPath, grouped);
            if (sf == null) continue;
            processed++;
            if (processed % 4000 == 0)
                Console.WriteLine($"  Header scan {processed}/{uniqueDescriptors.Count} ({sw.ElapsedMilliseconds}ms)");

            ms.SetLength(0);
            sf.Extract(ms);
            ms.Position = 0;
            if (!parser.TryReadRed4FileHeaders(ms, out var info)) continue;

            var strings = new HashSet<string>(info.StringDict.Values, StringComparer.Ordinal);
            string catLvl = $"{d.Category}:L{(byte)d.Level}";
            foreach (var wt in watchTypes)
            {
                if (strings.Contains(wt))
                {
                    string k = $"{catLvl}:{wt}";
                    sectorHitsByCatLvl[k] = sectorHitsByCatLvl.GetValueOrDefault(k) + 1;
                }
            }
            if (strings.Contains("worldLocationAreaNotifier") || strings.Contains("worldLocationAreaNode"))
                districtSectors.Add((catLvl, sf));
            if (strings.Contains("gameFastTravelPointData"))
                ftSectors.Add((catLvl, sf));
        }

        Console.WriteLine($"Header scan done in {sw.ElapsedMilliseconds}ms. DistrictSectors={districtSectors.Count}, FastTravelSectors={ftSectors.Count}");
        Console.WriteLine("=== Sectors containing each node type ===");
        foreach (var kv in sectorHitsByCatLvl.OrderBy(k => k.Key))
            Console.WriteLine($"  {kv.Key,-55} {kv.Value} sectors");

        // Now fully parse the districtSectors and list unique DistrictIDs!
        var foundDistrictIds = new Dictionary<string, int>();
        int outlineWithPts = 0, outlineNoPts = 0;
        foreach (var (catLvl, sf) in districtSectors)
        {
            var cr2w = ReadCr2w(sf, parser);
            if (cr2w?.RootChunk is not worldStreamingSector wss) continue;
            if (wss.NodeData?.Data is not worldNodeDataBuffer buf) continue;
            foreach (var nd in buf)
            {
                int ni = (int)(ulong)nd.NodeIdx;
                if (ni < 0 || ni >= wss.Nodes.Count) continue;
                if (wss.Nodes[ni]?.Chunk is worldTriggerAreaNode tan)
                {
                    foreach (var nh in tan.Notifiers)
                    {
                        if (nh?.Chunk is worldLocationAreaNotifier locN)
                        {
                            string id = locN.DistrictID.GetResolvedText() ?? $"0x{(ulong)locN.DistrictID:X16}";
                            foundDistrictIds[id] = foundDistrictIds.GetValueOrDefault(id) + 1;
                            if ((tan.Outline?.Chunk?.Points?.Count ?? 0) >= 3) outlineWithPts++;
                            else outlineNoPts++;
                        }
                    }
                }
            }
        }
        Console.WriteLine($"District triggers parsed: with>=3Pts={outlineWithPts}, <3Pts={outlineNoPts}, uniqueDistricts={foundDistrictIds.Count}");
        foreach (var kv in foundDistrictIds.OrderBy(k => k.Key).Take(40))
            Console.WriteLine($"  {kv.Key,-45} count={kv.Value}");

        await Task.CompletedTask;
    }

    public static async Task ProbeDetailsAsync(bool verbose = false)
    {
        var logger = new ConsoleLogger(verbose);
        var hashService = new HashService();
        hashService.Load();
        var parser = new Red4ParserService(hashService, logger, new HookService());
        var am = new ArchiveManager(hashService, parser, logger, new PercentProgressService());
        am.LoadGameArchives(new FileInfo(GameExePath));

        var grouped = new Dictionary<ulong, IGameFile>();
        foreach (var arch in am.GetBaseArchives().Concat(am.GetEp1Archives()))
            foreach (var kvp in arch.Files)
                grouped[kvp.Key] = kvp.Value;

        var ncSwFile = grouped.Values.First(f => f.FileName.Equals(@"base\worlds\03_night_city\_compiled\default\03_night_city.streamingworld", StringComparison.OrdinalIgnoreCase));
        var wsw = (worldStreamingWorld)ReadCr2w(ncSwFile, parser)!.RootChunk;

        // 1. Check Journal & Mappin categories/FastTravel
        var journalByHash = new Dictionary<uint, (string Path, gameJournalEntry Entry)>();
        foreach (var jf in grouped.Values.Where(f => f.FileName.EndsWith(".journal", StringComparison.OrdinalIgnoreCase)))
        {
            if (ReadCr2w(jf, parser)?.RootChunk is gameJournalResource jr && jr.Entry?.Chunk is gameJournalEntry root)
                TraverseJournal(root, "", journalByHash);
        }
        var mappinCatCounts = new Dictionary<string, int>();
        var mf = FindFile(wsw.MappinResource.DepotPath, grouped);
        if (mf != null && ReadCr2w(mf, parser)?.RootChunk is gameMappinResource mr)
        {
            foreach (var c in mr.CookedData)
            {
                if (journalByHash.TryGetValue((uint)c.JournalPathHash, out var j))
                {
                    string top = j.Path.Split('/').FirstOrDefault() ?? "";
                    string sub = j.Path.Split('/').Skip(1).FirstOrDefault() ?? "";
                    string k = $"{top}/{sub} ({j.Entry.GetType().Name})";
                    mappinCatCounts[k] = mappinCatCounts.GetValueOrDefault(k) + 1;
                }
            }
            Console.WriteLine("=== MappinResource top categories ===");
            foreach (var kv in mappinCatCounts.OrderByDescending(k => k.Value).Take(25))
                Console.WriteLine($"  {kv.Key,-55} {kv.Value}");
        }

        // Check PoiMappinResource breakdown
        var poiCatCounts = new Dictionary<string, int>();
        var pf = FindFile(wsw.PoiMappinResource.DepotPath, grouped);
        if (pf != null && ReadCr2w(pf, parser)?.RootChunk is gamePointOfInterestMappinResource pmr)
        {
            foreach (var c in pmr.CookedData)
            {
                if (journalByHash.TryGetValue((uint)c.JournalPathHash, out var j))
                {
                    string variant = "";
                    if (j.Entry is gameJournalPointOfInterestMappin poi)
                    {
                        var tv = poi.MappinData.TypedVariant?.Chunk;
                        if (tv is gamemappinsCommonVariant cv) variant = cv.Variant.ToString();
                        else if (tv is gamemappinsPhaseVariant pv) variant = pv.Variant.ToString();
                    }
                    string top = string.Join("/", j.Path.Split('/').Take(2));
                    string k = $"{top} [{variant}]";
                    poiCatCounts[k] = poiCatCounts.GetValueOrDefault(k) + 1;
                }
            }
            Console.WriteLine("=== PoiMappinResource top categories ===");
            foreach (var kv in poiCatCounts.OrderByDescending(k => k.Value).Take(30))
                Console.WriteLine($"  {kv.Key,-65} {kv.Value}");
        }

        // 2. Check worldLocationAreaNotifier details in Exterior:L4..L6 + L2 + Quest (only ~117 district sectors, fast!)
        var allDescriptors = new List<worldStreamingSectorDescriptor>();
        foreach (var blockRef in wsw.BlockRefs)
        {
            var bf = FindFile(blockRef.DepotPath, grouped);
            if (bf != null && ReadCr2w(bf, parser)?.RootChunk is worldStreamingBlock wsb)
                allDescriptors.AddRange(wsb.Descriptors);
        }

        var l6AndEp1 = allDescriptors
            .Where(d => d.Category.ToString() is "Exterior" && ((byte)d.Level >= 4 || (d.Data.DepotPath.GetResolvedText() ?? "").Contains(@"\ep1\", StringComparison.OrdinalIgnoreCase) && (byte)d.Level >= 2))
            .DistinctBy(d => d.Data.DepotPath.GetRedHash())
            .ToList();
        Console.WriteLine($"=== Probing {l6AndEp1.Count} Exterior L4-L6 + EP1 L2-L6 sectors ===");

        var districtSamples = new List<string>();
        var bldgSamples = new Dictionary<string, List<string>>();
        var meshCache = new Dictionary<ulong, Box?>();

        foreach (var d in l6AndEp1)
        {
            var sf = FindFile(d.Data.DepotPath, grouped);
            if (sf == null) continue;
            var cr2w = ReadCr2w(sf, parser);
            if (cr2w?.RootChunk is not worldStreamingSector wss) continue;
            if (wss.NodeData?.Data is not worldNodeDataBuffer buf) continue;

            foreach (var nd in buf)
            {
                int ni = (int)(ulong)nd.NodeIdx;
                if (ni < 0 || ni >= wss.Nodes.Count) continue;
                var node = wss.Nodes[ni]?.Chunk;
                if (node == null) continue;

                if (node is worldTriggerAreaNode tan)
                {
                    foreach (var nh in tan.Notifiers)
                    {
                        if (nh?.Chunk is worldLocationAreaNotifier locN)
                        {
                            string did = locN.DistrictID.GetResolvedText() ?? $"0x{(ulong)locN.DistrictID:X16}";
                            var o = tan.Outline?.Chunk;
                            if (o?.Points != null && o.Points.Count >= 3 && (did.Contains("Dogtown") || did.Contains("CityCenter") || did.Contains("Downtown") || did.Contains("Watson") || did.Contains("Kabuki") || did.Contains("LittleChina") || did.Contains("Heywood") || did.Contains("Pacifica") || did.Contains("Badlands")))
                            {
                                var p0 = o.Points[0];
                                var p1 = o.Points[1];
                                var pos = nd.Transform.Position;
                                var rot = nd.Transform.Orientation;
                                Console.WriteLine($"  DistrictTrigger {did} (L{(byte)d.Level}): pos=({(float)pos.X:F1},{(float)pos.Y:F1},{(float)pos.Z:F1}) scale=({nd.Scale.X:F2},{nd.Scale.Y:F2},{nd.Scale.Z:F2}) quat=({rot.I:F2},{rot.J:F2},{rot.K:F2},{rot.R:F2}) pts={o.Points.Count} h={o.Height:F1} pt0=({p0.X:F2},{p0.Y:F2},{p0.Z:F2}) pt1=({p1.X:F2},{p1.Y:F2},{p1.Z:F2})");
                            }
                        }
                    }
                }
                else if (node is worldBuildingProxyMeshNode || node is worldCookedBuildingProxyMeshNode || node is worldCookedGenericProxyMeshNode || node is worldStaticOccluderMeshNode || node is worldPrefabProxyMeshNode || node is worldCookedPrefabProxyMeshNode)
                {
                    if (node is worldMeshNode mn)
                    {
                        string t = node.GetType().Name;
                        if (!bldgSamples.TryGetValue(t, out var list)) bldgSamples[t] = list = new List<string>();
                        if (list.Count < 8)
                        {
                            ulong mh = mn.Mesh.DepotPath.GetRedHash();
                            if (!meshCache.TryGetValue(mh, out var box))
                            {
                                var mf2 = FindFile(mn.Mesh.DepotPath, grouped);
                                box = (mf2 != null && ReadCr2w(mf2, parser)?.RootChunk is CMesh cm) ? cm.BoundingBox : null;
                                meshCache[mh] = box;
                            }
                            string bstr = box != null
                                ? $"box=({box.Min.X:F1},{box.Min.Y:F1},{box.Min.Z:F1})..({box.Max.X:F1},{box.Max.Y:F1},{box.Max.Z:F1})"
                                : "box=null";
                            var pos = nd.Transform.Position;
                            list.Add($"[L{(byte)d.Level}] mesh='{mn.Mesh.DepotPath.GetResolvedText()}' pos=({(float)pos.X:F1},{(float)pos.Y:F1},{(float)pos.Z:F1}) scale=({nd.Scale.X:F2},{nd.Scale.Y:F2},{nd.Scale.Z:F2}) {bstr}");
                        }
                    }
                }
            }
        }

        foreach (var kv in bldgSamples)
        {
            Console.WriteLine($"=== Building candidate samples: {kv.Key} ===");
            foreach (var s in kv.Value) Console.WriteLine($"  {s}");
        }

        await Task.CompletedTask;
    }

    public static async Task Probe2Async(bool verbose = false)
    {
        var sw = Stopwatch.StartNew();
        var logger = new ConsoleLogger(verbose);
        var hashService = new HashService();
        hashService.Load();
        var parser = new Red4ParserService(hashService, logger, new HookService());
        var am = new ArchiveManager(hashService, parser, logger, new PercentProgressService());
        am.LoadGameArchives(new FileInfo(GameExePath));

        var grouped = new Dictionary<ulong, IGameFile>();
        foreach (var arch in am.GetBaseArchives().Concat(am.GetEp1Archives()))
            foreach (var kvp in arch.Files)
                grouped[kvp.Key] = kvp.Value;

        var ncSwFile = grouped.Values.First(f => f.FileName.Equals(@"base\worlds\03_night_city\_compiled\default\03_night_city.streamingworld", StringComparison.OrdinalIgnoreCase));
        var wsw = (worldStreamingWorld)ReadCr2w(ncSwFile, parser)!.RootChunk;

        // 1. Inspect DeviceResource & DeviceInitResource & AreaResource
        InspectRawResource("DeviceResource", wsw.DeviceResource.DepotPath, grouped, parser);
        InspectRawResource("DeviceInitResource", wsw.DeviceInitResource.DepotPath, grouped, parser);
        var areaFile = FindFile(wsw.AreaResource.DepotPath, grouped);
        if (areaFile != null && ReadCr2w(areaFile, parser)?.RootChunk is gameAreaResource gar)
        {
            var outlines = gar.CookedData.Where(d => d.Volume?.Chunk is gamemappinsOutlineMappinVolume).ToList();
            Console.WriteLine($"AreaResource: total={gar.CookedData.Count}, OutlineMappinVolumes={outlines.Count}");
            foreach (var o in outlines.Take(3))
            {
                var v = (gamemappinsOutlineMappinVolume)o.Volume!.Chunk!;
                Console.WriteLine($"  Area outline: pos=({o.Position.X:F1},{o.Position.Y:F1},{o.Position.Z:F1}) rad={o.Radius:F1} pts={v.OutlinePoints.Count} h={v.Height:F1}");
            }
        }

        // 2. Inspect ALL 117 District Sectors for AreaShapeOutline details!
        var allDescriptors = new List<worldStreamingSectorDescriptor>();
        foreach (var blockRef in wsw.BlockRefs)
        {
            var bf = FindFile(blockRef.DepotPath, grouped);
            if (bf != null && ReadCr2w(bf, parser)?.RootChunk is worldStreamingBlock wsb)
                allDescriptors.AddRange(wsb.Descriptors);
        }
        var uniqueSectors = allDescriptors
            .Where(d => d.Category.ToString() != "Navigation")
            .GroupBy(d => d.Data.DepotPath.GetRedHash())
            .Select(g => g.First())
            .ToList();

        Console.WriteLine("=== Scanning District Sectors for Outline Buffer / Points / Scale ===");
        int bufNonEmpty = 0, ptsGt4 = 0, scaleNonUnit = 0, totalTriggers = 0;
        int printedDistricts = 0;
        using (var ms = new MemoryStream(1024 * 1024))
        {
            foreach (var d in uniqueSectors)
            {
                var sf = FindFile(d.Data.DepotPath, grouped);
                if (sf == null) continue;
                ms.SetLength(0);
                sf.Extract(ms);
                ms.Position = 0;
                if (!parser.TryReadRed4FileHeaders(ms, out var info)) continue;
                if (!info.StringDict.Values.Contains("worldLocationAreaNotifier")) continue;

                ms.Position = 0;
                if (!parser.TryReadRed4File(ms, out var cr2w) || cr2w?.RootChunk is not worldStreamingSector wss) continue;
                if (wss.NodeData?.Data is not worldNodeDataBuffer buf) continue;

                foreach (var nd in buf)
                {
                    int ni = (int)(ulong)nd.NodeIdx;
                    if (ni < 0 || ni >= wss.Nodes.Count) continue;
                    if (wss.Nodes[ni]?.Chunk is not worldTriggerAreaNode tan) continue;
                    foreach (var nh in tan.Notifiers)
                    {
                        if (nh?.Chunk is not worldLocationAreaNotifier locN) continue;
                        totalTriggers++;
                        var o = tan.Outline?.Chunk;
                        byte[] bufBytes = o != null ? (byte[])o.Buffer : [];
                        int ptsCount = o?.Points?.Count ?? 0;
                        bool nonUnitScale = Math.Abs(nd.Scale.X - 1f) > 0.01f || Math.Abs(nd.Scale.Y - 1f) > 0.01f || Math.Abs(nd.Scale.Z - 1f) > 0.01f;
                        if (bufBytes.Length > 0) bufNonEmpty++;
                        if (ptsCount > 4) ptsGt4++;
                        if (nonUnitScale) scaleNonUnit++;

                        string did = locN.DistrictID.GetResolvedText() ?? $"0x{(ulong)locN.DistrictID:X16}";
                        if (bufBytes.Length > 0 || ptsCount > 4 || nonUnitScale || did.Contains("Kabuki") || did.Contains("Downtown") || did.Contains("Dogtown") || did.Contains("Watson") || did.Contains("Westbrook"))
                        {
                            if (printedDistricts < 20 || did.Contains("Kabuki") || did.Contains("Downtown") || did.Contains("Dogtown") || did.Contains("Watson"))
                            {
                                printedDistricts++;
                                var pos = nd.Transform.Position;
                                string hex = bufBytes.Length > 0 ? BitConverter.ToString(bufBytes, 0, Math.Min(64, bufBytes.Length)) : "<empty>";
                                string ptsStr = ptsCount > 0 ? string.Join(" ", o!.Points.Take(4).Select(p => $"({p.X:F2},{p.Y:F2})")) : "";
                                Console.WriteLine($"  [{d.Category}:L{(byte)d.Level}] {did}: pos=({(float)pos.X:F1},{(float)pos.Y:F1},{(float)pos.Z:F1}) scale=({nd.Scale.X:F2},{nd.Scale.Y:F2},{nd.Scale.Z:F2}) pts={ptsCount} [{ptsStr}] h={o?.Height:F1} bufLen={bufBytes.Length} hex={hex}");
                                if (bufBytes.Length >= 16)
                                {
                                    // Print as floats to see layout
                                    int fCount = Math.Min(16, bufBytes.Length / 4);
                                    var floats = new float[fCount];
                                    Buffer.BlockCopy(bufBytes, 0, floats, 0, fCount * 4);
                                    Console.WriteLine($"    as float32: {string.Join(", ", floats.Select(f => $"{f:F2}"))}");
                                }
                            }
                        }
                    }
                }
            }
        }
        Console.WriteLine($"Trigger stats: total={totalTriggers}, bufNonEmpty={bufNonEmpty}, pts>4={ptsGt4}, nonUnitScale={scaleNonUnit}");

        // 3. Probe cooked mesh nodes across ~12 Exterior sectors in Night City center & Dogtown across L0..L4
        var citySamples = uniqueSectors
            .Where(d => d.Category.ToString() == "Exterior" &&
                        ((byte)d.Level is 0 or 1 or 2 or 3 or 4) &&
                        d.StreamingBox.Min.X < -1000 && d.StreamingBox.Max.X > -2000 &&
                        d.StreamingBox.Min.Y < 1000 && d.StreamingBox.Max.Y > -1000)
            .GroupBy(d => (byte)d.Level)
            .SelectMany(g => g.Take(3))
            .ToList();
        Console.WriteLine($"=== Probing {citySamples.Count} central Exterior sectors (L0..L4) for cooked buildings ===");
        var meshCache = new Dictionary<ulong, Box?>();
        foreach (var d in citySamples)
        {
            var sf = FindFile(d.Data.DepotPath, grouped);
            if (sf == null) continue;
            var cr2w = ReadCr2w(sf, parser);
            if (cr2w?.RootChunk is not worldStreamingSector wss || wss.NodeData?.Data is not worldNodeDataBuffer buf) continue;

            int bldgProxy = 0, genProxy = 0, occ = 0, statMesh = 0, instMesh = 0;
            var samples = new List<string>();
            foreach (var nd in buf)
            {
                int ni = (int)(ulong)nd.NodeIdx;
                if (ni < 0 || ni >= wss.Nodes.Count) continue;
                var node = wss.Nodes[ni]?.Chunk;
                if (node == null) continue;

                ResourcePath? meshPath = null;
                string kind = node.GetType().Name;
                if (node is worldBuildingProxyMeshNode bp) { bldgProxy++; meshPath = bp.Mesh.DepotPath; }
                else if (node is worldCookedGenericProxyMeshNode gp && gp.Buffer.Data is worldCookedGenericProxyMeshNode_Buffer gpb)
                {
                    genProxy++;
                    int ii = (int)(uint)nd.InstanceIndex;
                    if (ii >= 0 && ii < gpb.InnerNodes.Count) meshPath = gpb.InnerNodes[ii].Mesh.DepotPath;
                }
                else if (node is worldCookedStaticMeshNode sm && sm.Buffer.Data is worldCookedStaticMeshNodeBuffer smb)
                {
                    statMesh++;
                    int ii = (int)(uint)nd.InstanceIndex;
                    if (ii >= 0 && ii < smb.InnerNodes.Count) meshPath = smb.InnerNodes[ii].Mesh.DepotPath;
                }
                else if (node is worldStaticOccluderMeshNode oc) { occ++; meshPath = oc.Mesh.DepotPath; }
                else if (node is worldInstancedMeshNode im && im.Buffer.Data is worldInstancedMeshNodeBuffer imb)
                {
                    instMesh++;
                    int ii = (int)(uint)nd.InstanceIndex;
                    if (ii >= 0 && ii < imb.InnerNodes.Count) meshPath = imb.InnerNodes[ii].Mesh.DepotPath;
                }

                if (meshPath != null && samples.Count < 4)
                {
                    ulong mh = meshPath.Value.GetRedHash();
                    if (!meshCache.TryGetValue(mh, out var box))
                    {
                        var mf2 = FindFile(meshPath.Value, grouped);
                        box = (mf2 != null && ReadCr2w(mf2, parser)?.RootChunk is CMesh cm) ? cm.BoundingBox : null;
                        meshCache[mh] = box;
                    }
                    string mstr = meshPath.Value.GetResolvedText() ?? $"0x{mh:X16}";
                    string bstr = box != null ? $"{(box.Max.X - box.Min.X):F1}x{(box.Max.Y - box.Min.Y):F1}x{(box.Max.Z - box.Min.Z):F1}" : "null";
                    samples.Add($"{kind}: '{mstr}' size={bstr} scale=({nd.Scale.X:F1},{nd.Scale.Y:F1},{nd.Scale.Z:F1})");
                }
            }
            Console.WriteLine($"  Sector L{(byte)d.Level} {sf.FileName}: BldgProxy={bldgProxy}, GenProxy={genProxy}, Occ={occ}, CookedStatic={statMesh}, Inst={instMesh}");
            foreach (var s in samples) Console.WriteLine($"    {s}");
        }

        Console.WriteLine($"Probe2 finished in {sw.ElapsedMilliseconds}ms");
        await Task.CompletedTask;
    }

    public static async Task Probe3Async(bool verbose = false)
    {
        var sw = Stopwatch.StartNew();
        var logger = new ConsoleLogger(verbose);
        var hashService = new HashService();
        hashService.Load();
        var parser = new Red4ParserService(hashService, logger, new HookService());
        var am = new ArchiveManager(hashService, parser, logger, new PercentProgressService());
        am.LoadGameArchives(new FileInfo(GameExePath));

        var grouped = new Dictionary<ulong, IGameFile>();
        foreach (var arch in am.GetBaseArchives().Concat(am.GetEp1Archives()))
            foreach (var kvp in arch.Files)
                grouped[kvp.Key] = kvp.Value;

        var ncSwFile = grouped.Values.First(f => f.FileName.Equals(@"base\worlds\03_night_city\_compiled\default\03_night_city.streamingworld", StringComparison.OrdinalIgnoreCase));
        var wsw = (worldStreamingWorld)ReadCr2w(ncSwFile, parser)!.RootChunk;

        // 1. Inspect DeviceResource Data & FastTravel
        var df = FindFile(wsw.DeviceResource.DepotPath, grouped);
        if (df != null && ReadCr2w(df, parser)?.RootChunk is gameDeviceResource gdr)
        {
            Console.WriteLine($"DeviceResource Data type={gdr.Data?.Chunk?.GetType().Name}, Count={gdr.Data?.Chunk?.Unk1.Count ?? 0}");
            if (gdr.Data?.Chunk != null)
            {
                var ftDevs = gdr.Data.Chunk.Unk1
                    .Where(d => (d.ClassName.GetResolvedText() ?? "").Contains("DataTerm", StringComparison.OrdinalIgnoreCase)
                             || (d.ClassName.GetResolvedText() ?? "").Contains("FastTravel", StringComparison.OrdinalIgnoreCase)
                             || (d.ClassName.GetResolvedText() ?? "").Contains("Subway", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                Console.WriteLine($"  FastTravel/DataTerm/Subway devices: {ftDevs.Count}");
                foreach (var x in ftDevs.Take(5))
                    Console.WriteLine($"    Class={x.ClassName.GetResolvedText()} pos=({x.NodePosition.X:F1},{x.NodePosition.Y:F1},{x.NodePosition.Z:F1})");
                foreach (var g in gdr.Data.Chunk.Unk1.GroupBy(d => d.ClassName.GetResolvedText() ?? "").OrderByDescending(g => g.Count()).Take(15))
                    Console.WriteLine($"    DeviceClass {g.Key,-40} {g.Count()}");
            }
        }

        // 2. Also check AlwaysLoaded sectors for FastTravel or Mappin nodes + NodeRefs
        var allDescriptors = new List<worldStreamingSectorDescriptor>();
        foreach (var blockRef in wsw.BlockRefs)
        {
            var bf = FindFile(blockRef.DepotPath, grouped);
            if (bf != null && ReadCr2w(bf, parser)?.RootChunk is worldStreamingBlock wsb)
                allDescriptors.AddRange(wsb.Descriptors);
        }
        foreach (var d in allDescriptors.Where(x => x.Category.ToString() == "AlwaysLoaded"))
        {
            var sf = FindFile(d.Data.DepotPath, grouped);
            if (sf == null) continue;
            var wss = ReadCr2w(sf, parser)?.RootChunk as worldStreamingSector;
            if (wss == null) continue;
            Console.WriteLine($"AlwaysLoaded {sf.FileName}: NodeRefs={wss.NodeRefs.Count}, Nodes={wss.Nodes.Count}");
            int ftRefCount = 0;
            foreach (var nr in wss.NodeRefs)
            {
                var txt = nr.GetResolvedText() ?? "";
                if (txt.Contains("fast_travel", StringComparison.OrdinalIgnoreCase) || txt.Contains("dataterm", StringComparison.OrdinalIgnoreCase) || txt.Contains("metro", StringComparison.OrdinalIgnoreCase))
                {
                    ftRefCount++;
                    if (ftRefCount <= 5) Console.WriteLine($"  NodeRef: {txt}");
                }
            }
            Console.WriteLine($"  FT/dataterm/metro NodeRefs in sector: {ftRefCount}");
        }

        // 3. Check building coverage in Downtown / Watson (X: -1800..-1000, Y: -200..1500) across L0..L5
        var downtownSectors = allDescriptors
            .Where(d => d.Category.ToString() == "Exterior"
                     && d.StreamingBox.Max.X > -1700 && d.StreamingBox.Min.X < -1100
                     && d.StreamingBox.Max.Y > 0 && d.StreamingBox.Min.Y < 800)
            .DistinctBy(d => d.Data.DepotPath.GetRedHash())
            .ToList();
        Console.WriteLine($"=== Downtown box sectors: {downtownSectors.Count} ===");
        var meshCache = new Dictionary<ulong, Box?>();
        var bigByType = new Dictionary<string, int>();
        var bigSamples = new Dictionary<string, List<string>>();
        foreach (var d in downtownSectors)
        {
            var sf = FindFile(d.Data.DepotPath, grouped);
            if (sf == null) continue;
            var cr2w = ReadCr2w(sf, parser);
            if (cr2w?.RootChunk is not worldStreamingSector wss || wss.NodeData?.Data is not worldNodeDataBuffer buf) continue;

            foreach (var nd in buf)
            {
                int ni = (int)(ulong)nd.NodeIdx;
                if (ni < 0 || ni >= wss.Nodes.Count) continue;
                var node = wss.Nodes[ni]?.Chunk;
                if (node == null) continue;

                ResourcePath? mp = null;
                string kind = $"{node.GetType().Name}@L{(byte)d.Level}";
                if (node is worldBuildingProxyMeshNode bp) mp = bp.Mesh.DepotPath;
                else if (node is worldCookedBuildingProxyMeshNode cbp && cbp.Buffer.Data is worldCookedGenericProxyMeshNode_Buffer cbpb)
                {
                    int ii = (int)(uint)nd.InstanceIndex;
                    if (ii >= 0 && ii < cbpb.InnerNodes.Count) mp = cbpb.InnerNodes[ii].Mesh.DepotPath;
                }
                else if (node is worldCookedGenericProxyMeshNode gp && gp.Buffer.Data is worldCookedGenericProxyMeshNode_Buffer gpb)
                {
                    int ii = (int)(uint)nd.InstanceIndex;
                    if (ii >= 0 && ii < gpb.InnerNodes.Count) mp = gpb.InnerNodes[ii].Mesh.DepotPath;
                }
                else if (node is worldCookedStaticMeshNode sm && sm.Buffer.Data is worldCookedStaticMeshNodeBuffer smb)
                {
                    int ii = (int)(uint)nd.InstanceIndex;
                    if (ii >= 0 && ii < smb.InnerNodes.Count) mp = smb.InnerNodes[ii].Mesh.DepotPath;
                }
                else if (node is worldStaticOccluderMeshNode oc) mp = oc.Mesh.DepotPath;
                else if (node is worldInstancedOccluderNode io) mp = io.Mesh.DepotPath;
                else if (node is worldInstancedMeshNode im && im.Buffer.Data is worldInstancedMeshNodeBuffer imb)
                {
                    int ii = (int)(uint)nd.InstanceIndex;
                    if (ii >= 0 && ii < imb.InnerNodes.Count) mp = imb.InnerNodes[ii].Mesh.DepotPath;
                }

                if (mp == null) continue;
                ulong mh = mp.Value.GetRedHash();
                if (!meshCache.TryGetValue(mh, out var box))
                {
                    var mf2 = FindFile(mp.Value, grouped);
                    box = (mf2 != null && ReadCr2w(mf2, parser)?.RootChunk is CMesh cm) ? cm.BoundingBox : null;
                    meshCache[mh] = box;
                }
                if (box == null) continue;
                float sx = (box.Max.X - box.Min.X) * Math.Abs(nd.Scale.X);
                float sy = (box.Max.Y - box.Min.Y) * Math.Abs(nd.Scale.Y);
                float sz = (box.Max.Z - box.Min.Z) * Math.Abs(nd.Scale.Z);
                if (sx >= 10 && sy >= 10 && sz >= 8 && sx <= 300 && sy <= 300)
                {
                    bigByType[kind] = bigByType.GetValueOrDefault(kind) + 1;
                    if (!bigSamples.TryGetValue(kind, out var lst)) bigSamples[kind] = lst = new List<string>();
                    if (lst.Count < 3)
                        lst.Add($"'{mp.Value.GetResolvedText()}' ({sx:F1}x{sy:F1}x{sz:F1}m)");
                }
            }
        }
        foreach (var kv in bigByType.OrderByDescending(k => k.Value))
        {
            Console.WriteLine($"  {kv.Key,-45} count={kv.Value}");
            foreach (var s in bigSamples[kv.Key]) Console.WriteLine($"      {s}");
        }

        Console.WriteLine($"Probe3 finished in {sw.ElapsedMilliseconds}ms");
        await Task.CompletedTask;
    }

    public static async Task ProbeFastTravelAsync()
    {
        var logger = new ConsoleLogger(false);
        var hashService = new HashService();
        hashService.Load();
        var parser = new Red4ParserService(hashService, logger, new HookService());
        var am = new ArchiveManager(hashService, parser, logger, new PercentProgressService());
        am.LoadGameArchives(new FileInfo(GameExePath));

        var grouped = new Dictionary<ulong, IGameFile>();
        foreach (var arch in am.GetBaseArchives().Concat(am.GetEp1Archives()))
            foreach (var kvp in arch.Files)
                grouped[kvp.Key] = kvp.Value;

        var tdb = new TweakDBService(hashService);
        await tdb.LoadDB(TweakDbEp1Path);

        var ftRecs = TweakDBService.GetRecords()
            .Where(r => r.GetResolvedText()?.StartsWith("FastTravelPoints.") == true)
            .ToList();
        Console.WriteLine($"FastTravelPoints records: {ftRecs.Count}");
        foreach (var id in ftRecs.Take(8))
        {
            var r = TweakDBService.GetRecord(id) as gamedataFastTravelPoint_Record;
            Console.WriteLine($"  {id.GetResolvedText()} -> DisplayName='{r?.DisplayName}', Desc='{r?.Description}', District='{r?.District.GetResolvedText()}', ShowMap={r?.ShowOnWorldMap}, Subway={r?.SubwayStation}");
        }

        var ncSwFile = grouped.Values.First(f => f.FileName.Equals(@"base\worlds\03_night_city\_compiled\default\03_night_city.streamingworld", StringComparison.OrdinalIgnoreCase));
        var wsw = (worldStreamingWorld)ReadCr2w(ncSwFile, parser)!.RootChunk;
        var alBlocks = new List<worldStreamingSectorDescriptor>();
        foreach (var blockRef in wsw.BlockRefs)
        {
            var bf = FindFile(blockRef.DepotPath, grouped);
            if (bf != null && ReadCr2w(bf, parser)?.RootChunk is worldStreamingBlock wsb)
                alBlocks.AddRange(wsb.Descriptors.Where(d => d.Category.ToString() == "AlwaysLoaded"));
        }

        // Check how NodeRefs link to NodeData / Nodes in AlwaysLoaded sectors!
        int matchedFtMarkers = 0;
        var suffixMatches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ftRecs)
        {
            string full = id.GetResolvedText()!;
            string shortName = full["FastTravelPoints.".Length..];
            suffixMatches[shortName] = full;
        }

        foreach (var d in alBlocks)
        {
            var sf = FindFile(d.Data.DepotPath, grouped);
            if (sf == null) continue;
            var wss = ReadCr2w(sf, parser)?.RootChunk as worldStreamingSector;
            if (wss?.NodeData?.Data is not worldNodeDataBuffer buf) continue;

            // Check NodeRefs vs NodeIdx / GlobalNodeId / PatchedNode
            Console.WriteLine($"Sector {sf.FileName}: NodeRefs={wss.NodeRefs.Count}, Nodes={wss.Nodes.Count}, Buf={buf.Count}");
            int sampleShown = 0;
            for (int i = 0; i < wss.NodeRefs.Count; i++)
            {
                string nr = wss.NodeRefs[i].GetResolvedText() ?? "";
                if (nr.Contains("dataterm", StringComparison.OrdinalIgnoreCase) || nr.Contains("_ftp_", StringComparison.OrdinalIgnoreCase))
                {
                    if (sampleShown < 6)
                    {
                        sampleShown++;
                        Console.WriteLine($"  NodeRef[{i}] = {nr}");
                    }
                    // Check if any buf item has GlobalNodeId or InstanceIndex or NodeIdx matching i?
                }
            }
            // Also check buf items whose PatchedNode or NodeIdx has dataterm
            foreach (var nd in buf)
            {
                string pnode = nd.PatchedNode.GetResolvedText() ?? "";
                int ni = (int)(ulong)nd.NodeIdx;
                var node = (ni >= 0 && ni < wss.Nodes.Count) ? wss.Nodes[ni]?.Chunk : null;
                if (node is worldStaticMarkerNode smn && smn.Data?.Chunk != null)
                {
                    string dt = smn.Data.Chunk.GetType().Name;
                    if (dt.Contains("FastTravel", StringComparison.OrdinalIgnoreCase) || pnode.Contains("dataterm", StringComparison.OrdinalIgnoreCase))
                    {
                        matchedFtMarkers++;
                        if (matchedFtMarkers <= 6)
                            Console.WriteLine($"  StaticMarker[{ni}] Data={dt} patched='{pnode}' pos=({(float)nd.Transform.Position.X:F1},{(float)nd.Transform.Position.Y:F1},{(float)nd.Transform.Position.Z:F1})");
                    }
                }
            }
        }

        // Also check ALL unique Marker Data types in AlwaysLoaded_0!
        var al0 = FindFile(alBlocks[0].Data.DepotPath, grouped);
        if (al0 != null && ReadCr2w(al0, parser)?.RootChunk is worldStreamingSector w0)
        {
            var markerTypes = new Dictionary<string, int>();
            foreach (var n in w0.Nodes)
            {
                if (n?.Chunk is worldStaticMarkerNode smn)
                {
                    string dt = smn.Data?.Chunk?.GetType().Name ?? "<null>";
                    markerTypes[dt] = markerTypes.GetValueOrDefault(dt) + 1;
                }
            }
            Console.WriteLine("=== StaticMarkerNode.Data types in always_loaded_0 ===");
            foreach (var kv in markerTypes.OrderByDescending(k => k.Value))
                Console.WriteLine($"  {kv.Key,-40} {kv.Value}");
        }
        Console.WriteLine($"matchedFtMarkers={matchedFtMarkers}");
    }

    private static void InspectRawResource(string label, ResourcePath path, Dictionary<ulong, IGameFile> grouped, Red4ParserService parser)
    {
        var f = FindFile(path, grouped);
        Console.WriteLine($"=== {label}: {path.GetResolvedText()} [{f?.GetArchive().Name ?? "MISSING"}] ===");
        if (f == null) return;
        var cr2w = ReadCr2w(f, parser);
        if (cr2w?.RootChunk == null) return;
        Console.WriteLine($"  Root={cr2w.RootChunk.GetType().Name}");
        foreach (var p in cr2w.RootChunk.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.DeclaringType != typeof(RedBaseClass)))
        {
            var v = p.GetValue(cr2w.RootChunk);
            if (v is ICollection c)
            {
                Console.WriteLine($"  prop {p.Name}: Count={c.Count}");
                int idx = 0;
                foreach (var item in c)
                {
                    if (idx++ >= 2) break;
                    Console.WriteLine($"    [{idx - 1}] {DescribeObject(item)}");
                }
            }
            else Console.WriteLine($"  prop {p.Name}: {v}");
        }
    }

    private static CR2WFile? ReadCr2w(IGameFile file, Red4ParserService parser)
    {
        using var ms = new MemoryStream();
        file.Extract(ms);
        ms.Position = 0;
        return parser.TryReadRed4File(ms, out var cr2w) ? cr2w : null;
    }

    private static IGameFile? FindFile(ResourcePath path, Dictionary<ulong, IGameFile> grouped)
    {
        if (grouped.TryGetValue(path.GetRedHash(), out var f)) return f;
        string? txt = path.GetResolvedText();
        return string.IsNullOrEmpty(txt) ? null : grouped.Values.FirstOrDefault(x => x.FileName.Equals(txt, StringComparison.OrdinalIgnoreCase));
    }

    private static void InspectLocationResource(ResourcePath locPath, Dictionary<ulong, IGameFile> grouped, Red4ParserService parser)
    {
        var lf = FindFile(locPath, grouped);
        Console.WriteLine($"=== LocationResource: {lf?.FileName} [{lf?.GetArchive().Name}] ===");
        if (lf == null) return;
        var cr2w = ReadCr2w(lf, parser);
        if (cr2w?.RootChunk is gameLocationResource glr)
        {
            byte[] bytes = (byte[])glr.Buffer;
            Console.WriteLine($"  Buffer length={bytes.Length} bytes");
            if (bytes.Length >= 16)
            {
                Console.WriteLine($"  First 32 bytes hex: {BitConverter.ToString(bytes, 0, Math.Min(32, bytes.Length))}");
                Console.WriteLine($"  ASCII preview: {Encoding.ASCII.GetString(bytes.Take(128).Select(b => b >= 32 && b < 127 ? b : (byte)'.').ToArray())}");
            }
        }
    }

    private static void InspectTraffic(worldStreamingWorld wsw, Dictionary<ulong, IGameFile> grouped, Red4ParserService parser)
    {
        var pf = FindFile(wsw.TrafficPersistentResource.DepotPath, grouped);
        var cf = FindFile(wsw.TrafficLaneConnectivityResource.DepotPath, grouped);
        var polyf = FindFile(wsw.TrafficLanePolygonsResource.DepotPath, grouped);
        var spatf = FindFile(wsw.TrafficSpatialRepresentationResource.DepotPath, grouped);

        var tpr = pf != null ? ReadCr2w(pf, parser)?.RootChunk as worldTrafficPersistentResource : null;
        var lcr = cf != null ? ReadCr2w(cf, parser)?.RootChunk as worldTrafficPersistentLaneConnectionsResource : null;
        var lpr = polyf != null ? ReadCr2w(polyf, parser)?.RootChunk as worldTrafficPersistentLanePolygonResource : null;
        var sr = spatf != null ? ReadCr2w(spatf, parser)?.RootChunk as worldTrafficPersistentSpatialResource : null;

        Console.WriteLine($"=== Traffic ===");
        Console.WriteLine($"  Persistent Lanes={tpr?.Data?.Lanes?.Count ?? 0}");
        Console.WriteLine($"  LaneConnections={lcr?.Data?.Count ?? 0}");
        Console.WriteLine($"  LanePolygons={lpr?.Data?.Count ?? 0}");
        Console.WriteLine($"  Spatial NeighborGroups={sr?.NeighborGroups?.Count ?? 0}");

        if (lpr?.Data != null && lpr.Data.Count > 0)
        {
            int withOutline = lpr.Data.Count(d => d.Value?.Outline != null && d.Value.Outline.Count >= 2);
            int withPoly = lpr.Data.Count(d => d.Value?.Polygon != null && d.Value.Polygon.Count >= 2);
            Console.WriteLine($"  LanePolygons: withOutline>=2={withOutline}, withPolygon>=2={withPoly}");
            foreach (var d in lpr.Data.Take(3))
            {
                var o = d.Value?.Outline;
                Console.WriteLine($"    PolyData idx={d.Index}, Outline={o?.Count ?? 0}, Poly={d.Value?.Polygon?.Count ?? 0}, p0=({o?[0].X:F1},{o?[0].Y:F1},{o?[0].Z:F1}) -> pN=({o?[^1].X:F1},{o?[^1].Y:F1},{o?[^1].Z:F1})");
            }
        }
        if (lcr?.Data != null && lcr.Data.Count > 0)
        {
            int withIn = lcr.Data.Count(d => d.Value?.InLanes != null && d.Value.InLanes.Count > 0);
            int withOut = lcr.Data.Count(d => d.Value?.Outlanes != null && d.Value.Outlanes.Count > 0);
            Console.WriteLine($"  LaneConnections: withIn={withIn}, withOut={withOut}");
            foreach (var d in lcr.Data.Take(3))
            {
                var ins = string.Join(",", d.Value?.InLanes?.Select(x => (ushort)x.LaneIndex) ?? []);
                var outs = string.Join(",", d.Value?.Outlanes?.Select(x => (ushort)x.LaneIndex) ?? []);
                Console.WriteLine($"    ConnData idx={d.Index}: In=[{ins}], Out=[{outs}]");
            }
        }
    }

    private static void InspectMappinsAndJournal(worldStreamingWorld wsw, Dictionary<ulong, IGameFile> grouped, Red4ParserService parser)
    {
        var journalByHash = new Dictionary<uint, (string Path, gameJournalEntry Entry)>();
        foreach (var jf in grouped.Values.Where(f => f.FileName.EndsWith(".journal", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.FileName))
        {
            var cr2w = ReadCr2w(jf, parser);
            if (cr2w?.RootChunk is gameJournalResource jr && jr.Entry?.Chunk is gameJournalEntry root)
                TraverseJournal(root, "", journalByHash);
        }
        Console.WriteLine($"=== Mappins & Journal (unique journal hashes={journalByHash.Count}) ===");

        var mf = FindFile(wsw.MappinResource.DepotPath, grouped);
        var pf = FindFile(wsw.PoiMappinResource.DepotPath, grouped);
        var mr = mf != null ? ReadCr2w(mf, parser)?.RootChunk as gameMappinResource : null;
        var pmr = pf != null ? ReadCr2w(pf, parser)?.RootChunk as gamePointOfInterestMappinResource : null;

        if (mr != null)
        {
            int hits = mr.CookedData.Count(c => journalByHash.ContainsKey((uint)c.JournalPathHash));
            Console.WriteLine($"  MappinResource Cooked={mr.CookedData.Count}, matched={hits}");
            foreach (var c in mr.CookedData.Where(c => journalByHash.ContainsKey((uint)c.JournalPathHash)).Take(5))
            {
                var (p, e) = journalByHash[(uint)c.JournalPathHash];
                Console.WriteLine($"    MATCH {e.GetType().Name}: path='{p}' pos=({c.Position.X:F1},{c.Position.Y:F1},{c.Position.Z:F1}) {DescribeMappinEntry(e)}");
            }
            foreach (var c in mr.CookedData.Where(c => !journalByHash.ContainsKey((uint)c.JournalPathHash)).Take(3))
            {
                Console.WriteLine($"    UNMATCHED hash=0x{(uint)c.JournalPathHash:X8} pos=({c.Position.X:F1},{c.Position.Y:F1},{c.Position.Z:F1})");
            }
        }
        if (pmr != null)
        {
            int hits = pmr.CookedData.Count(c => journalByHash.ContainsKey((uint)c.JournalPathHash));
            Console.WriteLine($"  PoiMappinResource Cooked={pmr.CookedData.Count}, matched={hits}");
            foreach (var c in pmr.CookedData.Where(c => journalByHash.ContainsKey((uint)c.JournalPathHash)).Take(8))
            {
                var (p, e) = journalByHash[(uint)c.JournalPathHash];
                Console.WriteLine($"    POI MATCH {e.GetType().Name}: path='{p}' pos=({c.Position.X:F1},{c.Position.Y:F1},{c.Position.Z:F1}) {DescribeMappinEntry(e)}");
            }
        }
    }

    private static string DescribeObject(object? obj)
    {
        if (obj == null) return "<null>";
        var t = obj.GetType();
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "PropertyName" && p.DeclaringType != typeof(RedBaseClass))
            .Take(8)
            .Select(p =>
            {
                var v = p.GetValue(obj);
                if (v is ICollection c) return $"{p.Name}=[{c.Count}]";
                return $"{p.Name}={v}";
            });
        return $"{t.Name} {{ {string.Join(", ", props)} }}";
    }

    private static string DescribeMappinEntry(gameJournalEntry e)
    {
        if (e is gameJournalPointOfInterestMappin poi)
            return $"{DescribeObject(poi.MappinData)}, QuestPath={poi.QuestPath?.Chunk}";
        if (e is gameJournalQuestMapPin qmp)
            return $"MappinType={qmp.MappinData.MappinType.GetResolvedText()}";
        return "";
    }

    private static void TraverseJournal(gameJournalEntry entry, string prefix, Dictionary<uint, (string, gameJournalEntry)> map)
    {
        string id = (string?)entry.Id ?? "";
        // Also test prefix including root id when non-empty (like "ep1")!
        string currentPath = entry is gameJournalRootFolderEntry
            ? id
            : (string.IsNullOrEmpty(prefix) ? id : $"{prefix}/{id}");

        if (!string.IsNullOrEmpty(currentPath))
        {
            uint h1 = Murmur32.Hash(currentPath, 0x5EEDBA5E);
            map[h1] = (currentPath, entry);
            // Also index without "ep1/" prefix if it started with "ep1/"
            if (currentPath.StartsWith("ep1/", StringComparison.OrdinalIgnoreCase))
            {
                string stripped = currentPath[4..];
                uint h2 = Murmur32.Hash(stripped, 0x5EEDBA5E);
                map.TryAdd(h2, (currentPath, entry));
            }
        }

        if (entry is gameJournalContainerEntry container)
        {
            foreach (var ch in container.Entries)
            {
                if (ch?.Chunk is gameJournalEntry child)
                    TraverseJournal(child, currentPath, map);
            }
        }
    }

    private static void InspectSampleSectors(List<worldStreamingSectorDescriptor> allDescriptors, Dictionary<ulong, IGameFile> grouped, Red4ParserService parser)
    {
        Console.WriteLine("=== Inspecting sample sectors ===");
        var samples = new List<worldStreamingSectorDescriptor>();
        samples.AddRange(allDescriptors.Where(b => (b.Data.DepotPath.GetResolvedText() ?? "").Contains("always_loaded", StringComparison.OrdinalIgnoreCase)));
        foreach (var grp in allDescriptors.Where(b => b.Category.ToString() is "Exterior" or "Quest" or "Interior").GroupBy(b => (Cat: b.Category.ToString(), Lvl: (byte)b.Level)))
        {
            samples.AddRange(grp.Where(b => (b.Data.DepotPath.GetResolvedText() ?? "").Contains(@"\ep1\", StringComparison.OrdinalIgnoreCase)).Take(3));
            samples.AddRange(grp.Where(b => b.StreamingBox.Min.X < -500 && b.StreamingBox.Max.X > -2000 && b.StreamingBox.Min.Y < 1500 && b.StreamingBox.Max.Y > -500).Take(4));
        }

        var instCounts = new Dictionary<string, int>();
        int locAreas = 0;
        int distTriggers = 0;
        int ftMarkers = 0;

        foreach (var b in samples.DistinctBy(s => s.Data.DepotPath.GetRedHash()))
        {
            var sf = FindFile(b.Data.DepotPath, grouped);
            if (sf == null) continue;
            var cr2w = ReadCr2w(sf, parser);
            if (cr2w?.RootChunk is not worldStreamingSector wss) continue;
            if (wss.NodeData?.Data is not worldNodeDataBuffer buf) continue;

            foreach (var nd in buf)
            {
                int nodeIdx = (int)(ulong)nd.NodeIdx;
                if (nodeIdx < 0 || nodeIdx >= wss.Nodes.Count) continue;
                var node = wss.Nodes[nodeIdx]?.Chunk;
                if (node == null) continue;

                string key = $"{b.Category}:L{(byte)b.Level}:{node.GetType().Name}";
                instCounts[key] = instCounts.GetValueOrDefault(key) + 1;

                var pos = nd.Transform.Position;
                if (node is worldLocationAreaNode lan)
                {
                    locAreas++;
                    if (locAreas <= 5)
                    {
                        var o = lan.Outline?.Chunk;
                        Console.WriteLine($"  [{b.Category}:L{(byte)b.Level}] LocationAreaNode '{lan.LocationName}' pos=({(float)pos.X:F1},{(float)pos.Y:F1},{(float)pos.Z:F1}) pts={o?.Points?.Count ?? 0} h={o?.Height:F1}");
                    }
                }
                else if (node is worldTriggerAreaNode tan)
                {
                    foreach (var nh in tan.Notifiers)
                    {
                        if (nh?.Chunk is worldLocationAreaNotifier locN)
                        {
                            distTriggers++;
                            if (distTriggers <= 10)
                            {
                                var o = tan.Outline?.Chunk;
                                Console.WriteLine($"  [{b.Category}:L{(byte)b.Level}] TriggerLocation District='{locN.DistrictID.GetResolvedText()}' pos=({(float)pos.X:F1},{(float)pos.Y:F1},{(float)pos.Z:F1}) pts={o?.Points?.Count ?? 0} h={o?.Height:F1} sector={sf.FileName}");
                                if (o?.Points != null && o.Points.Count > 0)
                                    Console.WriteLine($"      Pt0=({o.Points[0].X:F2},{o.Points[0].Y:F2}) Pt1=({o.Points[Math.Min(1, o.Points.Count - 1)].X:F2},{o.Points[Math.Min(1, o.Points.Count - 1)].Y:F2})");
                            }
                        }
                    }
                }
                else if (node is worldStaticMarkerNode smn && ftMarkers < 5)
                {
                    if (smn.Data?.Chunk != null)
                    {
                        ftMarkers++;
                        Console.WriteLine($"  [{b.Category}:L{(byte)b.Level}] StaticMarkerNode Data={smn.Data.Chunk.GetType().Name} pos=({(float)pos.X:F1},{(float)pos.Y:F1},{(float)pos.Z:F1})");
                    }
                }
                else if (node is worldBuildingProxyMeshNode || node is worldCookedBuildingProxyMeshNode || node is worldPrefabProxyMeshNode || node is worldCookedPrefabProxyMeshNode || node is worldStaticOccluderMeshNode || (node is worldStaticMeshNode && (byte)b.Level >= 1))
                {
                    if (instCounts[key] <= 2 && node is worldMeshNode mn)
                    {
                        Console.WriteLine($"  [{b.Category}:L{(byte)b.Level}] {node.GetType().Name} mesh='{mn.Mesh.DepotPath.GetResolvedText()}' pos=({(float)pos.X:F1},{(float)pos.Y:F1},{(float)pos.Z:F1}) scale=({nd.Scale.X:F2},{nd.Scale.Y:F2},{nd.Scale.Z:F2})");
                    }
                }
            }
        }

        Console.WriteLine($"Sample district nodes: LocationAreaNode={locAreas}, TriggerLocationNotifier={distTriggers}");
        Console.WriteLine("=== Top Node Counts by (Cat:Level:Type) ===");
        foreach (var kv in instCounts.Where(k => k.Key.Contains("Proxy") || k.Key.Contains("Mesh") || k.Key.Contains("Occluder") || k.Key.Contains("Location") || k.Key.Contains("Trigger") || k.Key.Contains("Road") || k.Key.Contains("Traffic") || k.Key.Contains("Marker") || k.Key.Contains("Area")).OrderByDescending(k => k.Value).Take(50))
            Console.WriteLine($"  {kv.Key,-55} {kv.Value}");
    }
}
