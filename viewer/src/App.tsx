import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  BUILDING_TYPE_ORDER,
  DISTRICT_PRESETS,
  INITIAL_CAMERA,
  POI_CATEGORY_META,
} from './constants/palette';
import { loadFromFiles, loadFromServer, type LoadProgressState } from './data/loader';
import {
  MapSpatialIndex,
  matchesBuildingFilter,
  matchesPoiFilter,
  matchesRoadFilter,
} from './data/spatialIndex';
import { InspectorPanel } from './components/InspectorPanel';
import { LeafletMapView } from './components/LeafletMapView';
import { LoadingOverlay } from './components/LoadingOverlay';
import { Sidebar } from './components/Sidebar';
import { TelemetryBar } from './components/TelemetryBar';
import { ThreeMapView } from './components/ThreeMapView';
import { TopBar } from './components/TopBar';
import type {
  CameraSyncState,
  CursorTelemetry,
  FilterState,
  FlyTarget,
  LayerVisibility,
  MapDataset,
  MapViewportRef,
  RoadFeature,
  SearchHit,
  SelectedFeature,
  ThreeSettings,
  ViewMode,
} from './types/map';

function createInitialFilters(): FilterState {
  const buildingTypes: Record<string, boolean> = {};
  for (const t of BUILDING_TYPE_ORDER) buildingTypes[t] = true;

  const poiCategories: Record<string, boolean> = {};
  for (const k of Object.keys(POI_CATEGORY_META)) poiCategories[k] = true;

  return {
    district: 'ALL',
    buildingTypes,
    poiCategories,
    buildingColorMode: 'type',
    roadColorMode: 'speed',
    searchQuery: '',
  };
}

const INITIAL_PROGRESS: LoadProgressState = {
  statuses: {
    stats: 'loading',
    regions: 'loading',
    poi: 'loading',
    buildings: 'loading',
    roads: 'loading',
  },
  counts: {},
  errors: {},
};

