// Immutable-media cache worker.
//
// Media URLs on this server never change after creation (content-named), so
// this worker serves same-origin GET /media/* cache-first, FOREVER, with no
// revalidation. Durability posture: cached bytes are precious (they cost real
// egress money to re-fetch) — we call navigator.storage.persist() from the
// player, evict only by our own LRU janitor, and never clear on upgrades.
//
// Eviction: metadata (size, lastAccess per URL) lives in IndexedDB. A janitor
// keeps Cache Storage usage under BUDGET_FRACTION of the origin quota (or a
// player-supplied cap), evicting least-recently-shown first.
//
// Failure posture: every path falls through to plain network — the slideshow
// must never be worse than uncached because this worker hiccuped.

const CACHE_NAME = 'media-v1';
const DB_NAME = 'media-cache-meta';
const STORE = 'entries';
const MAX_ENTRY_BYTES = 200 * 1024 * 1024;   // don't let one huge video eat the budget
const BUDGET_FRACTION = 0.8;                 // of origin quota; quota ≈ 60% of disk
const TOUCH_THROTTLE_MS = 24 * 3600 * 1000;  // lastAccess writes at most daily per URL
const JANITOR_INTERVAL_MS = 6 * 3600 * 1000;

let capBytes = null;                          // optional player-supplied cap
let hits = 0, misses = 0;
let lastJanitor = 0;

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));

self.addEventListener('fetch', e => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET') return;
  if (url.origin !== self.location.origin) return;
  if (!url.pathname.startsWith('/media/')) return;
  e.respondWith(cacheFirst(e.request));
});

self.addEventListener('message', e => {
  const msg = e.data || {};
  if (msg.type === 'config' && typeof msg.capBytes === 'number') capBytes = msg.capBytes;
  if (msg.type === 'janitor') e.waitUntil(janitor(true));
  if (msg.type === 'stats') {
    e.waitUntil((async () => {
      const est = await self.navigator.storage.estimate().catch(() => ({}));
      e.source?.postMessage({
        type: 'cache-stats', usage: est.usage, quota: est.quota,
        hits, misses, budget: await budgetBytes(),
      });
    })());
  }
});

async function cacheFirst(req) {
  try {
    const cache = await caches.open(CACHE_NAME);
    const hit = await cache.match(req.url);
    if (hit) {
      hits++;
      touch(req.url);           // fire-and-forget, throttled
      maybeJanitor();
      return hit;
    }
    misses++;
    const resp = await fetch(req);
    if (resp.ok && resp.status === 200) {   // never cache partial/range responses
      const len = Number(resp.headers.get('content-length')) || 0;
      if (len > 0 && len <= MAX_ENTRY_BYTES) {
        const copy = resp.clone();
        cache.put(req.url, copy)
          .then(() => putMeta(req.url, { size: len, lastAccess: Date.now() }))
          .then(() => maybeJanitor())
          .catch(() => {});      // quota/IO failure: stream-through still succeeded
      }
    }
    return resp;
  } catch {
    return fetch(req);           // worker must never make things worse
  }
}

// ---------- metadata (IndexedDB) ----------

function db() {
  return new Promise((resolve, reject) => {
    const open = indexedDB.open(DB_NAME, 1);
    open.onupgradeneeded = () => open.result.createObjectStore(STORE, { keyPath: 'url' });
    open.onsuccess = () => resolve(open.result);
    open.onerror = () => reject(open.error);
  });
}

async function putMeta(url, value) {
  const d = await db();
  return new Promise((resolve, reject) => {
    const tx = d.transaction(STORE, 'readwrite');
    tx.objectStore(STORE).put({ url, ...value });
    tx.oncomplete = resolve;
    tx.onerror = () => reject(tx.error);
  }).finally(() => d.close());
}

async function allMeta() {
  const d = await db();
  return new Promise((resolve, reject) => {
    const req = d.transaction(STORE).objectStore(STORE).getAll();
    req.onsuccess = () => resolve(req.result || []);
    req.onerror = () => reject(req.error);
  }).finally(() => d.close());
}

async function deleteMeta(url) {
  const d = await db();
  return new Promise((resolve, reject) => {
    const tx = d.transaction(STORE, 'readwrite');
    tx.objectStore(STORE).delete(url);
    tx.oncomplete = resolve;
    tx.onerror = () => reject(tx.error);
  }).finally(() => d.close());
}

const touched = new Map();  // url -> last in-memory touch, to throttle IDB writes
function touch(url) {
  const now = Date.now();
  const last = touched.get(url) || 0;
  if (now - last < TOUCH_THROTTLE_MS) return;
  touched.set(url, now);
  // read-modify-write so the stored size survives the lastAccess update
  db().then(d => {
    const tx = d.transaction(STORE, 'readwrite');
    const store = tx.objectStore(STORE);
    const get = store.get(url);
    get.onsuccess = () => {
      const rec = get.result || { url, size: 0 };
      rec.lastAccess = now;
      store.put(rec);
    };
    tx.oncomplete = () => d.close();
    tx.onabort = () => d.close();
  }).catch(() => {});
}

// ---------- janitor ----------

async function budgetBytes() {
  const est = await self.navigator.storage.estimate().catch(() => null);
  if (!est || !est.quota) return capBytes || 0;
  const fromQuota = Math.floor(est.quota * BUDGET_FRACTION);
  return capBytes ? Math.min(capBytes, fromQuota) : fromQuota;
}

function maybeJanitor() {
  if (Date.now() - lastJanitor > JANITOR_INTERVAL_MS) {
    lastJanitor = Date.now();
    janitor(false).catch(() => {});
  }
}

async function janitor(force) {
  const budget = await budgetBytes();
  if (!budget) return;
  const est = await self.navigator.storage.estimate().catch(() => null);
  if (!est) return;
  if (!force && est.usage < budget) return;

  const cache = await caches.open(CACHE_NAME);
  const meta = (await allMeta()).sort((a, b) => (a.lastAccess || 0) - (b.lastAccess || 0));
  let usage = est.usage;
  for (const m of meta) {
    if (usage <= budget) break;
    await cache.delete(m.url).catch(() => {});
    await deleteMeta(m.url).catch(() => {});
    usage -= (m.size || 0);
  }
}
