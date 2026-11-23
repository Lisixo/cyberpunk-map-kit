export interface BuildingTypeMeta {
  label: string;
  color: string;
}

export const BUILDING_TYPE_ORDER = [
  'megabuilding',
  'skyscraper',
  'highrise',
  'corporate',
  'commercial',
  'industrial',
  'residential',
  'infrastructure',
  'unknown',
] as const;

export const BUILDING_TYPE_META: Record<string, BuildingTypeMeta> = {
  megabuilding: { label: 'Megablok', color: '#ff2a6d' },
  skyscraper: { label: 'Wieżowiec', color: '#c084fc' },
  highrise: { label: 'Wysokościowiec', color: '#a78bfa' },
  corporate: { label: 'Korporacyjny', color: '#38bdf8' },
  commercial: { label: 'Komercyjny', color: '#facc15' },
  industrial: { label: 'Przemysłowy', color: '#fb923c' },
  residential: { label: 'Mieszkalny', color: '#4ade80' },
  infrastructure: { label: 'Infrastruktura', color: '#94a3b8' },
  unknown: { label: 'Nieznany', color: '#4b5e78' },
};

export const DISTRICT_COLORS: Record<string, string> = {
  'City Center': '#00f0ff',
  Watson: '#38bdf8',
  Westbrook: '#c084fc',
  Heywood: '#facc15',
  Pacifica: '#2dd4bf',
  'Santo Domingo': '#fb923c',
  Dogtown: '#ff2a6d',
  Badlands: '#d97706',
  'South Badlands': '#b45309',
  'Poza dzielnicami / Badlands': '#64748b',
  'q307Langley Clinic': '#f472b6',
};

export function getDistrictColor(district: string | null | undefined): string {
  if (!district) return '#64748b';
  return DISTRICT_COLORS[district] ?? '#64748b';
}

export type PoiGroupId = 'transport' | 'contracts' | 'services' | 'other';

export interface PoiCategoryMeta {
  label: string;
  color: string;
  group: PoiGroupId;
  shape?: 'diamond' | 'square' | 'circle';
}

export const POI_GROUPS: Record<PoiGroupId, string> = {
  transport: 'Transport',
  contracts: 'Kontrakty & NCPD',
  services: 'Usługi & Sklepy',
  other: 'Inne & Aktywności',
};

export const POI_CATEGORY_META: Record<string, PoiCategoryMeta> = {
  fast_travel: { label: 'Szybka podróż', color: '#00f0ff', group: 'transport', shape: 'diamond' },
  metro_station: { label: 'Stacja metra', color: '#38bdf8', group: 'transport', shape: 'square' },
  autofixer: { label: 'Autofixer', color: '#22d3ee', group: 'transport', shape: 'circle' },

  ncpd_scanner: { label: 'Skaner NCPD', color: '#60a5fa', group: 'contracts', shape: 'circle' },
  gig: { label: 'Kontrakt', color: '#2dd4bf', group: 'contracts', shape: 'circle' },
  side_job: { label: 'Zadanie poboczne', color: '#fcee09', group: 'contracts', shape: 'circle' },
  cyberpsycho: { label: 'Cyberpsychoza', color: '#ff2a6d', group: 'contracts', shape: 'diamond' },
  fixer: { label: 'Fixer', color: '#f59e0b', group: 'contracts', shape: 'square' },
  dogtown_courier: { label: 'Kurier Dogtown', color: '#fb7185', group: 'contracts', shape: 'circle' },

  ripperdoc: { label: 'Ripperdoc', color: '#e879f9', group: 'services', shape: 'circle' },
  ripperdoc_style: { label: 'Ripperdoc (styl)', color: '#d946ef', group: 'services', shape: 'circle' },
  shop_weapons: { label: 'Sklep z bronią', color: '#f87171', group: 'services', shape: 'circle' },
  shop_melee: { label: 'Broń biała', color: '#fb923c', group: 'services', shape: 'circle' },
  shop_clothes: { label: 'Odzież', color: '#a78bfa', group: 'services', shape: 'circle' },
  shop_food: { label: 'Jedzenie', color: '#4ade80', group: 'services', shape: 'circle' },
  bar: { label: 'Bar / Klub', color: '#c084fc', group: 'services', shape: 'circle' },
  shop_meds: { label: 'Apteka / Meds', color: '#34d399', group: 'services', shape: 'circle' },
  shop_netrunner: { label: 'Netrunner', color: '#22d3ee', group: 'services', shape: 'circle' },
  shop_junk: { label: 'Graciarnia', color: '#a3e635', group: 'services', shape: 'circle' },
  service_point: { label: 'Punkt usługowy', color: '#94a3b8', group: 'services', shape: 'circle' },

  apartment: { label: 'Mieszkanie', color: '#facc15', group: 'other', shape: 'square' },
  tarot: { label: 'Tarot', color: '#f472b6', group: 'other', shape: 'diamond' },
  street_race: { label: 'Wyścig uliczny', color: '#f97316', group: 'other', shape: 'diamond' },
  world_encounter: { label: 'Zdarzenie w świecie', color: '#cbd5e1', group: 'other', shape: 'circle' },
  poi_other: { label: 'Inne POI', color: '#94a3b8', group: 'other', shape: 'circle' },
};

