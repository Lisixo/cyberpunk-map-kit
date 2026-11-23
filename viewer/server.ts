import path from 'node:path';

const ALLOWED_DATA_FILES = new Set([
  'regions.geojson',
  'roads.geojson',
  'buildings.geojson',
  'poi.geojson',
  'index_stats.json',
]);

const PORT = Number(process.env.PORT) || 3000;
const DIST_DIR = path.resolve(import.meta.dir, 'dist');
const OUTPUT_DIR = path.resolve(import.meta.dir, '../output');

const server = Bun.serve({
  port: PORT,
  async fetch(req) {
    const url = new URL(req.url);
    const pathname = decodeURIComponent(url.pathname);

    if (pathname.startsWith('/data/')) {
      const fileName = pathname.slice('/data/'.length);
      if (!ALLOWED_DATA_FILES.has(fileName)) {
        return new Response(JSON.stringify({ error: 'Not found' }), {
          status: 404,
          headers: { 'Content-Type': 'application/json; charset=utf-8' },
        });
      }

      const filePath = path.resolve(OUTPUT_DIR, fileName);
      if (!filePath.startsWith(OUTPUT_DIR + path.sep)) {
        return new Response('Forbidden', { status: 403 });
      }

      const file = Bun.file(filePath);
      if (!(await file.exists())) {
        return new Response(JSON.stringify({ error: `Missing ${fileName}` }), {
          status: 404,
          headers: { 'Content-Type': 'application/json; charset=utf-8' },
        });
      }

      return new Response(file, {
        headers: {
          'Content-Type': 'application/json; charset=utf-8',
          'Cache-Control': 'no-cache',
        },
      });
    }

    const relPath = pathname === '/' ? 'index.html' : pathname.replace(/^\/+/, '');
    const staticPath = path.resolve(DIST_DIR, relPath);
    if (staticPath.startsWith(DIST_DIR + path.sep) || staticPath === DIST_DIR) {
      const staticFile = Bun.file(staticPath);
      if (await staticFile.exists()) {
        return new Response(staticFile);
      }
    }

    const indexFile = Bun.file(path.join(DIST_DIR, 'index.html'));
    if (await indexFile.exists()) {
      return new Response(indexFile, {
        headers: { 'Content-Type': 'text/html; charset=utf-8' },
      });
    }

    return new Response('Build missing. Run "bun run build" first.', { status: 404 });
  },
});

console.log(`Night City Holomap server listening on http://localhost:${server.port}`);
