# AGENTS.md

Guidance for coding agents working in `Q:\Projects\cyberpunk-map`.

## Project Overview

This repository extracts spatial map data directly from **Cyberpunk 2077 + Phantom Liberty (EP1)** game archives (`*.archive` and `tweakdb_ep1.bin`) into **GeoJSON** files (`regions.geojson`, `roads.geojson`, `buildings.geojson`, `poi.geojson`) and `index_stats.json`. It uses a `.NET 10` C# CLI extractor (`extractor/`) linked against the local **WolvenKit 10** libraries in `WolvenKit-10.0.0-beta.1/` without manually unpacking game archives to disk.

### Repository Layout

- `extractor/` — `.NET 10` (`net10.0-windows`, `x64`) C# extractor project:
  - `CyberpunkMapExtractor.csproj` — references `WolvenKit.Core.dll`, `WolvenKit.Common.dll`, `WolvenKit.RED4.dll`, `WolvenKit.Modkit.dll`, and `CommunityToolkit.Mvvm.dll` from `..\WolvenKit-10.0.0-beta.1\` with `<Private>false</Private>`.
  - `WkitLoader.cs` — `[ModuleInitializer]` registering `AssemblyLoadContext.Default.Resolving` and Win32 `SetDllDirectory` + `PATH` for `WolvenKit-10.0.0-beta.1\`.
  - `ConsoleLogger.cs` — `ILoggerService` implementation (`Verbose = false` suppresses noisy non-fatal RTTI warnings/errors).
  - `Program.cs` — CLI entry point (`Program.Main` delegates to `Runner.Run` so `WkitLoader.Init()` executes before WolvenKit types JIT-load).
  - `ExtractorRunner.cs` — Loads archives & TweakDB, scans sector headers once, orchestrates all 4 extractors, writes `output/index_stats.json`.
  - `GeoMath.cs` — RED4 transform math (`Transform` + `Vector3` scale + `Quaternion`), 2D convex hull + rotating-calipers OBB footprint computation, `AreaShapeOutline.Buffer` decoding, point-in-polygon, shoelace area, and `Utf8JsonWriter` GeoJSON helpers.
  - `LocalizationHelper.cs` — Loads `tweakdb_ep1.bin`, `pl-pl` & `en-us` `onscreens_final.json`, and builds `DistrictsByHash` / `DistrictsById` / `FastTravelPoints`.
  - `RegionsExtractor.cs` — Extracts district/location polygons to `output/regions.geojson` and builds `DistrictSpatialIndex` for spatial tagging.
  - `RoadsExtractor.cs` — Joins `all.traffic_persistent`, `all.lane_connections`, and `all.lane_polygons` into `output/roads.geojson`.
  - `BuildingsExtractor.cs` — Extracts, caches `CMesh.BoundingBox`, computes OBBs, deduplicates, and classifies building footprints into `output/buildings.geojson`.
  - `PoiExtractor.cs` — Joins `03_night_city.poimappins` with `cooked_journal.journal` and links `FastTravelPoints.*` to `AlwaysLoaded` `NodeRefs` into `output/poi.geojson`.
  - `Inspector.cs` — Diagnostic probe commands (`--inspect`, `--scan`, `--probe`, `--probe2`, `--probe3`, `--probe-ft`).
- `WolvenKit-10.0.0-beta.1/` — Local WolvenKit 10 runtime DLLs and native `kraken.dll` (Oodle decompression). **Do not modify or delete.**
- `output/` — Generated output files (`regions.geojson`, `roads.geojson`, `buildings.geojson`, `poi.geojson`, `index_stats.json`).
- `preview.html` — Self-contained HTML5 Canvas viewer for inspecting the generated GeoJSON layers (pan/zoom, search, click inspector, local file picker fallback).
- `README.md` — End-user documentation (Polish).

## Build & Run Commands

```powershell
# Build Release
dotnet build -c Release Q:\Projects\cyberpunk-map\extractor --nologo

# Run full extraction (Base + EP1, raw REDengine 4 meters, Polish localization)
dotnet run -c Release --project Q:\Projects\cyberpunk-map\extractor -- --extract

# Optional CLI flags:
#   --game "<path to Cyberpunk 2077>"   (default: Q:\SteamLibrary\steamapps\common\Cyberpunk 2077)
#   --out "<output directory>"          (default: Q:\Projects\cyberpunk-map\output)
#   --verbose                           (print WolvenKit RTTI warnings & non-fatal exceptions)

# Diagnostic inspection modes (Inspector.cs)
dotnet run -c Release --project Q:\Projects\cyberpunk-map\extractor -- --inspect
dotnet run -c Release --project Q:\Projects\cyberpunk-map\extractor -- --scan
dotnet run -c Release --project Q:\Projects\cyberpunk-map\extractor -- --probe
dotnet run -c Release --project Q:\Projects\cyberpunk-map\extractor -- --probe2
dotnet run -c Release --project Q:\Projects\cyberpunk-map\extractor -- --probe3
dotnet run -c Release --project Q:\Projects\cyberpunk-map\extractor -- --probe-ft

