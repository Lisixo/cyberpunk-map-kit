import { getPoiMeta } from '../constants/palette';
import type {
  BBox2D,
  BuildingFeature,
  FilterState,
  LayerVisibility,
  MapDataset,
  PoiFeature,
  RegionFeature,
  RoadFeature,
  SearchHit,
  SelectedFeature,
} from '../types/map';

const CELL_SIZE = 250; // meters

export function pointInPolygon(x: number, y: number, ring: [number, number][]): boolean {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const xi = ring[i][0];
    const yi = ring[i][1];
    const xj = ring[j][0];
    const yj = ring[j][1];
    const intersects = yi > y !== yj > y && x < ((xj - xi) * (y - yi)) / (yj - yi + 1e-12) + xi;
    if (intersects) inside = !inside;
  }
  return inside;
}

export function pointToSegmentDistSq(
  px: number,
  py: number,
  ax: number,
  ay: number,
  bx: number,
  by: number,
): number {
  const dx = bx - ax;
  const dy = by - ay;
  const lenSq = dx * dx + dy * dy;
  if (lenSq <= 1e-12) {
    const rx = px - ax;
    const ry = py - ay;
    return rx * rx + ry * ry;
  }
  let t = ((px - ax) * dx + (py - ay) * dy) / lenSq;
  if (t < 0) t = 0;
  else if (t > 1) t = 1;
  const projX = ax + t * dx;
  const projY = ay + t * dy;
  const rx = px - projX;
  const ry = py - projY;
  return rx * rx + ry * ry;
}

function cellKey(cx: number, cy: number): number {
  // Shift coordinates so indices stay comfortably inside 32-bit int packing
  return ((cx + 2048) << 16) ^ (cy + 2048);
}

export function matchesBuildingFilter(b: BuildingFeature, filters: FilterState): boolean {
  if (filters.district !== 'ALL' && b.properties.district !== filters.district) return false;
  return filters.buildingTypes[b._type] !== false;
}

export function matchesRoadFilter(r: RoadFeature, filters: FilterState): boolean {
  if (filters.district !== 'ALL' && r.properties.district !== filters.district) return false;
  return true;
}

export function matchesPoiFilter(p: PoiFeature, filters: FilterState): boolean {
  if (filters.district !== 'ALL' && p.properties.district !== filters.district) return false;
  return filters.poiCategories[p.properties.category] !== false;
}

export function matchesRegionFilter(
  reg: RegionFeature,
  visibility: LayerVisibility,
  filters: FilterState,
): boolean {
  if (!visibility.regionQuest && reg.properties.is_quest_district) return false;
  if (!visibility.regionSubdistricts && reg.properties.is_subdistrict) return false;
  if (
    filters.district !== 'ALL' &&
    reg.properties.top_district !== filters.district &&
    reg.properties.name !== filters.district
  ) {
    return false;
  }
  return true;
}

export function matchesSearchQuery(textParts: (string | null | undefined)[], query: string): boolean {
  const q = query.trim().toLowerCase();
  if (!q) return false;
  for (let i = 0; i < textParts.length; i++) {
    const val = textParts[i];
    if (val && val.toLowerCase().includes(q)) return true;
  }
  return false;
}

export class MapSpatialIndex {
  readonly dataset: MapDataset;
  readonly roadById = new Map<number, RoadFeature>();
  readonly worldBBox: BBox2D;

  private readonly buildingGrid = new Map<number, BuildingFeature[]>();
  private readonly roadGrid = new Map<number, RoadFeature[]>();
  private readonly regionsSmallestFirst: RegionFeature[];

  constructor(dataset: MapDataset) {
    this.dataset = dataset;

    for (const r of dataset.roads) {
      this.roadById.set(r.properties.id, r);
      this.insertIntoGrid(this.roadGrid, r._bbox, r);
    }

    for (const b of dataset.buildings) {
      this.insertIntoGrid(this.buildingGrid, b._bbox, b);
    }

    // Sort regions smallest-first, giving Dogtown priority over Pacifica
    this.regionsSmallestFirst = [...dataset.regions].sort((a, b) => {
      const aDog = a.properties.district_id === 'Districts.Dogtown' ? 0 : 1;
      const bDog = b.properties.district_id === 'Districts.Dogtown' ? 0 : 1;
      if (aDog !== bDog) return aDog - bDog;
      return a.properties.area_m2 - b.properties.area_m2;
    });

    if (dataset.stats?.world_bbox_2d?.length === 4) {
      this.worldBBox = dataset.stats.world_bbox_2d;
    } else {
      let minX = -3500;
      let minY = -4500;
      let maxX = 3500;
      let maxY = 3500;
      if (dataset.buildings.length > 0 || dataset.roads.length > 0) {
        minX = Infinity;
        minY = Infinity;
        maxX = -Infinity;
        maxY = -Infinity;
        for (const b of dataset.buildings) {
          if (b._bbox[0] < minX) minX = b._bbox[0];
          if (b._bbox[1] < minY) minY = b._bbox[1];
          if (b._bbox[2] > maxX) maxX = b._bbox[2];
          if (b._bbox[3] > maxY) maxY = b._bbox[3];
        }
        for (const r of dataset.roads) {
          if (r._bbox[0] < minX) minX = r._bbox[0];
          if (r._bbox[1] < minY) minY = r._bbox[1];
          if (r._bbox[2] > maxX) maxX = r._bbox[2];
          if (r._bbox[3] > maxY) maxY = r._bbox[3];
        }
      }
      this.worldBBox = [minX, minY, maxX, maxY];
    }
  }

