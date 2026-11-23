import { AlertTriangle, CheckCircle2, FolderOpen, Loader2, RefreshCw } from 'lucide-react';
import type { LayerKey, LoadProgressState } from '../data/loader';

interface LoadingOverlayProps {
  progress: LoadProgressState;
  hasAnyData: boolean;
  isLoading: boolean;
  onRetry: () => void;
  onUploadFiles: (files: FileList) => void;
}

const LAYER_LABELS: { key: LayerKey; label: string; sizeHint: string }[] = [
  { key: 'stats', label: 'index_stats.json', sizeHint: 'Metadane' },
  { key: 'regions', label: 'regions.geojson', sizeHint: '0.14 MB' },
  { key: 'poi', label: 'poi.geojson', sizeHint: '0.31 MB' },
  { key: 'buildings', label: 'buildings.geojson', sizeHint: '9.4 MB' },
  { key: 'roads', label: 'roads.geojson', sizeHint: '22.6 MB' },
];

export function LoadingOverlay({
  progress,
  hasAnyData,
  isLoading,
  onRetry,
  onUploadFiles,
}: LoadingOverlayProps) {
  if (!isLoading && hasAnyData) return null;

  return (
    <div className="loading-overlay">
      <div className="loading-card">
        <div className="loading-card__tag">KIROSHI OPTICS // DATA LINK</div>
        <h2>Inicjalizacja Night City Holomap</h2>
        <p>Ładowanie warstw GeoJSON wyeksportowanych z REDengine 4...</p>

        <div className="loading-list">
          {LAYER_LABELS.map(({ key, label, sizeHint }) => {
            const st = progress.statuses[key];
            const cnt = progress.counts[key];
            const err = progress.errors[key];
            return (
              <div key={key} className={`loading-row loading-row--${st}`}>
                <span className="loading-row__name">
                  {st === 'loading' && <Loader2 size={14} className="spin" />}
                  {st === 'loaded' && <CheckCircle2 size={14} />}
                  {st === 'error' && <AlertTriangle size={14} />}
                  <code>{label}</code>
                </span>
                <span className="loading-row__meta">
                  {st === 'loading' && sizeHint}
                  {st === 'loaded' &&
                    (cnt !== undefined ? `${cnt.toLocaleString('pl-PL')} obiektów` : 'OK')}
                  {st === 'error' && (err || 'Błąd')}
                </span>
              </div>
            );
          })}
        </div>

        {!isLoading && !hasAnyData && (
          <div className="loading-fallback">
            <p className="loading-fallback__warn">
              Nie udało się pobrać plików z <code>/data/</code>. Upewnij się, że katalog{' '}
              <code>output/</code> zawiera pliki GeoJSON albo wskaż je ręcznie poniżej:
            </p>
            <div className="loading-fallback__actions">
              <button type="button" className="action-btn" onClick={onRetry}>
                <RefreshCw size={14} /> Spróbuj ponownie
              </button>
              <label className="action-btn">
                <FolderOpen size={14} /> Wybierz pliki z output/
                <input
                  type="file"
                  multiple
                  accept=".geojson,.json"
                  style={{ display: 'none' }}
                  onChange={(e) => {
                    if (e.target.files && e.target.files.length > 0) {
                      onUploadFiles(e.target.files);
                    }
                  }}
                />
              </label>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
