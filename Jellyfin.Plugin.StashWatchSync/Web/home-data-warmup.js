/* JF To Stash Sync — shared, signed-in-client configuration and image warmup.
 * Transient memory only: no personal/user-specific metadata is persisted in storage.
 */
(function () {
  'use strict';
  const T = (text) => window.__jfToStashI18n?.t(text) ?? String(text ?? '');
  if (window.__jfHomeWarmup) return;
  const CONFIG_TTL = 10 * 1000;
  const MAX_IMAGES = 8;
  let user = '', entry = null, pending = null;
  const warmed = new Map();
  const imageRefs = new Map();
  function currentUser() {
    try { return String(window.ApiClient?.getCurrentUserId?.() || ''); }
    catch (_) { return ''; }
  }
  function syncUser() {
    const next = currentUser();
    if (next !== user) {
      user = next;
      entry = null;
      pending = null;
      warmed.clear();
      imageRefs.clear();
      if (next) document.dispatchEvent(new CustomEvent('jf-to-stash-user-ready', {detail:{userId:next}}));
    }
    return user;
  }
  async function getConfig() {
    const id = syncUser();
    if (!id) throw new Error('Jellyfin user is not signed in');
    if (entry && entry.user === id && Date.now() < entry.expires) return entry.data;
    if (pending && pending.user === id) return pending.promise;
    const client = window.ApiClient;
    if (!client?.fetch || !client?.getUrl) throw new Error('Jellyfin API unavailable');
    const promise = (async () => {
      const response = await client.fetch({url:client.getUrl('JFToStashSync/PlayerOCounterConfig'),type:'GET'});
      if (!response.ok) throw new Error('Jellyfin config HTTP ' + response.status);
      const data = await response.json();
      if (currentUser() === id) entry = {user:id,expires:Date.now()+CONFIG_TTL,data};
      return data;
    })();
    pending = {user:id,promise};
    try { return await promise; }
    finally { if (pending?.promise === promise) pending = null; }
  }
  // Preload no more than the first few images; never preload user-authored external URLs.
  function preloadImages(urls) {
    if (!syncUser() || typeof Image !== 'function') return;
    for (const url of urls.slice(0, MAX_IMAGES)) {
      if (!url || typeof url !== 'string' || imageRefs.has(url)) continue;
      // All URLs come from ApiClient.getUrl('/Items/.../Images/...').
      const img = new Image();
      imageRefs.set(url, img);
      // Release image element/decoded bitmap on completion; remember only
      // the URL so a later Home remount does not queue the same preload.
      img.onload = img.onerror = () => {
        if (imageRefs.get(url) === img) imageRefs.set(url, true);
      };
      if (imageRefs.size > 36) imageRefs.delete(imageRefs.keys().next().value);
      img.src = url;
    }
  }
  function signalDataReady(kind) { document.dispatchEvent(new CustomEvent('jf-to-stash-data-ready',{detail:{kind,userId:user}})); }
  window.__jfHomeWarmup = {getConfig,preloadImages,signalDataReady,currentUser:syncUser};
  // Auth may complete after the scripts are loaded on a Settings page.
  const timer = setInterval(() => {
    syncUser();
    if (user) clearInterval(timer);
  }, 400);
  syncUser();
})();
