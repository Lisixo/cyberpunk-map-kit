import fs from 'node:fs';
import path from 'node:path';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { defineConfig, type Plugin, type Connect } from 'vite';
import react from '@vitejs/plugin-react';

const ALLOWED_DATA_FILES = new Set([
  'regions.geojson',
  'roads.geojson',
  'buildings.geojson',
  'poi.geojson',
  'index_stats.json',
]);

const OUTPUT_DIR = path.resolve(__dirname, '../output');

function attachDataMiddleware(middlewares: Connect.Server): void {
  middlewares.use('/data', (req: IncomingMessage, res: ServerResponse, next: Connect.NextFunction) => {
    const method = (req.method || 'GET').toUpperCase();
    if (method !== 'GET' && method !== 'HEAD') {
      next();
      return;
    }

    const rawUrl = (req.url || '/').split('?')[0];
    const fileName = rawUrl.replace(/^\/+/, '');
    if (!ALLOWED_DATA_FILES.has(fileName)) {
      res.statusCode = 404;
      res.setHeader('Content-Type', 'application/json; charset=utf-8');
      res.end(JSON.stringify({ error: 'Not found' }));
      return;
    }

    const targetPath = path.resolve(OUTPUT_DIR, fileName);
    if (!targetPath.startsWith(OUTPUT_DIR + path.sep)) {
      res.statusCode = 403;
      res.end();
      return;
    }

    fs.stat(targetPath, (err, stats) => {
      if (err || !stats.isFile()) {
        res.statusCode = 404;
        res.setHeader('Content-Type', 'application/json; charset=utf-8');
        res.end(JSON.stringify({ error: `Missing ${fileName} in output directory` }));
        return;
      }

      res.statusCode = 200;
      res.setHeader('Content-Type', 'application/json; charset=utf-8');
      res.setHeader('Content-Length', String(stats.size));
      res.setHeader('Cache-Control', 'no-cache');

      if (method === 'HEAD') {
        res.end();
        return;
      }

      const stream = fs.createReadStream(targetPath);
      stream.on('error', () => {
        if (!res.headersSent) {
          res.statusCode = 500;
        }
        res.end();
      });
      stream.pipe(res);
    });
  });
}

function serveOutputDataPlugin(): Plugin {
  return {
    name: 'serve-output-data',
    configureServer(server) {
      attachDataMiddleware(server.middlewares);
    },
    configurePreviewServer(server) {
      attachDataMiddleware(server.middlewares);
    },
  };
}

export default defineConfig({
  plugins: [react(), serveOutputDataPlugin()],
  build: {
    chunkSizeWarningLimit: 900,
    rollupOptions: {
      output: {
        manualChunks: {
          three: ['three'],
          leaflet: ['leaflet'],
        },
      },
    },
  },
});
