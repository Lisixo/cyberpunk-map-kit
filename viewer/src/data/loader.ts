import type {
  BBox2D,
  BuildingFeature,
  IndexStats,
  MapDataset,
  PoiFeature,
  RegionFeature,
  RoadFeature,
} from '../types/map';

export type LayerKey = 'stats' | 'regions' | 'poi' | 'buildings' | 'roads';
export type LayerLoadStatus = 'idle' | 'loading' | 'loaded' | 'error';

export interface LoadProgressState {
  statuses: Record<LayerKey, LayerLoadStatus>;
  counts: Partial<Record<LayerKey, number>>;
  errors: Partial<Record<LayerKey, string>>;
}

function computePolygonBBox(ring: [number, number][]): BBox2D {
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (let i = 0; i < ring.length; i++) {
    const x = ring[i][0];
    const y = ring[i][1];
    if (x < minX) minX = x;
    if (y < minY) minY = y;
    if (x > maxX) maxX = x;
    if (y > maxY) maxY = y;
  }
  return [minX, minY, maxX, maxY];
}

function computeLineBBox(coords: [number, number, number][]): BBox2D {
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (let i = 0; i < coords.length; i++) {
    const x = coords[i][0];
    const y = coords[i][1];
    if (x < minX) minX = x;
    if (y < minY) minY = y;
    if (x > maxX) maxX = x;
    if (y > maxY) maxY = y;
  }
  return [minX, minY, maxX, maxY];
}

function prepareRegions(raw: unknown): RegionFeature[] {
  const fc = raw as { features?: RegionFeature[] };
  const features = Array.isArray(fc?.features) ? fc.features : [];
  const result: RegionFeature[] = [];
  for (const f of features) {
    if (!f?.geometry || f.geometry.type !== 'Polygon') continue;
    const ring = f.geometry.coordinates?.[0];
    if (!ring || ring.length < 3) continue;
    f._bbox = f.properties?.bbox?.length === 4 ? f.properties.bbox : computePolygonBBox(ring);
    result.push(f);
  }
  return result;
}

function prepareRoads(raw: unknown): RoadFeature[] {
  const fc = raw as { features?: RoadFeature[] };
  const features = Array.isArray(fc?.features) ? fc.features : [];
  const result: RoadFeature[] = [];
  for (const f of features) {
    if (!f?.geometry || f.geometry.type !== 'LineString') continue;
    const coords = f.geometry.coordinates;
    if (!coords || coords.length < 2) continue;
    f._bbox = computeLineBBox(coords);
    result.push(f);
  }
  return result;
}

function prepareBuildings(raw: unknown): BuildingFeature[] {
  const fc = raw as { features?: BuildingFeature[] };
  const features = Array.isArray(fc?.features) ? fc.features : [];
  const result: BuildingFeature[] = [];
  for (const f of features) {
    if (!f?.geometry || f.geometry.type !== 'Polygon') continue;
    const ring = f.geometry.coordinates?.[0];
    if (!ring || ring.length < 4) continue;
    f._bbox = computePolygonBBox(ring);
    f._type = f.properties?.type ?? 'unknown';
    result.push(f);
  }
  return result;
}

function preparePoi(raw: unknown): PoiFeature[] {
  const fc = raw as { features?: PoiFeature[] };
  const features = Array.isArray(fc?.features) ? fc.features : [];
  const result: PoiFeature[] = [];
  for (const f of features) {
    if (!f?.geometry || f.geometry.type !== 'Point') continue;
    const c = f.geometry.coordinates;
    if (!c || c.length < 2) continue;
    f._bbox = [c[0], c[1], c[0], c[1]];
    result.push(f);
  }
  return result;
}

async function fetchJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(url, { signal });
  if (!res.ok) {
    throw new Error(`HTTP ${res.status} (${url})`);
  }
  return (await res.json()) as T;
}

export async function loadFromServer(
  onProgress: (state: LoadProgressState) => void,
  signal?: AbortSignal,
): Promise<MapDataset> {
  const state: LoadProgressState = {
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

  const emit = () => {
    onProgress({
      statuses: { ...state.statuses },
      counts: { ...state.counts },
      errors: { ...state.errors },
    });
  };
  emit();

  let stats: IndexStats | null = null;
  let regions: RegionFeature[] = [];
  let poi: PoiFeature[] = [];
  let buildings: BuildingFeature[] = [];
  let roads: RoadFeature[] = [];

  await Promise.allSettled([
    fetchJson<IndexStats>('/data/index_stats.json', signal)
      .then((json) => {
        stats = json;
        state.statuses.stats = 'loaded';
        emit();
      })
      .catch((err: unknown) => {
        state.statuses.stats = 'error';
        state.errors.stats = err instanceof Error ? err.message : String(err);
        emit();
      }),

    fetchJson<unknown>('/data/regions.geojson', signal)
      .then((json) => {
        regions = prepareRegions(json);
        state.statuses.regions = 'loaded';
        state.counts.regions = regions.length;
        emit();
      })
      .catch((err: unknown) => {
        state.statuses.regions = 'error';
        state.errors.regions = err instanceof Error ? err.message : String(err);
        emit();
      }),

    fetchJson<unknown>('/data/poi.geojson', signal)
      .then((json) => {
        poi = preparePoi(json);
        state.statuses.poi = 'loaded';
        state.counts.poi = poi.length;
        emit();
      })
      .catch((err: unknown) => {
        state.statuses.poi = 'error';
        state.errors.poi = err instanceof Error ? err.message : String(err);
        emit();
      }),

    fetchJson<unknown>('/data/buildings.geojson', signal)
      .then((json) => {
        buildings = prepareBuildings(json);
        state.statuses.buildings = 'loaded';
        state.counts.buildings = buildings.length;
        emit();
      })
      .catch((err: unknown) => {
        state.statuses.buildings = 'error';
        state.errors.buildings = err instanceof Error ? err.message : String(err);
        emit();
      }),

    fetchJson<unknown>('/data/roads.geojson', signal)
      .then((json) => {
        roads = prepareRoads(json);
        state.statuses.roads = 'loaded';
        state.counts.roads = roads.length;
        emit();
      })
      .catch((err: unknown) => {
        state.statuses.roads = 'error';
        state.errors.roads = err instanceof Error ? err.message : String(err);
        emit();
      }),
  ]);

  return { regions, roads, buildings, poi, stats };
}

export async function loadFromFiles(files: FileList, existing?: MapDataset): Promise<MapDataset> {
  const dataset: MapDataset = {
    regions: existing?.regions ?? [],
    roads: existing?.roads ?? [],
    buildings: existing?.buildings ?? [],
    poi: existing?.poi ?? [],
    stats: existing?.stats ?? null,
  };

  for (const file of Array.from(files)) {
    const name = file.name.toLowerCase();
    const json = JSON.parse(await file.text()) as unknown;
    if (name.includes('region')) {
      dataset.regions = prepareRegions(json);
    } else if (name.includes('road')) {
      dataset.roads = prepareRoads(json);
    } else if (name.includes('building')) {
      dataset.buildings = prepareBuildings(json);
    } else if (name.includes('poi')) {
      dataset.poi = preparePoi(json);
    } else if (name.includes('stats')) {
      dataset.stats = json as IndexStats;
    }
  }

  return dataset;
}
