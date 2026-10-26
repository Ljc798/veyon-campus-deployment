const CACHE_VERSION = "veyon-campus-mobile-v42";

self.addEventListener("install", event => {
  event.waitUntil(self.skipWaiting());
});

self.addEventListener("activate", event => {
  event.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys
        .filter(key => key.startsWith("veyon-campus-mobile-") && key !== CACHE_VERSION)
        .map(key => caches.delete(key))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener("fetch", event => {
  const url = new URL(event.request.url);
  if (url.origin !== self.location.origin || event.request.method !== "GET") return;

  // This app controls a live teacher console on the local network. Cached HTML or
  // scripts can point a reconnected phone at obsolete routes, so always use the
  // teacher's current response and never fall back to a stale shell.
  event.respondWith(fetch(event.request, { cache: "no-store" }));
});
