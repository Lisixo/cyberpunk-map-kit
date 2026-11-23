import { useMemo, useRef, useState, useEffect } from 'react';
import {
  Box,
  Compass,
  Layers,
  Map as MapIcon,
  Maximize2,
  PanelLeftClose,
  PanelLeftOpen,
  Search,
  X,
} from 'lucide-react';
import { DISTRICT_PRESETS, getPoiMeta } from '../constants/palette';
import type { MapSpatialIndex } from '../data/spatialIndex';
import type { SearchHit, ViewMode } from '../types/map';

interface TopBarProps {
  viewMode: ViewMode;
  onChangeViewMode: (mode: ViewMode) => void;
  sidebarOpen: boolean;
  onToggleSidebar: () => void;
  searchQuery: string;
  onChangeSearchQuery: (q: string) => void;
  spatialIndex: MapSpatialIndex | null;
  onSelectSearchHit: (hit: SearchHit) => void;
  onJumpDistrict: (presetId: string) => void;
  onFitAll: () => void;
  searchInputRef: React.RefObject<HTMLInputElement | null>;
}

const KIND_LABELS: Record<SearchHit['kind'], string> = {
  region: 'Regiony i dzielnice',
  poi: 'Punkty POI',
  building: 'Budynki',
  road: 'Drogi',
};

export function TopBar({
  viewMode,
  onChangeViewMode,
  sidebarOpen,
  onToggleSidebar,
  searchQuery,
  onChangeSearchQuery,
  spatialIndex,
  onSelectSearchHit,
  onJumpDistrict,
  onFitAll,
  searchInputRef,
}: TopBarProps) {
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const searchBoxRef = useRef<HTMLDivElement | null>(null);

  const hits = useMemo(() => {
    if (!spatialIndex || searchQuery.trim().length < 2) return [];
    return spatialIndex.search(searchQuery, 30);
  }, [spatialIndex, searchQuery]);

  const groupedHits = useMemo(() => {
    const groups: Partial<Record<SearchHit['kind'], SearchHit[]>> = {};
    for (const h of hits) {
      if (!groups[h.kind]) groups[h.kind] = [];
      groups[h.kind]!.push(h);
    }
    return groups;
  }, [hits]);

  useEffect(() => {
    const onDocDown = (e: MouseEvent) => {
      if (searchBoxRef.current && !searchBoxRef.current.contains(e.target as Node)) {
        setDropdownOpen(false);
      }
    };
    document.addEventListener('mousedown', onDocDown);
    return () => document.removeEventListener('mousedown', onDocDown);
  }, []);

  return (
    <header className="topbar">
      <div className="topbar__left">
        <button
          type="button"
          className="icon-btn"
          onClick={onToggleSidebar}
          title={sidebarOpen ? 'Ukryj panel boczny' : 'Pokaż panel boczny'}
        >
          {sidebarOpen ? <PanelLeftClose size={17} /> : <PanelLeftOpen size={17} />}
        </button>

        <div className="brand">
          <span className="brand__tag">NC // 2077</span>
          <span className="brand__title">NIGHT CITY HOLOMAP</span>
        </div>
      </div>

      <div className="topbar__center">
        <div className="mode-switch" role="group" aria-label="Tryb widoku">
          <button
            type="button"
            className={`mode-switch__btn ${viewMode === '3d' ? 'is-active' : ''}`}
            onClick={() => onChangeViewMode('3d')}
            title="Widok 3D Three.js (klawisz 3)"
          >
            <Box size={15} />
            <span>3D THREE.JS</span>
            <kbd>3</kbd>
          </button>
          <button
            type="button"
            className={`mode-switch__btn ${viewMode === '2d' ? 'is-active' : ''}`}
            onClick={() => onChangeViewMode('2d')}
            title="Widok taktyczny 2D Leaflet (klawisz 2)"
          >
            <MapIcon size={15} />
            <span>2D LEAFLET</span>
            <kbd>2</kbd>
          </button>
        </div>
      </div>

      <div className="topbar__right">
        <div className="search-box" ref={searchBoxRef}>
          <Search size={15} className="search-box__icon" />
          <input
            ref={searchInputRef}
            type="text"
            className="search-box__input"
            placeholder="Szukaj dzielnicy, POI, budynku... (Ctrl+K)"
            value={searchQuery}
            onFocus={() => setDropdownOpen(true)}
            onChange={(e) => {
              onChangeSearchQuery(e.target.value);
              setDropdownOpen(true);
            }}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && hits.length > 0) {
                onSelectSearchHit(hits[0]);
                setDropdownOpen(false);
              } else if (e.key === 'Escape') {
                setDropdownOpen(false);
                searchInputRef.current?.blur();
              }
            }}
          />
          {searchQuery && (
            <button
              type="button"
              className="search-box__clear"
              onClick={() => {
                onChangeSearchQuery('');
                searchInputRef.current?.focus();
              }}
              title="Wyczyść"
            >
              <X size={14} />
            </button>
          )}

          {dropdownOpen && searchQuery.trim().length >= 2 && (
            <div className="search-dropdown">
              {hits.length === 0 ? (
                <div className="search-dropdown__empty">Brak wyników dla „{searchQuery}”</div>
              ) : (
                (['region', 'poi', 'building'] as const).map((kind) => {
                  const list = groupedHits[kind];
                  if (!list || list.length === 0) return null;
                  return (
                    <div key={kind} className="search-dropdown__group">
                      <div className="search-dropdown__group-title">
                        <Layers size={12} /> {KIND_LABELS[kind]} ({list.length})
                      </div>
                      {list.map((hit, idx) => {
                        const dotColor =
                          hit.kind === 'poi'
                            ? getPoiMeta((hit.feature.properties as { category: string }).category).color
                            : hit.kind === 'region'
                              ? '#38bdf8'
                              : '#c084fc';
                        return (
                          <button
                            key={`${kind}-${idx}`}
                            type="button"
                            className="search-dropdown__item"
                            onClick={() => {
                              onSelectSearchHit(hit);
                              setDropdownOpen(false);
                            }}
                          >
                            <span className="dot" style={{ backgroundColor: dotColor }} />
                            <span className="search-dropdown__text">
                              <strong>{hit.label}</strong>
                              <small>{hit.subtitle}</small>
                            </span>
                          </button>
                        );
                      })}
                    </div>
                  );
                })
              )}
            </div>
          )}
        </div>

        <div className="district-jump">
          <Compass size={14} />
          <select
            aria-label="Skocz do dzielnicy"
            defaultValue=""
            onChange={(e) => {
              if (e.target.value) {
                onJumpDistrict(e.target.value);
                e.target.value = '';
              }
            }}
          >
            <option value="" disabled>
              Skocz do...
            </option>
            {DISTRICT_PRESETS.map((p) => (
              <option key={p.id} value={p.id}>
                {p.label}
              </option>
            ))}
          </select>
        </div>

        <button
          type="button"
          className="icon-btn"
          onClick={onFitAll}
          title="Dopasuj widok do całej mapy"
        >
          <Maximize2 size={16} />
        </button>
      </div>
    </header>
  );
}
