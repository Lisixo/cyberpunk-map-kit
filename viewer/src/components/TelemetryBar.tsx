import type { CursorTelemetry, ViewMode } from '../types/map';

interface TelemetryBarProps {
  telemetry: CursorTelemetry;
  viewMode: ViewMode;
  visibleCounts: {
    buildings: number;
    roads: number;
    poi: number;
  };
}

export function TelemetryBar({ telemetry, viewMode, visibleCounts }: TelemetryBarProps) {
  return (
    <div className="telemetry-bar">
      <div className="telemetry-bar__mode">
        <span className="pulse-dot" />
        <span>{viewMode === '3d' ? 'THREE.JS 3D' : 'LEAFLET 2D'}</span>
      </div>

      <div className="telemetry-bar__coords">
        <span>X: <strong>{telemetry.x.toFixed(1)}</strong> m</span>
        <span>Y: <strong>{telemetry.y.toFixed(1)}</strong> m</span>
      </div>

      <div className="telemetry-bar__loc">
        {telemetry.district ? (
          <>
            <strong>{telemetry.district}</strong>
            {telemetry.subdistrict && <span> / {telemetry.subdistrict}</span>}
          </>
        ) : (
          <span className="muted">Poza wyznaczonymi regionami</span>
        )}
      </div>

      <div className="telemetry-bar__counts">
        <span>Budynki: <strong>{visibleCounts.buildings.toLocaleString('pl-PL')}</strong></span>
        <span>Pasy: <strong>{visibleCounts.roads.toLocaleString('pl-PL')}</strong></span>
        <span>POI: <strong>{visibleCounts.poi.toLocaleString('pl-PL')}</strong></span>
      </div>
    </div>
  );
}
