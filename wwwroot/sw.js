'use strict';
const CACHE = 'nexffy-v1';

// On install: pre-cache the SPA shell
self.addEventListener('install', e =>
  e.waitUntil(
    caches.open(CACHE)
      .then(c => c.add('/'))
      .then(() => self.skipWaiting())
  )
);

// On activate: evict old cache versions
self.addEventListener('activate', e =>
  e.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k))))
      .then(() => self.clients.claim())
  )
);

// Fetch strategy:
//  - API calls  → network only (app JS handles offline fallback)
//  - Everything else → network-first, fall back to cache, fall back to index.html
self.addEventListener('fetch', e => {
  if (e.request.method !== 'GET') return;

  const url = new URL(e.request.url);
  if (url.pathname.startsWith('/api/')) return;

  e.respondWith(
    fetch(e.request)
      .then(res => {
        const clone = res.clone();
        caches.open(CACHE).then(c => c.put(e.request, clone));
        return res;
      })
      .catch(() =>
        caches.match(e.request)
          .then(cached => cached || caches.match('/'))
      )
  );
});

// When the app tells us to sync pending bills, notify all open tabs
self.addEventListener('message', e => {
  if (e.data?.type === 'SKIP_WAITING') self.skipWaiting();
});