export function App() {
  const [dataset, setDataset] = useState<MapDataset | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [progress, setProgress] = useState<LoadProgressState>(INITIAL_PROGRESS);

  const [viewMode, setViewMode] = useState<ViewMode>('3d');
  const [sidebarOpen, setSidebarOpen] = useState(true);

  const [visibility, setVisibility] = useState<LayerVisibility>({
    regions: true,
    regionSubdistricts: false,
    regionQuest: false,
    regionLabels: true,
    roads: true,
    buildings: true,
    poi: true,
  });

  const [filters, setFilters] = useState<FilterState>(createInitialFilters);
  const [threeSettings, setThreeSettings] = useState<ThreeSettings>({
    zScale: 1.0,
    buildingStyle: 'solid',
    showGrid: true,
    showFog: true,
    autoOrbit: false,
  });

  const [selected, setSelected] = useState<SelectedFeature | null>(null);
  const [pendingFlyTarget, setPendingFlyTarget] = useState<FlyTarget | null>(null);
  const [telemetry, setTelemetry] = useState<CursorTelemetry>({
    x: INITIAL_CAMERA.x,
    y: INITIAL_CAMERA.y,
    district: 'City Center',
    subdistrict: null,
  });

  const cameraSyncRef = useRef<CameraSyncState>({ ...INITIAL_CAMERA });
  const viewportRef = useRef<MapViewportRef | null>(null);
  const searchInputRef = useRef<HTMLInputElement | null>(null);
  const flyNonceRef = useRef(0);

  const startLoad = useCallback(() => {
    const controller = new AbortController();
    setIsLoading(true);
    loadFromServer((state) => setProgress(state), controller.signal).then((loaded) => {
      if (controller.signal.aborted) return;
      setDataset(loaded);
      setIsLoading(false);
    });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    return startLoad();
  }, [startLoad]);

  const spatialIndex = useMemo(() => {
    if (!dataset) return null;
    return new MapSpatialIndex(dataset);
  }, [dataset]);

  const hasAnyData = useMemo(() => {
    if (!dataset) return false;
    return (
      dataset.regions.length > 0 ||
      dataset.roads.length > 0 ||
      dataset.buildings.length > 0 ||
      dataset.poi.length > 0
    );
  }, [dataset]);

  const visibleCounts = useMemo(() => {
    if (!dataset) return { buildings: 0, roads: 0, poi: 0 };
    let buildings = 0;
    if (visibility.buildings) {
      for (const b of dataset.buildings) {
        if (matchesBuildingFilter(b, filters)) buildings++;
      }
    }
    let roads = 0;
    if (visibility.roads) {
      for (const r of dataset.roads) {
        if (matchesRoadFilter(r, filters)) roads++;
      }
    }
    let poi = 0;
    if (visibility.poi) {
      for (const p of dataset.poi) {
        if (matchesPoiFilter(p, filters)) poi++;
      }
    }
    return { buildings, roads, poi };
  }, [dataset, visibility.buildings, visibility.roads, visibility.poi, filters]);

  const flyTo = useCallback((target: Omit<FlyTarget, 'nonce'>) => {
    flyNonceRef.current += 1;
    setPendingFlyTarget({ ...target, nonce: flyNonceRef.current });
  }, []);

  const handleJumpDistrict = useCallback(
    (districtId: string) => {
      const preset = DISTRICT_PRESETS.find((p) => p.id === districtId);
      if (preset) {
        flyTo({
          x: preset.x,
          y: preset.y,
          zoom2d: preset.zoom2d,
          distance3d: preset.distance3d,
        });
        return;
      }
      const reg = dataset?.regions.find(
        (r) =>
          !r.properties.is_quest_district &&
          (r.properties.name === districtId || r.properties.top_district === districtId),
      );
      if (reg) {
        const span = Math.max(reg._bbox[2] - reg._bbox[0], reg._bbox[3] - reg._bbox[1], 600);
        flyTo({
          x: reg.properties.center[0],
          y: reg.properties.center[1],
          zoom2d: -1.1,
          distance3d: Math.min(5500, Math.max(950, span * 0.9)),
        });
      }
    },
    [dataset, flyTo],
  );

  const handleSelectSearchHit = useCallback(
    (hit: SearchHit) => {
      if (hit.kind === 'building') {
        setSelected({ kind: 'building', feature: hit.feature as never });
      } else if (hit.kind === 'poi') {
        setSelected({ kind: 'poi', feature: hit.feature as never });
      } else if (hit.kind === 'region') {
        setSelected({ kind: 'region', feature: hit.feature as never });
      } else if (hit.kind === 'road') {
        setSelected({ kind: 'road', feature: hit.feature as never });
      }
      flyTo({
        x: hit.x,
        y: hit.y,
        z: hit.z,
        zoom2d: hit.zoom2d,
        distance3d: hit.distance3d,
      });
    },
    [flyTo],
  );

  const handleSelectRoad = useCallback(
    (road: RoadFeature) => {
      setSelected({ kind: 'road', feature: road });
      const pts = road.geometry.coordinates;
      const mid = pts[Math.floor(pts.length / 2)] ?? pts[0];
      if (mid) {
        flyTo({
          x: mid[0],
          y: mid[1],
          z: mid[2],
          zoom2d: 1.5,
          distance3d: 320,
        });
      }
    },
    [flyTo],
  );

  const handleCenterOnSelected = useCallback(() => {
    if (!selected) return;
    if (selected.kind === 'building') {
      const [x, y, z] = selected.feature.properties.location;
      flyTo({
        x,
        y,
        z: selected.feature.properties.top_z ?? z,
        zoom2d: 1.5,
        distance3d: Math.max(240, selected.feature.properties.height_m * 3.5),
      });
    } else if (selected.kind === 'poi') {
      const [x, y, z] = selected.feature.geometry.coordinates;
      flyTo({ x, y, z, zoom2d: 1.5, distance3d: 340 });
    } else if (selected.kind === 'road') {
      const pts = selected.feature.geometry.coordinates;
      const mid = pts[Math.floor(pts.length / 2)] ?? pts[0];
      if (mid) flyTo({ x: mid[0], y: mid[1], z: mid[2], zoom2d: 1.5, distance3d: 320 });
    } else if (selected.kind === 'region') {
      const reg = selected.feature;
      const span = Math.max(reg._bbox[2] - reg._bbox[0], reg._bbox[3] - reg._bbox[1], 500);
      flyTo({
        x: reg.properties.center[0],
        y: reg.properties.center[1],
        zoom2d: reg.properties.is_subdistrict ? -0.4 : -1.2,
        distance3d: Math.min(5500, Math.max(900, span * 1.1)),
      });
    }
  }, [selected, flyTo]);

  const handleUploadFiles = useCallback(
    async (files: FileList) => {
      const merged = await loadFromFiles(files, dataset ?? undefined);
      setDataset(merged);
      setIsLoading(false);
    },
    [dataset],
  );

  // Global keyboard shortcuts: Ctrl+K, Esc, 2, 3
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        searchInputRef.current?.focus();
        searchInputRef.current?.select();
        return;
      }

      const tag = (e.target as HTMLElement | null)?.tagName;
      const inInput = tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT';

      if (e.key === 'Escape' && !inInput) {
        setSelected(null);
        return;
      }

      if (!inInput && !e.ctrlKey && !e.metaKey && !e.altKey) {
        if (e.key === '3') setViewMode('3d');
        else if (e.key === '2') setViewMode('2d');
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  return (
    <div className="app-shell">
      <TopBar
        viewMode={viewMode}
        onChangeViewMode={setViewMode}
        sidebarOpen={sidebarOpen}
        onToggleSidebar={() => setSidebarOpen((v) => !v)}
        searchQuery={filters.searchQuery}
        onChangeSearchQuery={(q) => setFilters((prev) => ({ ...prev, searchQuery: q }))}
        spatialIndex={spatialIndex}
        onSelectSearchHit={handleSelectSearchHit}
        onJumpDistrict={handleJumpDistrict}
        onFitAll={() => viewportRef.current?.fitAll()}
        searchInputRef={searchInputRef}
      />

      <div className="workspace">
        {dataset && (
          <Sidebar
            open={sidebarOpen}
            dataset={dataset}
            viewMode={viewMode}
            visibility={visibility}
            onChangeVisibility={setVisibility}
            filters={filters}
            onChangeFilters={setFilters}
            threeSettings={threeSettings}
            onChangeThreeSettings={setThreeSettings}
            onJumpDistrict={handleJumpDistrict}
            onUploadFiles={handleUploadFiles}
          />
        )}

        <main className="stage">
          {spatialIndex && hasAnyData && (
            <>
              {viewMode === '3d' ? (
                <ThreeMapView
                  ref={viewportRef}
                  spatialIndex={spatialIndex}
                  visibility={visibility}
                  filters={filters}
                  threeSettings={threeSettings}
                  selected={selected}
                  onSelect={setSelected}
                  cameraSyncRef={cameraSyncRef}
                  onTelemetry={setTelemetry}
                  pendingFlyTarget={pendingFlyTarget}
                />
              ) : (
                <LeafletMapView
                  ref={viewportRef}
                  spatialIndex={spatialIndex}
                  visibility={visibility}
                  filters={filters}
                  selected={selected}
                  onSelect={setSelected}
                  cameraSyncRef={cameraSyncRef}
                  onTelemetry={setTelemetry}
                  pendingFlyTarget={pendingFlyTarget}
                />
              )}

              <InspectorPanel
                selected={selected}
                spatialIndex={spatialIndex}
                onClose={() => setSelected(null)}
                onSelectRoad={handleSelectRoad}
                onCenterOnSelected={handleCenterOnSelected}
              />

              <TelemetryBar
                telemetry={telemetry}
                viewMode={viewMode}
                visibleCounts={visibleCounts}
              />
            </>
          )}

          <LoadingOverlay
            progress={progress}
            hasAnyData={hasAnyData}
            isLoading={isLoading}
            onRetry={startLoad}
            onUploadFiles={handleUploadFiles}
          />
        </main>
      </div>
    </div>
  );
}

export default App;
