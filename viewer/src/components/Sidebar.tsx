import { useMemo, useState } from 'react';
import {
  ChevronDown,
  ChevronRight,
  Database,
  Eye,
  Filter,
  FolderOpen,
  Navigation,
  Sliders,
} from 'lucide-react';
import {
  BUILDING_TYPE_META,
  BUILDING_TYPE_ORDER,
  DISTRICT_COLORS,
  POI_CATEGORY_META,
  POI_GROUPS,
  type PoiGroupId,
} from '../constants/palette';
import type {
  BuildingColorMode,
  FilterState,
  LayerVisibility,
  MapDataset,
  RoadColorMode,
  ThreeSettings,
  ViewMode,
} from '../types/map';

interface SidebarProps {
  open: boolean;
  dataset: MapDataset;
  viewMode: ViewMode;
  visibility: LayerVisibility;
  onChangeVisibility: (next: LayerVisibility) => void;
  filters: FilterState;
  onChangeFilters: (next: FilterState) => void;
  threeSettings: ThreeSettings;
  onChangeThreeSettings: (next: ThreeSettings) => void;
  onJumpDistrict: (districtId: string) => void;
  onUploadFiles: (files: FileList) => void;
}

export function Sidebar({
  open,
  dataset,
  viewMode,
  visibility,
  onChangeVisibility,
  filters,
  onChangeFilters,
  threeSettings,
  onChangeThreeSettings,
  onJumpDistrict,
  onUploadFiles,
}: SidebarProps) {
  const [expandedPoiGroups, setExpandedPoiGroups] = useState<Record<PoiGroupId, boolean>>({
    transport: true,
    contracts: true,
    services: false,
    other: false,
  });

  const statsSummary = useMemo(() => {
    const bTypes: Record<string, number> = {};
    for (const b of dataset.buildings) {
      bTypes[b._type] = (bTypes[b._type] ?? 0) + 1;
    }
    const poiCats: Record<string, number> = {};
    for (const p of dataset.poi) {
      poiCats[p.properties.category] = (poiCats[p.properties.category] ?? 0) + 1;
    }
    const districts = new Set<string>();
    for (const reg of dataset.regions) {
      if (reg.properties.top_district && !reg.properties.is_quest_district) {
        districts.add(reg.properties.top_district);
      }
    }
    for (const b of dataset.buildings) {
      if (b.properties.district) districts.add(b.properties.district);
    }
    return {
      bTypes,
      poiCats,
      districts: Array.from(districts).sort((a, b) => a.localeCompare(b, 'pl')),
    };
  }, [dataset]);

  const poiCategoriesByGroup = useMemo(() => {
    const grouped: Record<PoiGroupId, string[]> = {
      transport: [],
      contracts: [],
      services: [],
      other: [],
    };
    const allCats = new Set([
      ...Object.keys(POI_CATEGORY_META),
      ...Object.keys(statsSummary.poiCats),
    ]);
    for (const cat of allCats) {
      if ((statsSummary.poiCats[cat] ?? 0) === 0 && !POI_CATEGORY_META[cat]) continue;
      const group = POI_CATEGORY_META[cat]?.group ?? 'other';
      grouped[group].push(cat);
    }
    for (const g of Object.keys(grouped) as PoiGroupId[]) {
      grouped[g].sort(
        (a, b) => (statsSummary.poiCats[b] ?? 0) - (statsSummary.poiCats[a] ?? 0),
      );
    }
    return grouped;
  }, [statsSummary.poiCats]);

  if (!open) return null;

  const setAllBuildingTypes = (val: boolean) => {
    const next: Record<string, boolean> = {};
    for (const t of BUILDING_TYPE_ORDER) next[t] = val;
    onChangeFilters({ ...filters, buildingTypes: next });
  };

  const setAllPoiCategories = (val: boolean) => {
    const next: Record<string, boolean> = { ...filters.poiCategories };
    for (const cat of Object.keys(POI_CATEGORY_META)) next[cat] = val;
    for (const cat of Object.keys(statsSummary.poiCats)) next[cat] = val;
    onChangeFilters({ ...filters, poiCategories: next });
  };

  return (
    <aside className="sidebar">
      {/* Layers */}
      <section className="panel-section">
        <div className="panel-section__head">
          <Eye size={14} />
          <h2>Warstwy mapy</h2>
        </div>

        <label className="layer-row">
          <span className="layer-row__label">
            <input
              type="checkbox"
              checked={visibility.regions}
              onChange={(e) =>
                onChangeVisibility({ ...visibility, regions: e.target.checked })
              }
            />
            <span className="dot" style={{ backgroundColor: '#38bdf8' }} />
            Regiony i dzielnice
          </span>
          <span className="count-pill">{dataset.regions.length.toLocaleString('pl-PL')}</span>
        </label>

        {visibility.regions && (
          <div className="sub-toggles">
            <label className="mini-check">
              <input
                type="checkbox"
                checked={visibility.regionSubdistricts}
                onChange={(e) =>
                  onChangeVisibility({ ...visibility, regionSubdistricts: e.target.checked })
                }
              />
              Poddzielnice
            </label>
            <label className="mini-check">
              <input
                type="checkbox"
                checked={visibility.regionQuest}
                onChange={(e) =>
                  onChangeVisibility({ ...visibility, regionQuest: e.target.checked })
                }
              />
              Questowe
            </label>
            <label className="mini-check">
              <input
                type="checkbox"
                checked={visibility.regionLabels}
                onChange={(e) =>
                  onChangeVisibility({ ...visibility, regionLabels: e.target.checked })
                }
              />
              Etykiety
            </label>
          </div>
        )}

        <label className="layer-row">
          <span className="layer-row__label">
            <input
              type="checkbox"
              checked={visibility.roads}
              onChange={(e) =>
                onChangeVisibility({ ...visibility, roads: e.target.checked })
              }
            />
            <span className="dot" style={{ backgroundColor: '#f59e0b' }} />
            Sieć drogowa (pasy)
          </span>
          <span className="count-pill">{dataset.roads.length.toLocaleString('pl-PL')}</span>
        </label>

        <label className="layer-row">
          <span className="layer-row__label">
            <input
              type="checkbox"
              checked={visibility.buildings}
              onChange={(e) =>
                onChangeVisibility({ ...visibility, buildings: e.target.checked })
              }
            />
            <span className="dot" style={{ backgroundColor: '#c084fc' }} />
            Budynki
          </span>
          <span className="count-pill">{dataset.buildings.length.toLocaleString('pl-PL')}</span>
        </label>

        <label className="layer-row">
          <span className="layer-row__label">
            <input
              type="checkbox"
              checked={visibility.poi}
              onChange={(e) =>
                onChangeVisibility({ ...visibility, poi: e.target.checked })
              }
            />
            <span className="dot" style={{ backgroundColor: '#00f0ff' }} />
            Punkty POI
          </span>
          <span className="count-pill">{dataset.poi.length.toLocaleString('pl-PL')}</span>
        </label>
      </section>

      {/* District Filter */}
      <section className="panel-section">
        <div className="panel-section__head">
          <Filter size={14} />
          <h2>Filtr dzielnicy</h2>
        </div>

        <div className="district-filter-row">
          <select
            value={filters.district}
            onChange={(e) => onChangeFilters({ ...filters, district: e.target.value })}
          >
            <option value="ALL">Wszystkie dzielnice</option>
            {statsSummary.districts.map((d) => (
              <option key={d} value={d}>
                {d}
              </option>
            ))}
          </select>
          {filters.district !== 'ALL' && (
            <button
              type="button"
              className="mini-btn"
              onClick={() => onJumpDistrict(filters.district)}
              title="Centruj kamerę na wybranej dzielnicy"
            >
              <Navigation size={13} />
            </button>
          )}
        </div>
      </section>

      {/* Building styling & type filters */}
      <section className="panel-section">
        <div className="panel-section__head">
          <Sliders size={14} />
          <h2>Budynki — kolor i typy</h2>
          <div className="head-actions">
            <button type="button" className="text-btn" onClick={() => setAllBuildingTypes(true)}>
              Wszystkie
            </button>
            <button type="button" className="text-btn" onClick={() => setAllBuildingTypes(false)}>
              Żadne
            </button>
          </div>
        </div>

        <div className="segmented">
          {(
            [
              ['type', 'Typ'],
              ['height', 'Wysokość'],
              ['district', 'Dzielnica'],
            ] as [BuildingColorMode, string][]
          ).map(([mode, label]) => (
            <button
              key={mode}
              type="button"
              className={filters.buildingColorMode === mode ? 'is-active' : ''}
              onClick={() => onChangeFilters({ ...filters, buildingColorMode: mode })}
            >
              {label}
            </button>
          ))}
        </div>

        <div className="chip-grid">
          {BUILDING_TYPE_ORDER.map((typeKey) => {
            const meta = BUILDING_TYPE_META[typeKey];
            const active = filters.buildingTypes[typeKey] !== false;
            const cnt = statsSummary.bTypes[typeKey] ?? 0;
            return (
              <button
                key={typeKey}
                type="button"
                className={`filter-chip ${active ? 'is-on' : ''}`}
                onClick={() =>
                  onChangeFilters({
                    ...filters,
                    buildingTypes: {
                      ...filters.buildingTypes,
                      [typeKey]: !active,
                    },
                  })
                }
              >
                <span className="dot" style={{ backgroundColor: meta.color }} />
                <span className="filter-chip__name">{meta.label}</span>
                <span className="filter-chip__cnt">{cnt.toLocaleString('pl-PL')}</span>
              </button>
            );
          })}
        </div>
      </section>

      {/* Road styling */}
      <section className="panel-section">
        <div className="panel-section__head">
          <Sliders size={14} />
          <h2>Drogi — kolorowanie</h2>
        </div>

        <div className="segmented">
          {(
            [
              ['speed', 'Prędkość'],
              ['elevation', 'Wysokość Z'],
              ['district', 'Dzielnica'],
            ] as [RoadColorMode, string][]
          ).map(([mode, label]) => (
            <button
              key={mode}
              type="button"
              className={filters.roadColorMode === mode ? 'is-active' : ''}
              onClick={() => onChangeFilters({ ...filters, roadColorMode: mode })}
            >
              {label}
            </button>
          ))}
        </div>

        {filters.roadColorMode === 'speed' && (
          <div className="legend-row">
            <span><i className="dot" style={{ backgroundColor: '#fcee09' }} /> ≥15 Autostrada</span>
            <span><i className="dot" style={{ backgroundColor: '#00f0ff' }} /> ≥10 Arteria</span>
            <span><i className="dot" style={{ backgroundColor: '#f59e0b' }} /> ≥4 Ulica</span>
            <span><i className="dot" style={{ backgroundColor: '#3b82f6' }} /> &lt;4 Lokalna</span>
          </div>
        )}
        {filters.roadColorMode === 'elevation' && (
          <div className="legend-row">
            <span><i className="dot" style={{ backgroundColor: '#3b82f6' }} /> &lt;0m</span>
            <span><i className="dot" style={{ backgroundColor: '#00f0ff' }} /> 0–40m</span>
            <span><i className="dot" style={{ backgroundColor: '#f59e0b' }} /> 40–100m</span>
            <span><i className="dot" style={{ backgroundColor: '#ff2a6d' }} /> &gt;100m</span>
          </div>
        )}
        {filters.roadColorMode === 'district' && (
          <div className="legend-row">
            {Object.entries(DISTRICT_COLORS).slice(0, 6).map(([d, col]) => (
              <span key={d}><i className="dot" style={{ backgroundColor: col }} /> {d}</span>
            ))}
          </div>
        )}
      </section>

      {/* POI categories */}
      <section className="panel-section">
        <div className="panel-section__head">
          <Filter size={14} />
          <h2>Kategorie POI</h2>
          <div className="head-actions">
            <button type="button" className="text-btn" onClick={() => setAllPoiCategories(true)}>
              Wszystkie
            </button>
            <button type="button" className="text-btn" onClick={() => setAllPoiCategories(false)}>
              Żadne
            </button>
          </div>
        </div>

        <div className="poi-groups">
          {(Object.keys(POI_GROUPS) as PoiGroupId[]).map((groupId) => {
            const cats = poiCategoriesByGroup[groupId];
            if (cats.length === 0) return null;
            const isOpen = expandedPoiGroups[groupId];
            return (
              <div key={groupId} className="poi-group">
                <button
                  type="button"
                  className="poi-group__head"
                  onClick={() =>
                    setExpandedPoiGroups({ ...expandedPoiGroups, [groupId]: !isOpen })
                  }
                >
                  {isOpen ? <ChevronDown size={13} /> : <ChevronRight size={13} />}
                  <span>{POI_GROUPS[groupId]}</span>
                  <small>{cats.reduce((acc, c) => acc + (statsSummary.poiCats[c] ?? 0), 0)}</small>
                </button>
                {isOpen && (
                  <div className="poi-group__list">
                    {cats.map((cat) => {
                      const meta = POI_CATEGORY_META[cat] ?? { label: cat, color: '#94a3b8' };
                      const active = filters.poiCategories[cat] !== false;
                      const cnt = statsSummary.poiCats[cat] ?? 0;
                      return (
                        <label key={cat} className="poi-cat-row">
                          <span>
                            <input
                              type="checkbox"
                              checked={active}
                              onChange={(e) =>
                                onChangeFilters({
                                  ...filters,
                                  poiCategories: {
                                    ...filters.poiCategories,
                                    [cat]: e.target.checked,
                                  },
                                })
                              }
                            />
                            <span className="dot" style={{ backgroundColor: meta.color }} />
                            {meta.label}
                          </span>
                          <span className="count-pill">{cnt}</span>
                        </label>
                      );
                    })}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </section>

      {/* 3D Settings */}
      {viewMode === '3d' && (
        <section className="panel-section">
          <div className="panel-section__head">
            <Sliders size={14} />
            <h2>Ustawienia 3D (Three.js)</h2>
          </div>

          <div className="segmented">
            <button
              type="button"
              className={threeSettings.buildingStyle === 'solid' ? 'is-active' : ''}
              onClick={() => onChangeThreeSettings({ ...threeSettings, buildingStyle: 'solid' })}
            >
              Bryły (Solid)
            </button>
            <button
              type="button"
              className={threeSettings.buildingStyle === 'hologram' ? 'is-active' : ''}
              onClick={() =>
                onChangeThreeSettings({ ...threeSettings, buildingStyle: 'hologram' })
              }
            >
              Hologram
            </button>
          </div>

          <label className="slider-row">
            <span>Skala wysokości Z: <strong>{threeSettings.zScale.toFixed(1)}×</strong></span>
            <input
              type="range"
              min={0.5}
              max={3.0}
              step={0.1}
              value={threeSettings.zScale}
              onChange={(e) =>
                onChangeThreeSettings({
                  ...threeSettings,
                  zScale: Number(e.target.value),
                })
              }
            />
          </label>

          <div className="sub-toggles">
            <label className="mini-check">
              <input
                type="checkbox"
                checked={threeSettings.showGrid}
                onChange={(e) =>
                  onChangeThreeSettings({ ...threeSettings, showGrid: e.target.checked })
                }
              />
              Siatka
            </label>
            <label className="mini-check">
              <input
                type="checkbox"
                checked={threeSettings.showFog}
                onChange={(e) =>
                  onChangeThreeSettings({ ...threeSettings, showFog: e.target.checked })
                }
              />
              Mgła
            </label>
            <label className="mini-check">
              <input
                type="checkbox"
                checked={threeSettings.autoOrbit}
                onChange={(e) =>
                  onChangeThreeSettings({ ...threeSettings, autoOrbit: e.target.checked })
                }
              />
              Auto-orbita
            </label>
          </div>
        </section>
      )}

      {/* Data Source */}
      <section className="panel-section">
        <div className="panel-section__head">
          <Database size={14} />
          <h2>Dane źródłowe</h2>
        </div>

        {dataset.stats && (
          <div className="stats-meta">
            <div><span>Zakres:</span> <strong>{dataset.stats.scope}</strong></div>
            <div><span>Język:</span> <strong>{dataset.stats.language}</strong></div>
            <div><span>Wygenerowano:</span> <strong>{new Date(dataset.stats.generated_at).toLocaleDateString('pl-PL')}</strong></div>
          </div>
        )}

        <label className="upload-btn">
          <FolderOpen size={14} />
          <span>Wczytaj własne pliki .geojson</span>
          <input
            type="file"
            multiple
            accept=".geojson,.json"
            style={{ display: 'none' }}
            onChange={(e) => {
              if (e.target.files && e.target.files.length > 0) {
                onUploadFiles(e.target.files);
                e.target.value = '';
              }
            }}
          />
        </label>
      </section>
    </aside>
  );
}