export function getPoiMeta(category: string): PoiCategoryMeta {
  return (
    POI_CATEGORY_META[category] ?? {
      label: category,
      color: '#94a3b8',
      group: 'other',
      shape: 'circle',
    }
  );
}

export function getBuildingTypeColor(type: string | null | undefined): string {
  const key = type ?? 'unknown';
  return BUILDING_TYPE_META[key]?.color ?? BUILDING_TYPE_META.unknown.color;
}

export function getRoadSpeedColor(maxSpeed: number): string {
  if (maxSpeed >= 15) return '#fcee09'; // autostrada / ekspresówka
  if (maxSpeed >= 10) return '#00f0ff'; // arteria miejska
  if (maxSpeed >= 4) return '#f59e0b';  // ulica
  return '#3b82f6';                     // droga lokalna / alejka
}

export function getElevationColor(z: number): string {
  if (z < 0) return '#3b82f6';
  if (z < 40) return '#00f0ff';
  if (z < 100) return '#f59e0b';
  return '#ff2a6d';
}

export function getHeightColor(heightM: number): string {
  if (heightM < 15) return '#38bdf8';
  if (heightM < 45) return '#00f0ff';
  if (heightM < 110) return '#c084fc';
  return '#fcee09';
}

export interface DistrictPreset {
  id: string;
  label: string;
  x: number;
  y: number;
  zoom2d: number;
  distance3d: number;
}

export const DISTRICT_PRESETS: DistrictPreset[] = [
  { id: 'City Center', label: 'Centrum', x: -1650, y: 200, zoom2d: -0.7, distance3d: 1850 },
  { id: 'Watson', label: 'Watson', x: -1450, y: 1550, zoom2d: -1.1, distance3d: 2500 },
  { id: 'Westbrook', label: 'Westbrook', x: -300, y: 550, zoom2d: -1.1, distance3d: 2450 },
  { id: 'Heywood', label: 'Heywood', x: -1650, y: -900, zoom2d: -1.0, distance3d: 2250 },
  { id: 'Santo Domingo', label: 'Santo Domingo', x: -550, y: -1550, zoom2d: -1.3, distance3d: 2800 },
  { id: 'Pacifica', label: 'Pacifica', x: -2050, y: -2350, zoom2d: -1.2, distance3d: 2450 },
  { id: 'Dogtown', label: 'Dogtown', x: -1830, y: -2650, zoom2d: -0.5, distance3d: 1550 },
  { id: 'Badlands', label: 'Badlands', x: 1350, y: -600, zoom2d: -2.4, distance3d: 5800 },
];

export const INITIAL_CAMERA: { x: number; y: number; zoom2d: number } = {
  x: -1350,
  y: 250,
  zoom2d: -1.6,
};