# Serve preview.html locally
python -m http.server 8080
```

## Critical WolvenKit 10 & REDengine 4 Invariants

1. **Keep `WkitLoader` initialization before JIT**:
   `Program.Main` must only call `Runner.Run(args)` without referencing any `WolvenKit.*` types directly in `Program.Main`, ensuring `[ModuleInitializer]` (`WkitLoader.Init`) runs first to hook `AssemblyLoadContext.Default.Resolving` and add `WolvenKit-10.0.0-beta.1` via `SetDllDirectory` so `kraken.dll` loads.
2. **Coordinate System & Scope**:
   - Coordinates are **raw REDengine 4 world meters** `[x, y, z]` (`Z` = elevation). Do not convert to WGS84 unless explicitly requested.
   - Always load both Base (`archive\pc\content`) and Phantom Liberty (`archive\pc\ep1`) archives, letting EP1 override duplicate file hashes.
   - All world resources are anchored from `base\worlds\03_night_city\_compiled\default\03_night_city.streamingworld` (`worldStreamingWorld`) and its `BlockRefs` (`*.streamingblock` -> `Descriptors`).
3. **Sector Node Buffers & Cooked InnerNodes**:
   - `worldStreamingSector.NodeData.Data` is `worldNodeDataBuffer`, which itself implements `IList<worldCompiledNodeInstanceSetupInfo>` (iterate `buf` directly — do **not** read `buf.Data`).
   - Node lookup: `wss.Nodes[(int)(ulong)nd.NodeIdx]`.
   - Cooked mesh nodes (`worldCookedBuildingProxyMeshNode`, `worldCookedPrefabProxyMeshNode`, `worldCookedGenericProxyMeshNode`, `worldCookedStaticMeshNode`, `worldInstancedMeshNode`) store the mesh reference in `Buffer.Data.InnerNodes[(int)(uint)nd.InstanceIndex]`.
4. **District Polygons (`AreaShapeOutline.Buffer`)**:
   - `AreaShapeOutline.Points` is often just a `2m x 2m` placeholder (`(-1,-1)..(1,1)`).
   - The real cooked polygon lives in `AreaShapeOutline.Buffer`: `int32 vertexCount`, followed by `vertexCount` `Vector4(float x, y, z, w)`, followed by trailing `float height`. Always decode `Buffer` first (falling back to `Points`), then transform vertices by `nd.Scale`, `nd.Transform.Orientation`, and `nd.Transform.Position`.
5. **TweakDB `IsQuestDistrict` & Dogtown Precedence**:
   - In `tweakdb_ep1.bin`, `Districts.Dogtown` and `Districts.CityCenter` have `IsQuestDistrict = true`, even though they are major playable surface districts. `LocalizationHelper` explicitly overrides `IsQuestDistrict = false` for major playable districts, while keeping `Cyberspace`, `VR_Tutorial`, `Unlisted`, `SpaceStation`, `LangleyClinic`, and `Cynosure` as quest-only.
   - `DistrictSpatialIndex.Lookup` checks `Districts.Dogtown` before `Pacifica` (`Coastview` / `WestWindEstate`) so Dogtown objects are never misassigned to Pacifica.
6. **Split Traffic Resources (CP2077 2.0+)**:
   - `all.traffic_persistent` holds lane attributes (`Lanes`), `all.lane_connections` holds `InLanes`/`Outlanes`, `all.lane_polygons` holds `Outline`/`Polygon` (all indexed by `ushort` lane index), and `all.spatial_representation` holds `NeighborGroups`. `Outline` vertices are already ordered in travel direction.
7. **Localization & POIs**:
   - `onscreens_final.json` CR2W files wrap `localizationPersistenceOnScreenEntries` inside `JsonResource.Root.Chunk`. Load English (`en-us`) first as fallback, then Polish (`pl-pl`) base + EP1.
   - `JournalPathHash` in `03_night_city.mappins` and `03_night_city.poimappins` matches `cooked_journal.journal` paths via `Murmur32.Hash(path, 0x5EEDBA5E)` (also index EP1 paths with the leading `"ep1/"` stripped).
   - Fast Travel Dataterms & Metro stations match TweakDB `FastTravelPoints.*` records to `AlwaysLoaded` sector `NodeRefs` via `nd.GlobalNodeId.Hash == nodeRef.GetRedHash()`.
8. **Buildings Filtering & Performance**:
   - Use **exact runtime type checks** (`node.GetType() == typeof(...)`) in `BuildingsExtractor` because `worldRoadProxyMeshNode`, `worldInvalidProxyMeshNode`, `worldEntityProxyMeshNode`, and `worldDestructibleProxyMeshNode` inherit from `worldPrefabProxyMeshNode`.
   - Read `.mesh` files with `parseBuffers: false` and cache `CMesh.BoundingBox` by path hash (`ulong`).
   - Skip `box_occluder.w2mesh` (`worldStaticOccluderMeshNode`) in Badlands / non-urban regions and above `Level 4` or `120m` footprint / `130m` height so terrain/mountain occluders are not exported as buildings.

## Validation Checklist

After modifying any extractor code, rebuild and run `--extract`, then verify `output/`:
- `regions.geojson`, `roads.geojson`, `buildings.geojson`, `poi.geojson`, and `index_stats.json` are valid JSON / RFC 7946 GeoJSON with zero `NaN` or `Infinity` values.
- Polish names are populated in `regions.geojson` and `poi.geojson` (no unresolved `LocKey#...` or raw `FT 05` codes when localized names exist).
- `Dogtown` appears with realistic building and `dogtown_courier` POI counts.
- `roads.geojson` has `0` dangling `in_lanes` / `out_lanes` references.
- `buildings.geojson` contains valid OBB polygons (`width_m`, `length_m`, `height_m`, `area_m2`, `rotation_deg`, optional `type`) without road/entity/invalid proxy nodes or Badlands terrain occluders.
