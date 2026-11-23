import { useState } from 'react';
import {
  ArrowDownLeft,
  ArrowUpRight,
  Building2,
  Check,
  Copy,
  Crosshair,
  MapPin,
  Radar,
  Route,
  X,
} from 'lucide-react';
import {
  BUILDING_TYPE_META,
  getBuildingTypeColor,
  getDistrictColor,
  getPoiMeta,
} from '../constants/palette';
import type { MapSpatialIndex } from '../data/spatialIndex';
import type { RoadFeature, SelectedFeature } from '../types/map';

interface InspectorPanelProps {
  selected: SelectedFeature | null;
  spatialIndex: MapSpatialIndex;
  onClose: () => void;
  onSelectRoad: (road: RoadFeature) => void;
  onCenterOnSelected: () => void;
}

export function InspectorPanel({
  selected,
  spatialIndex,
  onClose,
  onSelectRoad,
  onCenterOnSelected,
}: InspectorPanelProps) {
  const [copied, setCopied] = useState(false);

  if (!selected) return null;

  const getCoordsText = (): string => {
    if (selected.kind === 'building') {
      const [x, y, z] = selected.feature.properties.location;
      return `${x.toFixed(2)}, ${y.toFixed(2)}, ${z.toFixed(2)}`;
    }
    if (selected.kind === 'poi') {
      const [x, y, z = 0] = selected.feature.geometry.coordinates;
      return `${x.toFixed(2)}, ${y.toFixed(2)}, ${z.toFixed(2)}`;
    }
    if (selected.kind === 'road') {
      const c = selected.feature.geometry.coordinates[0];
      return `${c[0].toFixed(2)}, ${c[1].toFixed(2)}, ${c[2].toFixed(2)}`;
    }
    const [x, y] = selected.feature.properties.center;
    return `${x.toFixed(2)}, ${y.toFixed(2)}`;
  };

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(getCoordsText());
      setCopied(true);
      setTimeout(() => setCopied(false), 1400);
    } catch {
      // Clipboard blocked in non-HTTPS contexts
    }
  };

  const renderHeader = () => {
    if (selected.kind === 'building') {
      const b = selected.feature;
      const meta = BUILDING_TYPE_META[b._type] ?? BUILDING_TYPE_META.unknown;
      return {
        icon: <Building2 size={16} />,
        badge: meta.label,
        badgeColor: getBuildingTypeColor(b._type),
        title: b.properties.name || `Budynek #${b.properties.id}`,
        subtitle: [b.properties.district, b.properties.subdistrict].filter(Boolean).join(' · ') || 'Night City',
      };
    }
    if (selected.kind === 'poi') {
      const p = selected.feature;
      const meta = getPoiMeta(p.properties.category);
      return {
        icon: <MapPin size={16} />,
        badge: meta.label,
        badgeColor: meta.color,
        title: p.properties.name,
        subtitle: [p.properties.district, p.properties.subdistrict].filter(Boolean).join(' · ') || 'Night City',
      };
    }
    if (selected.kind === 'road') {
      const r = selected.feature;
      return {
        icon: <Route size={16} />,
        badge: `Pas ruchu #${r.properties.id}`,
        badgeColor: '#00f0ff',
        title: `Grupa #${r.properties.neighbor_group_index} · Pas ${r.properties.lane_number}`,
        subtitle: [r.properties.district, r.properties.subdistrict].filter(Boolean).join(' · ') || 'Sieć drogowa',
      };
    }
    const reg = selected.feature;
    return {
      icon: <Radar size={16} />,
      badge: reg.properties.is_subdistrict ? 'Poddzielnica' : 'Dzielnica',
      badgeColor: getDistrictColor(reg.properties.top_district),
      title: reg.properties.name,
      subtitle: reg.properties.top_district,
    };
  };

  const head = renderHeader();

  return (
    <aside className="inspector">
      <div className="inspector__head">
        <div className="inspector__badge" style={{ borderColor: head.badgeColor, color: head.badgeColor }}>
          {head.icon}
          <span>{head.badge}</span>
        </div>
        <button type="button" className="icon-btn" onClick={onClose} title="Zamknij (Esc)">
          <X size={15} />
        </button>
      </div>

      <h3 className="inspector__title">{head.title}</h3>
      <p className="inspector__sub">{head.subtitle}</p>

      <div className="inspector__actions">
        <button type="button" className="action-btn" onClick={onCenterOnSelected}>
          <Crosshair size={14} /> Centruj widok
        </button>
        <button type="button" className="action-btn" onClick={handleCopy}>
          {copied ? <Check size={14} /> : <Copy size={14} />}
          {copied ? 'Skopiowano!' : 'Kopiuj XYZ'}
        </button>
      </div>

      <div className="inspector__body">
        {selected.kind === 'building' && (
          <>
            <div className="kv-grid">
              <div className="kv-tile">
                <small>Wysokość</small>
                <strong>{selected.feature.properties.height_m.toFixed(1)} m</strong>
              </div>
              <div className="kv-tile">
                <small>Powierzchnia</small>
                <strong>{Math.round(selected.feature.properties.area_m2).toLocaleString('pl-PL')} m²</strong>
              </div>
              <div className="kv-tile">
                <small>Wymiary</small>
                <strong>
                  {selected.feature.properties.width_m.toFixed(1)} × {selected.feature.properties.length_m.toFixed(1)} m
                </strong>
              </div>
              <div className="kv-tile">
                <small>Zakres Z</small>
                <strong>
                  {selected.feature.properties.base_z.toFixed(1)} .. {selected.feature.properties.top_z.toFixed(1)} m
                </strong>
              </div>
            </div>
            <div className="kv-list">
              <div><span>ID obiektu</span><code>{selected.feature.properties.id}</code></div>
              <div><span>Rotacja</span><code>{selected.feature.properties.rotation_deg}°</code></div>
              <div><span>Źródło</span><code>{selected.feature.properties.source_type}</code></div>
              <div><span>Mesh</span><code className="break-all">{selected.feature.properties.mesh_path}</code></div>
            </div>
          </>
        )}

        {selected.kind === 'road' && (
          <>
            <div className="kv-grid">
              <div className="kv-tile">
                <small>Długość</small>
                <strong>{selected.feature.properties.length_m.toFixed(1)} m</strong>
              </div>
              <div className="kv-tile">
                <small>Prędkość maks.</small>
                <strong>{selected.feature.properties.max_speed}</strong>
              </div>
              <div className="kv-tile">
                <small>Szerokość</small>
                <strong>{selected.feature.properties.width_m} m</strong>
              </div>
              <div className="kv-tile">
                <small>Dwukierunkowa</small>
                <strong>{selected.feature.properties.road_bidirectional ? 'Tak' : 'Nie'}</strong>
              </div>
            </div>

            <div className="lane-links">
              <div className="lane-links__group">
                <span className="lane-links__title lane-links__title--in">
                  <ArrowDownLeft size={13} /> Pasy wchodzące ({selected.feature.properties.in_lanes.length})
                </span>
                <div className="lane-chips">
                  {selected.feature.properties.in_lanes.length === 0 ? (
                    <span className="muted">Brak</span>
                  ) : (
                    selected.feature.properties.in_lanes.map((id) => {
                      const lane = spatialIndex.roadById.get(id);
                      return (
                        <button
                          key={id}
                          type="button"
                          className="lane-chip lane-chip--in"
                          disabled={!lane}
                          onClick={() => lane && onSelectRoad(lane)}
                        >
                          #{id}
                        </button>
                      );
                    })
                  )}
                </div>
              </div>

              <div className="lane-links__group">
                <span className="lane-links__title lane-links__title--out">
                  <ArrowUpRight size={13} /> Pasy wychodzące ({selected.feature.properties.out_lanes.length})
                </span>
                <div className="lane-chips">
                  {selected.feature.properties.out_lanes.length === 0 ? (
                    <span className="muted">Brak</span>
                  ) : (
                    selected.feature.properties.out_lanes.map((id) => {
                      const lane = spatialIndex.roadById.get(id);
                      return (
                        <button
                          key={id}
                          type="button"
                          className="lane-chip lane-chip--out"
                          disabled={!lane}
                          onClick={() => lane && onSelectRoad(lane)}
                        >
                          #{id}
                        </button>
                      );
                    })
                  )}
                </div>
              </div>
            </div>
          </>
        )}

        {selected.kind === 'poi' && (
          <div className="kv-list">
            <div><span>Kategoria</span><code>{selected.feature.properties.category}</code></div>
            <div><span>Wariant</span><code>{selected.feature.properties.variant ?? '—'}</code></div>
            <div><span>Współrzędne</span><code>{getCoordsText()}</code></div>
            <div><span>Źródło</span><code>{selected.feature.properties.source}</code></div>
            <div><span>Ref ID</span><code className="break-all">{selected.feature.properties.ref_id}</code></div>
          </div>
        )}

        {selected.kind === 'region' && (
          <>
            <div className="kv-grid">
              <div className="kv-tile">
                <small>Powierzchnia</small>
                <strong>
                  {(selected.feature.properties.area_m2 / 1_000_000).toFixed(2)} km²
                </strong>
              </div>
              <div className="kv-tile">
                <small>Zakres Z</small>
                <strong>
                  {selected.feature.properties.z_min.toFixed(0)} .. {selected.feature.properties.z_max.toFixed(0)} m
                </strong>
              </div>
            </div>
            <div className="kv-list">
              <div><span>District ID</span><code>{selected.feature.properties.district_id}</code></div>
              <div><span>Nadrzędna</span><code>{selected.feature.properties.parent_district ?? '—'}</code></div>
              <div><span>Questowy</span><code>{selected.feature.properties.is_quest_district ? 'Tak' : 'Nie'}</code></div>
              <div><span>Sektor</span><code className="break-all">{selected.feature.properties.source_sector}</code></div>
            </div>
          </>
        )}

        <details className="raw-json">
          <summary>Surowy JSON</summary>
          <pre>{JSON.stringify(selected.feature.properties, null, 2)}</pre>
        </details>
      </div>
    </aside>
  );
}