  private insertIntoGrid<T>(grid: Map<number, T[]>, bbox: BBox2D, item: T): void {
    const minCx = Math.floor(bbox[0] / CELL_SIZE);
    const maxCx = Math.floor(bbox[2] / CELL_SIZE);
    const minCy = Math.floor(bbox[1] / CELL_SIZE);
    const maxCy = Math.floor(bbox[3] / CELL_SIZE);

    for (let cx = minCx; cx <= maxCx; cx++) {
      for (let cy = minCy; cy <= maxCy; cy++) {
        const key = cellKey(cx, cy);
        let arr = grid.get(key);
        if (!arr) {
          arr = [];
          grid.set(key, arr);
        }
        arr.push(item);
      }
    }
  }

  private queryGrid<T extends { _bbox: BBox2D }>(
    grid: Map<number, T[]>,
    wx: number,
    wy: number,
    pad: number,
  ): T[] {
    const minCx = Math.floor((wx - pad) / CELL_SIZE);
    const maxCx = Math.floor((wx + pad) / CELL_SIZE);
    const minCy = Math.floor((wy - pad) / CELL_SIZE);
    const maxCy = Math.floor((wy + pad) / CELL_SIZE);

    const seen = new Set<T>();
    const out: T[] = [];
    for (let cx = minCx; cx <= maxCx; cx++) {
      for (let cy = minCy; cy <= maxCy; cy++) {
        const bucket = grid.get(cellKey(cx, cy));
        if (!bucket) continue;
        for (let i = 0; i < bucket.length; i++) {
          const item = bucket[i];
          if (!seen.has(item)) {
            seen.add(item);
            out.push(item);
          }
        }
      }
    }
    return out;
  }

  findDistrictAt(
    wx: number,
    wy: number,
    includeQuest = false,
  ): { district: string | null; subdistrict: string | null; region: RegionFeature | null } {
    let bestSub: RegionFeature | null = null;
    let bestTop: RegionFeature | null = null;

    // First check Dogtown specifically (AGENTS.md invariant #5)
    for (let i = 0; i < this.regionsSmallestFirst.length; i++) {
      const reg = this.regionsSmallestFirst[i];
      if (!includeQuest && reg.properties.is_quest_district) continue;
      const b = reg._bbox;
      if (wx < b[0] || wx > b[2] || wy < b[1] || wy > b[3]) continue;
      if (!pointInPolygon(wx, wy, reg.geometry.coordinates[0])) continue;

      if (reg.properties.district_id === 'Districts.Dogtown') {
        return {
          district: 'Dogtown',
          subdistrict: null,
          region: reg,
        };
      }
      if (!bestSub && reg.properties.is_subdistrict) {
        bestSub = reg;
      } else if (!bestTop && !reg.properties.is_subdistrict) {
        bestTop = reg;
      }
      if (bestSub && bestTop) break;
    }

    const chosen = bestSub ?? bestTop;
    if (!chosen) return { district: null, subdistrict: null, region: null };

    return {
      district: chosen.properties.top_district || chosen.properties.parent_district || chosen.properties.name,
      subdistrict: chosen.properties.is_subdistrict ? chosen.properties.name : null,
      region: chosen,
    };
  }

