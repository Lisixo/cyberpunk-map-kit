export type BBox2D = [number, number, number, number]; // [minX, minY, maxX, maxY]

export interface RegionProperties {
  id: number;
  district_id: string;
  enum_name: string;
  name: string;
  parent_district_id: string | null;
  parent_district: string | null;
  top_district: string;
  is_subdistrict: boolean;
  is_quest_district: boolean;
  area_m2: number;
  z_min: number;
  z_max: number;
  center: [number, number];
  bbox: BBox2D;
  source_sector: string;
}

export interface RegionFeature {
  type: 'Feature';
  id: number;
  geometry: {
    type: 'Polygon';
    coordinates: [number, number][][];
  };
  properties: RegionProperties;
  _bbox: BBox2D;
}

export interface RoadProperties {
  id: number;
  direction: string;
  road_bidirectional: boolean;
  group_lane_count: number;
  neighbor_group_index: number;
  lane_number: number;
  seq_number: number;
  is_reversed: boolean;
  length_m: number;
  width_m: number;
  max_speed: number;
  flags: number;
  sub_graph_id: number;
  dead_end_start: boolean;
  in_lanes: number[];
  out_lanes: number[];
  road_materials: string[];
  district: string | null;
  subdistrict: string | null;
}

export interface RoadFeature {
  type: 'Feature';
  id: number;
  geometry: {
    type: 'LineString';
    coordinates: [number, number, number][];
  };
  properties: RoadProperties;
  _bbox: BBox2D;
}

export interface BuildingProperties {
  id: number;
  name: string;
  type: string | null;
  location: [number, number, number];
  width_m: number;
  length_m: number;
  height_m: number;
  area_m2: number;
  base_z: number;
  top_z: number;
  rotation_deg: number;
  district: string | null;
  subdistrict: string | null;
  source_type: string;
  mesh_path: string;
}

export interface BuildingFeature {
  type: 'Feature';
  id: number;
  geometry: {
    type: 'Polygon';
    coordinates: [number, number][][];
  };
  properties: BuildingProperties;
  _bbox: BBox2D;
  _type: string;
}

export interface PoiProperties {
  id: number;
  name: string;
  category: string;
  variant: string | null;
  district: string | null;
  subdistrict: string | null;
  source: string;
  ref_id: string;
}

export interface PoiFeature {
  type: 'Feature';
  id: number;
  geometry: {
    type: 'Point';
    coordinates: [number, number, number];
  };
  properties: PoiProperties;
  _bbox: BBox2D;
}

export interface IndexStats {
  generated_at: string;
  elapsed_seconds: number;
  game_directory: string;
  coordinate_system: string;
  language: string;
  scope: string;
  world_bbox_2d: BBox2D;
  counts: {
    regions: number;
    surface_regions: number;
    road_lanes: number;
    road_groups: number;
    road_bidirectional_groups: number;
    road_connections: number;
    buildings: number;
    building_raw_candidates: number;
    poi: number;
    poi_fast_travel: number;
    poi_metro: number;
    non_fatal_rtti_errors: number;
  };
  buildings_by_type: Record<string, number>;
  buildings_by_source: Record<string, number>;
  buildings_by_district: Record<string, number>;
  poi_by_category: Record<string, number>;
  poi_by_district: Record<string, number>;
}

export interface MapDataset {
  regions: RegionFeature[];
  roads: RoadFeature[];
  buildings: BuildingFeature[];
  poi: PoiFeature[];
  stats: IndexStats | null;
}

export type ViewMode = '3d' | '2d';
export type BuildingColorMode = 'type' | 'height' | 'district';
export type RoadColorMode = 'speed' | 'elevation' | 'district';
export type BuildingRenderStyle = 'solid' | 'hologram';

export type SelectedFeature =
  | { kind: 'building'; feature: BuildingFeature }
  | { kind: 'road'; feature: RoadFeature }
  | { kind: 'poi'; feature: PoiFeature }
  | { kind: 'region'; feature: RegionFeature };

export interface LayerVisibility {
  regions: boolean;
  regionSubdistricts: boolean;
  regionQuest: boolean;
  regionLabels: boolean;
  roads: boolean;
  buildings: boolean;
  poi: boolean;
}

export interface FilterState {
  district: string; // 'ALL' or district name
  buildingTypes: Record<string, boolean>;
  poiCategories: Record<string, boolean>;
  buildingColorMode: BuildingColorMode;
  roadColorMode: RoadColorMode;
  searchQuery: string;
}

export interface ThreeSettings {
  zScale: number;
  buildingStyle: BuildingRenderStyle;
  showGrid: boolean;
  showFog: boolean;
  autoOrbit: boolean;
}

export interface CameraSyncState {
  x: number;
  y: number;
  zoom2d: number;
}

export interface CursorTelemetry {
  x: number;
  y: number;
  z?: number;
  district: string | null;
  subdistrict: string | null;
}

export interface FlyTarget {
  x: number;
  y: number;
  z?: number;
  zoom2d?: number;
  distance3d?: number;
  nonce: number;
}

export interface SearchHit {
  kind: SelectedFeature['kind'];
  feature: BuildingFeature | RoadFeature | PoiFeature | RegionFeature;
  label: string;
  subtitle: string;
  x: number;
  y: number;
  z?: number;
  zoom2d: number;
  distance3d: number;
}

export interface MapViewportRef {
  flyTo: (target: Omit<FlyTarget, 'nonce'>) => void;
  fitAll: () => void;
}
