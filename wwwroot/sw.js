// Minimal service worker. Its jobs are (1) make AiPulse installable and (2) show a friendly page instead of the browser's
// "no internet" dinosaur when a navigation fails. It deliberately does NOT cache pages or API calls: the app is a live Blazor Server
// circuit, and serving a stale page that cannot reach its server would be worse than the offline notice.
const VERSION = 'aipulse-sw-1';
const OFFLINE_URL = '/offline.html';

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(VERSION).then((cache) => cache.add(OFFLINE_URL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== VERSION).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (event) => {
  if (event.request.mode !== 'navigate') return;
  event.respondWith(fetch(event.request).catch(() => caches.match(OFFLINE_URL)));
});