  hitTest2D(
    wx: number,
    wy: number,
    toleranceMeters: number,
    visibility: LayerVisibility,
    filters: FilterState,
  ): SelectedFeature | null {
    // 1. POIs
    if (visibility.poi) {
      const poiTol = toleranceMeters * 1.45;
      const poiTolSq = poiTol * poiTol;
      let bestPoi: PoiFeature | null = null;
      let bestDistSq = poiTolSq;

      for (let i = 0; i < this.dataset.poi.length; i++) {
        const p = this.dataset.poi[i];
        if (!matchesPoiFilter(p, filters)) continue;
        const dx = p.geometry.coordinates[0] - wx;
        const dy = p.geometry.coordinates[1] - wy;
        const dSq = dx * dx + dy * dy;
        if (dSq <= bestDistSq) {
          bestDistSq = dSq;
          bestPoi = p;
        }
      }
      if (bestPoi) return { kind: 'poi', feature: bestPoi };
    }

    // 2. Buildings
    if (visibility.buildings) {
      const candidates = this.queryGrid(this.buildingGrid, wx, wy, 10);
      let bestBuilding: BuildingFeature | null = null;
      let bestArea = Infinity;

      for (let i = 0; i < candidates.length; i++) {
        const b = candidates[i];
        if (!matchesBuildingFilter(b, filters)) continue;
        if (
          wx < b._bbox[0] ||
          wx > b._bbox[2] ||
          wy < b._bbox[1] ||
          wy > b._bbox[3]
        ) {
          continue;
        }
        if (pointInPolygon(wx, wy, b.geometry.coordinates[0])) {
          if (b.properties.area_m2 < bestArea) {
            bestArea = b.properties.area_m2;
            bestBuilding = b;
          }
        }
      }
      if (bestBuilding) return { kind: 'building', feature: bestBuilding };
    }

    // 3. Roads
    if (visibility.roads) {
      const candidates = this.queryGrid(this.roadGrid, wx, wy, toleranceMeters);
      const tolSq = toleranceMeters * toleranceMeters;
      let bestRoad: RoadFeature | null = null;
      let bestDistSq = tolSq;

      for (let i = 0; i < candidates.length; i++) {
        const r = candidates[i];
        if (!matchesRoadFilter(r, filters)) continue;
        const rb = r._bbox;
        if (
          wx < rb[0] - toleranceMeters ||
          wx > rb[2] + toleranceMeters ||
          wy < rb[1] - toleranceMeters ||
          wy > rb[3] + toleranceMeters
        ) {
          continue;
        }
        const pts = r.geometry.coordinates;
        for (let j = 0; j < pts.length - 1; j++) {
          const dSq = pointToSegmentDistSq(
            wx,
            wy,
            pts[j][0],
            pts[j][1],
            pts[j + 1][0],
            pts[j + 1][1],
          );
          if (dSq <= bestDistSq) {
            bestDistSq = dSq;
            bestRoad = r;
          }
        }
      }
      if (bestRoad) return { kind: 'road', feature: bestRoad };
    }

    // 4. Regions
    if (visibility.regions) {
      for (let i = 0; i < this.regionsSmallestFirst.length; i++) {
        const reg = this.regionsSmallestFirst[i];
        if (!matchesRegionFilter(reg, visibility, filters)) continue;
        const b = reg._bbox;
        if (wx < b[0] || wx > b[2] || wy < b[1] || wy > b[3]) continue;
        if (pointInPolygon(wx, wy, reg.geometry.coordinates[0])) {
          return { kind: 'region', feature: reg };
        }
      }
    }

    return null;
  }

  search(query: string, limit = 36): SearchHit[] {
    const q = query.trim().toLowerCase();
    if (!q) return [];

    const hits: SearchHit[] = [];

    // 1. Regions (top districts first, then subdistricts)
    for (const reg of this.dataset.regions) {
      if (reg.properties.is_quest_district) continue;
      const hay = `${reg.properties.name} ${reg.properties.top_district} ${reg.properties.district_id}`.toLowerCase();
      if (hay.includes(q)) {
        const span = Math.max(
          reg._bbox[2] - reg._bbox[0],
          reg._bbox[3] - reg._bbox[1],
          400,
        );
        hits.push({
          kind: 'region',
          feature: reg,
          label: reg.properties.name,
          subtitle: reg.properties.is_subdistrict
            ? `Poddzielnica · ${reg.properties.top_district}`
            : 'Dzielnica',
          x: reg.properties.center[0],
          y: reg.properties.center[1],
          zoom2d: reg.properties.is_subdistrict ? -0.4 : -1.2,
          distance3d: Math.min(5500, Math.max(900, span * 1.15)),
        });
      }
      if (hits.length >= 10) break;
    }

    // 2. POIs
    let poiCount = 0;
    for (const p of this.dataset.poi) {
      const meta = getPoiMeta(p.properties.category);
      const hay = `${p.properties.name} ${meta.label} ${p.properties.category} ${p.properties.district ?? ''} ${p.properties.subdistrict ?? ''}`.toLowerCase();
      if (hay.includes(q)) {
        hits.push({
          kind: 'poi',
          feature: p,
          label: p.properties.name,
          subtitle: `${meta.label} · ${p.properties.subdistrict ?? p.properties.district ?? 'Night City'}`,
          x: p.geometry.coordinates[0],
          y: p.geometry.coordinates[1],
          z: p.geometry.coordinates[2],
          zoom2d: 1.4,
          distance3d: 360,
        });
        poiCount++;
        if (poiCount >= 14) break;
      }
    }

    // 3. Buildings
    let bldCount = 0;
    for (const b of this.dataset.buildings) {
      const hay = `${b.properties.name} ${b._type} ${b.properties.district ?? ''} ${b.properties.subdistrict ?? ''}`.toLowerCase();
      if (hay.includes(q)) {
        const cx = b.properties.location[0];
        const cy = b.properties.location[1];
        hits.push({
          kind: 'building',
          feature: b,
          label: b.properties.name || `Budynek #${b.properties.id}`,
          subtitle: `${b._type} (${Math.round(b.properties.height_m)} m) · ${b.properties.subdistrict ?? b.properties.district ?? ''}`,
          x: cx,
          y: cy,
          z: b.properties.top_z,
          zoom2d: 1.5,
          distance3d: Math.max(260, b.properties.height_m * 3.5),
        });
        bldCount++;
        if (bldCount >= 12 || hits.length >= limit) break;
      }
    }

    return hits.slice(0, limit);
  }
}
