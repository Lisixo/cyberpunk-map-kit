import {
  forwardRef,
  useEffect,
  useImperativeHandle,
  useRef,
  useState,
} from 'react';
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import { Minus, Plus, Crosshair } from 'lucide-react';
import {
  getBuildingTypeColor,
  getDistrictColor,
  getElevationColor,
  getHeightColor,
  getPoiMeta,
  getRoadSpeedColor,
} from '../constants/palette';
import {
  MapSpatialIndex,
  matchesBuildingFilter,
  matchesPoiFilter,
  matchesRegionFilter,
  matchesRoadFilter,
  matchesSearchQuery,
} from '../data/spatialIndex';
import type {
  CameraSyncState,
  CursorTelemetry,
  FilterState,
  FlyTarget,
  LayerVisibility,
  MapViewportRef,
  SelectedFeature,
} from '../types/map';

interface LeafletMapViewProps {
  spatialIndex: MapSpatialIndex;
  visibility: LayerVisibility;
  filters: FilterState;
  selected: SelectedFeature | null;
  onSelect: (sel: SelectedFeature | null) => void;
  cameraSyncRef: React.MutableRefObject<CameraSyncState>;
  onTelemetry: (telemetry: CursorTelemetry) => void;
  pendingFlyTarget: FlyTarget | null;
}

function chooseGridStep(pxPerMeter: number): number {
  const targetPx = 110;
  const rawMeters = targetPx / Math.max(0.001, pxPerMeter);
  const steps = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000];
  for (const s of steps) {
    if (s >= rawMeters) return s;
  }
  return 5000;
}

function formatScaleLabel(meters: number): string {
  return meters >= 1000 ? `${(meters / 1000).toFixed(meters % 1000 === 0 ? 0 : 1)} km` : `${meters} m`;
}

