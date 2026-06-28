'use strict';

const SHELL = 'nexiffy-shell-v17';
const DATA  = 'nexiffy-data-v17';

const SHELL_FILES = ['/', '/manifest.json', '/icon.svg'];

// ── Empty offline shapes so the app JS never crashes on undefined ──
const OFFLINE_AUTH     = { username: null };
const OFFLINE_MEDS     = { items: [], total: 0, page: 1, pageSize: 500 };
const OFFLINE_DASH     = {
  offline: true,
  period: 'today', startDate: '', today: '',
  periodSales: 0, periodBillCount: 0,
  totalRevenue: 0, totalMedicines: 0, lowStock: 0, totalBills: 0, inventoryValue: 0,
  dailyRevenue: [], salesByCategory: [], savedCount: 0, cancelledCount: 0,
  expiryCalendar: [], topMedicines: [], recentBills: [], expiringMeds: []
};
const OFFLINE_LIST     = { items: [], total: 0, page: 1, pageSize: 20 };
const OFFLINE_CATS     = [];

// ── Install: pre-cache the app shell ─────────────
self.addEventListener('install', e =>
  e.waitUntil(
    caches.open(SHELL)
      .then(c => c.addAll(SHELL_FILES))
      .then(() => self.skipWaiting())
  )
);

// ── Activate: evict old caches ────────────────────
self.addEventListener('activate', e =>
  e.waitUntil(
    caches.keys()
      .then(keys => Promise.all(
        keys.filter(k => k !== SHELL && k !== DATA).map(k => caches.delete(k))
      ))
      .then(() => self.clients.claim())
  )
);

// ── Fetch: per-route caching strategy ────────────
self.addEventListener('fetch', e => {
  if (e.request.method !== 'GET') return;

  const { pathname, search } = new URL(e.request.url);

  // ── App shell (HTML/CSS/JS/icons) → cache-first ─
  if (!pathname.startsWith('/api/')) {
    e.respondWith(
      caches.open(SHELL)
        .then(c => c.match(e.request))
        .then(hit => hit || fetch(e.request).then(r => updateCache(SHELL, e.request, r)))
        .catch(() => caches.match('/'))
    );
    return;
  }

  // ── Auth/me → network-first, cache fallback ──────
  // Cached so the user stays "logged in" offline between app restarts.
  if (pathname === '/api/Auth/me') {
    e.respondWith(networkFirst(e.request, DATA, OFFLINE_AUTH, 200));
    return;
  }

  // ── Medicine catalog → network-first, cache fallback ─
  // Only cache: categories, low-stock, and the full POS catalog (pageSize=500).
  // Paginated/filtered queries (search=, category=, page=N) are not cached to
  // prevent the DATA cache growing without bound from every search term ever typed.
  if (pathname.startsWith('/api/Medicines')) {
    if (pathname.endsWith('/categories')) {
      e.respondWith(networkFirst(e.request, DATA, OFFLINE_CATS, 200));
    } else if (pathname.endsWith('/low-stock')) {
      e.respondWith(networkFirst(e.request, DATA, OFFLINE_LIST, 200));
    } else if (!search || search.includes('pageSize=500')) {
      // Full catalog fetch — cache it for POS offline use
      e.respondWith(networkFirst(e.request, DATA, OFFLINE_MEDS, 200));
    } else {
      // Filtered/paginated search — network only, no cache pollution
      e.respondWith(
        fetch(e.request).catch(() =>
          new Response(JSON.stringify(OFFLINE_MEDS), {
            status: 200, headers: { 'Content-Type': 'application/json' }
          })
        )
      );
    }
    return;
  }

  // ── Dashboard → network-first, stale-ok ──────────
  if (pathname.startsWith('/api/Dashboard')) {
    e.respondWith(networkFirst(e.request, DATA, OFFLINE_DASH, 200));
    return;
  }

  // ── Categories → network-first ────────────────────
  if (pathname.startsWith('/api/Categories')) {
    e.respondWith(networkFirst(e.request, DATA, OFFLINE_CATS, 200));
    return;
  }

  // ── Bills list (read) → network-first ────────────
  if (pathname.startsWith('/api/Bills') && !pathname.includes('/cancel')) {
    e.respondWith(networkFirst(e.request, DATA, OFFLINE_LIST, 200));
    return;
  }

  // ── Everything else → network only (Health, etc.) ─
});

// ── Helpers ───────────────────────────────────────

async function updateCache(name, req, res) {
  if (res && res.ok) {
    const c = await caches.open(name);
    c.put(req, res.clone());
  }
  return res;
}

async function networkFirst(req, cacheName, offlineShape, offlineStatus) {
  try {
    const res = await fetch(req);
    if (res.ok) updateCache(cacheName, req, res);
    return res;
  } catch {
    const cached = await caches.match(req);
    if (cached) return cached;
    return new Response(JSON.stringify(offlineShape), {
      status: offlineStatus,
      headers: { 'Content-Type': 'application/json' }
    });
  }
}

self.addEventListener('message', e => {
  if (e.data?.type === 'SKIP_WAITING') self.skipWaiting();
});