export const LeafletMapView = forwardRef<MapViewportRef, LeafletMapViewProps>(function LeafletMapView(
  {
    spatialIndex,
    visibility,
    filters,
    selected,
    onSelect,
    cameraSyncRef,
    onTelemetry,
    pendingFlyTarget,
  },
  ref,
) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const mapRef = useRef<L.Map | null>(null);
  const selectionLayerRef = useRef<L.LayerGroup | null>(null);
  const scheduleDrawRef = useRef<() => void>(() => {});
  const lastFlyNonceRef = useRef<number>(pendingFlyTarget?.nonce ?? 0);

  const [scaleBar, setScaleBar] = useState<{ widthPx: number; label: string }>({
    widthPx: 90,
    label: '500 m',
  });

  const propsRef = useRef({
    spatialIndex,
    visibility,
    filters,
    selected,
    onSelect,
    onTelemetry,
  });
  propsRef.current = {
    spatialIndex,
    visibility,
    filters,
    selected,
    onSelect,
    onTelemetry,
  };

  useImperativeHandle(ref, () => ({
    flyTo(target) {
      const map = mapRef.current;
      if (!map) return;
      const z = target.zoom2d ?? Math.max(map.getZoom(), 0.5);
      map.setView(L.latLng(target.y, target.x), z, { animate: true });
    },
    fitAll() {
      const map = mapRef.current;
      if (!map) return;
      const [minX, minY, maxX, maxY] = spatialIndex.worldBBox;
      map.fitBounds(
        L.latLngBounds(L.latLng(minY, minX), L.latLng(maxY, maxX)),
        { padding: [32, 32], animate: true },
      );
    },
  }));

  useEffect(() => {
    const host = hostRef.current;
    const canvas = canvasRef.current;
    if (!host || !canvas) return;

    const init = cameraSyncRef.current;
    const map = L.map(host, {
      crs: L.CRS.Simple,
      renderer: L.svg({ padding: 0.5 }),
      center: L.latLng(init.y, init.x),
      zoom: init.zoom2d,
      minZoom: -5,
      maxZoom: 4,
      zoomSnap: 0.25,
      zoomDelta: 0.5,
      wheelPxPerZoomLevel: 85,
      zoomAnimation: false,
      fadeAnimation: false,
      attributionControl: false,
      zoomControl: false,
    });
    mapRef.current = map;

    const selectionLayer = L.layerGroup().addTo(map);
    selectionLayerRef.current = selectionLayer;

    let rafId = 0;
    const draw = () => {
      rafId = 0;
      const ctx = canvas.getContext('2d');
      if (!ctx) return;

      const w = host.clientWidth;
      const h = host.clientHeight;
      if (w <= 0 || h <= 0) return;

      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      const targetW = Math.round(w * dpr);
      const targetH = Math.round(h * dpr);
      if (canvas.width !== targetW || canvas.height !== targetH) {
        canvas.width = targetW;
        canvas.height = targetH;
      }
      ctx.setTransform(targetW / w, 0, 0, targetH / h, 0, 0);
      ctx.clearRect(0, 0, w, h);

      const zoom = map.getZoom();
      const pxPerMeter = map.options.crs?.scale(zoom) ?? Math.pow(2, zoom);
      if (!Number.isFinite(pxPerMeter) || pxPerMeter <= 0) return;

      const topLeft = map.containerPointToLatLng(L.point(0, 0));
      const bottomRight = map.containerPointToLatLng(L.point(w, h));
      const originX = topLeft.lng;
      const originY = topLeft.lat;
      const wMinX = Math.min(topLeft.lng, bottomRight.lng);
      const wMaxX = Math.max(topLeft.lng, bottomRight.lng);
      const wMinY = Math.min(topLeft.lat, bottomRight.lat);
      const wMaxY = Math.max(topLeft.lat, bottomRight.lat);

      const { spatialIndex: si, visibility: vis, filters: flt, selected: sel } = propsRef.current;
      const q = flt.searchQuery.trim();

      // Update scale bar UI
      const gridStep = chooseGridStep(pxPerMeter);
      const barPx = Math.round(gridStep * pxPerMeter);
      setScaleBar((prev) =>
        prev.widthPx === barPx && prev.label === formatScaleLabel(gridStep)
          ? prev
          : { widthPx: barPx, label: formatScaleLabel(gridStep) },
      );

      // 1. Tactical Meter Grid
      ctx.save();
      ctx.lineWidth = 1;
      ctx.strokeStyle = 'rgba(0, 240, 255, 0.065)';
      const startX = Math.floor(wMinX / gridStep) * gridStep;
      const startY = Math.floor(wMinY / gridStep) * gridStep;

      ctx.beginPath();
      for (let gx = startX; gx <= wMaxX; gx += gridStep) {
        const sx = (gx - originX) * pxPerMeter;
        ctx.moveTo(sx, 0);
        ctx.lineTo(sx, h);
      }
      for (let gy = startY; gy <= wMaxY; gy += gridStep) {
        const sy = (originY - gy) * pxPerMeter;
        ctx.moveTo(0, sy);
        ctx.lineTo(w, sy);
      }
      ctx.stroke();

      ctx.fillStyle = 'rgba(139, 155, 180, 0.42)';
      ctx.font = '10px ui-monospace, Consolas, monospace';
      ctx.textAlign = 'left';
      for (let gx = startX; gx <= wMaxX; gx += gridStep) {
        const sx = (gx - originX) * pxPerMeter;
        if (sx > 25 && sx < w - 45) {
          ctx.fillText(`${Math.round(gx)}m`, sx + 4, h - 42);
        }
      }
      ctx.restore();

      const viewPad = 12 / pxPerMeter;
      const isVisibleBBox = (b: [number, number, number, number]) =>
        b[2] >= wMinX - viewPad &&
        b[0] <= wMaxX + viewPad &&
        b[3] >= wMinY - viewPad &&
        b[1] <= wMaxY + viewPad;

      // 2. Regions
      if (vis.regions) {
        const regions = si.dataset.regions;
        for (let i = 0; i < regions.length; i++) {
          const reg = regions[i];
          if (!matchesRegionFilter(reg, vis, flt)) continue;
          if (!isVisibleBBox(reg._bbox)) continue;

          const ring = reg.geometry.coordinates[0];
          const isMatch =
            q.length > 0 &&
            matchesSearchQuery(
              [reg.properties.name, reg.properties.top_district, reg.properties.district_id],
              q,
            );
          const isSel = sel?.kind === 'region' && sel.feature === reg;
          const color = getDistrictColor(reg.properties.top_district);

          ctx.beginPath();
          for (let j = 0; j < ring.length; j++) {
            const sx = (ring[j][0] - originX) * pxPerMeter;
            const sy = (originY - ring[j][1]) * pxPerMeter;
            if (j === 0) ctx.moveTo(sx, sy);
            else ctx.lineTo(sx, sy);
          }
          ctx.closePath();

          if (!reg.properties.is_subdistrict || isMatch || isSel) {
            ctx.fillStyle = color;
            ctx.globalAlpha = isMatch || isSel ? 0.17 : 0.05;
            ctx.fill();
          }

          ctx.globalAlpha = isMatch || isSel ? 0.95 : reg.properties.is_subdistrict ? 0.35 : 0.65;
          ctx.strokeStyle = isMatch || isSel ? '#fcee09' : color;
          ctx.lineWidth = isMatch || isSel ? 2 : reg.properties.is_subdistrict ? 1 : 1.5;
          ctx.stroke();
        }
        ctx.globalAlpha = 1;
      }

      // 3. Roads
      if (vis.roads) {
        const roads = si.dataset.roads;
        const baseWidth = Math.max(0.75, Math.min(4.2, pxPerMeter * 1.45));
        ctx.lineCap = 'round';
        ctx.lineJoin = 'round';

        for (let i = 0; i < roads.length; i++) {
          const r = roads[i];
          if (!matchesRoadFilter(r, flt)) continue;
          if (!isVisibleBBox(r._bbox)) continue;

          const pts = r.geometry.coordinates;
          if (flt.roadColorMode === 'elevation') {
            ctx.strokeStyle = getElevationColor(pts[0]?.[2] ?? 0);
          } else if (flt.roadColorMode === 'district') {
            ctx.strokeStyle = getDistrictColor(r.properties.district);
          } else {
            ctx.strokeStyle = getRoadSpeedColor(r.properties.max_speed);
          }

          ctx.globalAlpha = zoom < -1.5 ? 0.52 : 0.78;
          ctx.lineWidth = r.properties.max_speed >= 15 ? baseWidth * 1.25 : baseWidth;

          ctx.beginPath();
          for (let j = 0; j < pts.length; j++) {
            const sx = (pts[j][0] - originX) * pxPerMeter;
            const sy = (originY - pts[j][1]) * pxPerMeter;
            if (j === 0) ctx.moveTo(sx, sy);
            else ctx.lineTo(sx, sy);
          }
          ctx.stroke();

          // One-way directional chevrons when zoomed in
          if (zoom >= 1.25 && pts.length >= 2) {
            const mid = Math.floor((pts.length - 1) / 2);
            const ax = (pts[mid][0] - originX) * pxPerMeter;
            const ay = (originY - pts[mid][1]) * pxPerMeter;
            const bx = (pts[mid + 1][0] - originX) * pxPerMeter;
            const by = (originY - pts[mid + 1][1]) * pxPerMeter;
            const len = Math.hypot(bx - ax, by - ay);
            if (len > 14) {
              const mx = (ax + bx) * 0.5;
              const my = (ay + by) * 0.5;
              const ux = (bx - ax) / len;
              const uy = (by - ay) / len;
              const sz = 4.2;
              ctx.beginPath();
              ctx.moveTo(mx + ux * sz, my + uy * sz);
              ctx.lineTo(mx - ux * sz * 0.7 - uy * sz * 0.65, my - uy * sz * 0.7 + ux * sz * 0.65);
              ctx.moveTo(mx + ux * sz, my + uy * sz);
              ctx.lineTo(mx - ux * sz * 0.7 + uy * sz * 0.65, my - uy * sz * 0.7 - ux * sz * 0.65);
              ctx.stroke();
            }
          }
        }
        ctx.globalAlpha = 1;
      }

      // 4. Buildings
      if (vis.buildings) {
        const buildings = si.dataset.buildings;
        const drawStroke = zoom >= -0.75;

        for (let i = 0; i < buildings.length; i++) {
          const b = buildings[i];
          if (!matchesBuildingFilter(b, flt)) continue;
          if (!isVisibleBBox(b._bbox)) continue;

          const ring = b.geometry.coordinates[0];
          const isMatch =
            q.length > 0 &&
            matchesSearchQuery(
              [b.properties.name, b._type, b.properties.district, b.properties.subdistrict],
              q,
            );

          let fill: string;
          if (isMatch) fill = '#fcee09';
          else if (flt.buildingColorMode === 'height') fill = getHeightColor(b.properties.height_m);
          else if (flt.buildingColorMode === 'district') fill = getDistrictColor(b.properties.district);
          else fill = getBuildingTypeColor(b._type);

          ctx.beginPath();
          for (let j = 0; j < ring.length; j++) {
            const sx = (ring[j][0] - originX) * pxPerMeter;
            const sy = (originY - ring[j][1]) * pxPerMeter;
            if (j === 0) ctx.moveTo(sx, sy);
            else ctx.lineTo(sx, sy);
          }
          ctx.closePath();

          ctx.fillStyle = fill;
          ctx.globalAlpha = isMatch ? 0.92 : 0.74;
          ctx.fill();

          if (drawStroke || isMatch) {
            ctx.globalAlpha = isMatch ? 1 : 0.85;
            ctx.strokeStyle = isMatch ? '#ffffff' : 'rgba(6, 9, 15, 0.75)';
            ctx.lineWidth = isMatch ? 1.8 : 0.85;
            ctx.stroke();
          }
        }
        ctx.globalAlpha = 1;
      }

      // 5. Region Labels (drawn over buildings for readability)
      if (vis.regions && vis.regionLabels) {
        const regions = si.dataset.regions;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        for (let i = 0; i < regions.length; i++) {
          const reg = regions[i];
          if (reg.properties.is_quest_district) continue;
          if (!matchesRegionFilter(reg, vis, flt)) continue;
          if (reg.properties.is_subdistrict && zoom < -0.75) continue;
          if (!isVisibleBBox(reg._bbox)) continue;

          const sx = (reg.properties.center[0] - originX) * pxPerMeter;
          const sy = (originY - reg.properties.center[1]) * pxPerMeter;
          if (sx < -80 || sx > w + 80 || sy < -40 || sy > h + 40) continue;

          const isTop = !reg.properties.is_subdistrict;
          ctx.font = isTop
            ? '700 12px "Segoe UI", system-ui, sans-serif'
            : '600 10.5px "Segoe UI", system-ui, sans-serif';
          ctx.lineWidth = 3.5;
          ctx.strokeStyle = 'rgba(5, 8, 17, 0.88)';
          ctx.strokeText(reg.properties.name.toUpperCase(), sx, sy);
          ctx.fillStyle = isTop ? '#e6edf3' : '#94a3b8';
          ctx.fillText(reg.properties.name.toUpperCase(), sx, sy);
        }
      }

      // 6. POIs
      if (vis.poi) {
        const pois = si.dataset.poi;
        const r = Math.max(3.4, Math.min(6.8, 3.8 + (zoom + 2) * 0.65));
        const showLabels = zoom >= 0.5;
        const poiPadLeft = Math.max(80, 220 / pxPerMeter);
        const poiPad = Math.max(80, 32 / pxPerMeter);

        for (let i = 0; i < pois.length; i++) {
          const p = pois[i];
          if (!matchesPoiFilter(p, flt)) continue;
          const [px, py] = p.geometry.coordinates;
          if (px < wMinX - poiPadLeft || px > wMaxX + poiPad || py < wMinY - poiPad || py > wMaxY + poiPad) continue;

          const sx = (px - originX) * pxPerMeter;
          const sy = (originY - py) * pxPerMeter;
          const meta = getPoiMeta(p.properties.category);
          const isMatch =
            q.length > 0 &&
            matchesSearchQuery(
              [p.properties.name, meta.label, p.properties.district, p.properties.subdistrict],
              q,
            );

          const rad = isMatch ? r * 1.45 : r;
          ctx.fillStyle = isMatch ? '#fcee09' : meta.color;
          ctx.strokeStyle = '#06090f';
          ctx.lineWidth = 1.4;

          ctx.beginPath();
          if (meta.shape === 'diamond') {
            ctx.moveTo(sx, sy - rad * 1.2);
            ctx.lineTo(sx + rad * 1.2, sy);
            ctx.lineTo(sx, sy + rad * 1.2);
            ctx.lineTo(sx - rad * 1.2, sy);
            ctx.closePath();
          } else if (meta.shape === 'square') {
            ctx.rect(sx - rad, sy - rad, rad * 2, rad * 2);
          } else {
            ctx.arc(sx, sy, rad, 0, Math.PI * 2);
          }
          ctx.fill();
          ctx.stroke();

          if (showLabels || isMatch) {
            ctx.font = isMatch
              ? '700 11px "Segoe UI", system-ui, sans-serif'
              : '500 10px "Segoe UI", system-ui, sans-serif';
            ctx.textAlign = 'left';
            ctx.textBaseline = 'middle';
            ctx.lineWidth = 3;
            ctx.strokeStyle = 'rgba(5, 8, 17, 0.9)';
            ctx.strokeText(p.properties.name, sx + rad + 5, sy);
            ctx.fillStyle = isMatch ? '#fcee09' : '#e2e8f0';
            ctx.fillText(p.properties.name, sx + rad + 5, sy);
          }
        }
      }
    };

    const scheduleDraw = () => {
      if (!rafId) {
        rafId = requestAnimationFrame(draw);
      }
    };
    scheduleDrawRef.current = scheduleDraw;

    const syncCamera = () => {
      const c = map.getCenter();
      cameraSyncRef.current = {
        x: c.lng,
        y: c.lat,
        zoom2d: map.getZoom(),
      };
    };

    map.on('move zoom viewreset resize moveend zoomend', scheduleDraw);
    map.on('moveend zoomend', syncCamera);

    let lastMouseTime = 0;
    map.on('mousemove', (e: L.LeafletMouseEvent) => {
      const now = performance.now();
      if (now - lastMouseTime < 55) return;
      lastMouseTime = now;
      const wx = e.latlng.lng;
      const wy = e.latlng.lat;
      const loc = propsRef.current.spatialIndex.findDistrictAt(wx, wy);
      propsRef.current.onTelemetry({
        x: wx,
        y: wy,
        district: loc.district,
        subdistrict: loc.subdistrict,
      });
    });

    map.on('click', (e: L.LeafletMouseEvent) => {
      const zoom = map.getZoom();
      const pxPerMeter = Math.max(0.001, map.options.crs?.scale(zoom) ?? Math.pow(2, zoom));
      const toleranceMeters = Math.max(2.5, 13 / pxPerMeter);

      const { spatialIndex: si, visibility: vis, filters: flt, onSelect: selectCb } = propsRef.current;
      const hit = si.hitTest2D(e.latlng.lng, e.latlng.lat, toleranceMeters, vis, flt);
      selectCb(hit);
    });

    const resizeObserver = new ResizeObserver(() => {
      map.invalidateSize({ animate: false });
      scheduleDraw();
    });
    resizeObserver.observe(host);

    scheduleDraw();

    return () => {
      if (rafId) cancelAnimationFrame(rafId);
      resizeObserver.disconnect();
      syncCamera();
      map.remove();
      mapRef.current = null;
      selectionLayerRef.current = null;
    };
  }, [spatialIndex, cameraSyncRef]);

  // Trigger redraw when visibility, filters, or selection change
  useEffect(() => {
    scheduleDrawRef.current();
  }, [visibility, filters, selected]);

  // Render native Leaflet selection vectors on top
  useEffect(() => {
    const layer = selectionLayerRef.current;
    if (!layer) return;
    layer.clearLayers();

    if (!selected) return;

    if (selected.kind === 'building') {
      const latlngs = selected.feature.geometry.coordinates[0].map((p) => L.latLng(p[1], p[0]));
      L.polygon(latlngs, {
        color: '#00f0ff',
        weight: 2.5,
        fillColor: '#00f0ff',
        fillOpacity: 0.3,
        interactive: false,
      }).addTo(layer);
    } else if (selected.kind === 'road') {
      const r = selected.feature;
      for (const inId of r.properties.in_lanes) {
        const lane = spatialIndex.roadById.get(inId);
        if (lane) {
          L.polyline(
            lane.geometry.coordinates.map((p) => L.latLng(p[1], p[0])),
            { color: '#22c55e', weight: 4, opacity: 0.9, interactive: false },
          ).addTo(layer);
        }
      }
      for (const outId of r.properties.out_lanes) {
        const lane = spatialIndex.roadById.get(outId);
        if (lane) {
          L.polyline(
            lane.geometry.coordinates.map((p) => L.latLng(p[1], p[0])),
            { color: '#ff2a6d', weight: 4, opacity: 0.9, interactive: false },
          ).addTo(layer);
        }
      }
      L.polyline(
        r.geometry.coordinates.map((p) => L.latLng(p[1], p[0])),
        { color: '#00f0ff', weight: 5, opacity: 1, interactive: false },
      ).addTo(layer);
    } else if (selected.kind === 'poi') {
      const [px, py] = selected.feature.geometry.coordinates;
      L.circleMarker(L.latLng(py, px), {
        radius: 13,
        color: '#fcee09',
        weight: 2.5,
        fillColor: '#fcee09',
        fillOpacity: 0.25,
        interactive: false,
      }).addTo(layer);
    } else if (selected.kind === 'region') {
      const latlngs = selected.feature.geometry.coordinates[0].map((p) => L.latLng(p[1], p[0]));
      L.polygon(latlngs, {
        color: '#fcee09',
        weight: 2.5,
        fillColor: '#fcee09',
        fillOpacity: 0.14,
        interactive: false,
      }).addTo(layer);
    }
  }, [selected, spatialIndex]);

  // Execute pending fly target when triggered externally
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !pendingFlyTarget) return;
    if (pendingFlyTarget.nonce === lastFlyNonceRef.current) return;
    lastFlyNonceRef.current = pendingFlyTarget.nonce;
    const z = pendingFlyTarget.zoom2d ?? Math.max(map.getZoom(), 0.5);
    map.setView(L.latLng(pendingFlyTarget.y, pendingFlyTarget.x), z, { animate: true });
  }, [pendingFlyTarget]);

  const handleZoomIn = () => mapRef.current?.zoomIn(0.5);
  const handleZoomOut = () => mapRef.current?.zoomOut(0.5);
  const handleFit = () => {
    const map = mapRef.current;
    if (!map) return;
    const [minX, minY, maxX, maxY] = spatialIndex.worldBBox;
    map.fitBounds(
      L.latLngBounds(L.latLng(minY, minX), L.latLng(maxY, maxX)),
      { padding: [32, 32], animate: true },
    );
  };

  return (
    <div className="viewport-wrapper leaflet-theme">
      <div ref={hostRef} className="viewport-canvas" />
      <canvas ref={canvasRef} className="leaflet-custom-canvas" />

      <div className="leaflet-hud-controls">
        <button type="button" onClick={handleZoomIn} title="Przybliż (+)">
          <Plus size={15} />
        </button>
        <button type="button" onClick={handleZoomOut} title="Oddal (-)">
          <Minus size={15} />
        </button>
        <button type="button" onClick={handleFit} title="Dopasuj całą mapę">
          <Crosshair size={15} />
        </button>
      </div>

      <div className="leaflet-scale-bar">
        <div className="leaflet-scale-bar__line" style={{ width: `${Math.max(36, Math.min(180, scaleBar.widthPx))}px` }} />
        <span>{scaleBar.label}</span>
      </div>
    </div>
  );
});
