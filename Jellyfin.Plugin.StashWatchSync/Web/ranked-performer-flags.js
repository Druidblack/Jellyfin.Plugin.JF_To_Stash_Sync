/* JF To Stash Sync — flags for ranked performer portraits (v1.2.0.26).
 * Independent of Actor Plus. Read-only birthplace lookup from Jellyfin's Person metadata.
 */
(function () {
    'use strict';
    const T = (text) => window.__jfToStashI18n?.t(text) ?? String(text ?? '');
    if (window.__jfRankedPerformerFlagsLoaded) return;
    window.__jfRankedPerformerFlagsLoaded = true;

    const TILE_SELECTOR = '.jf-spp-tile[data-person-id], .jf-bpt-tile[data-person-id]';
    const MAX_BATCH = 60;
    const CACHE_MS = 10 * 60 * 1000;
    const cache = new Map(); // Person ID -> {country, birthPlace, expires}; cache also stores missing values.
    let enabled = false;
    let configuredUser = '';
    let loadingConfig = false;
    let requestRunning = false;
    let pendingRefresh = false;
    let timer = null;

    function api() { return window.ApiClient; }
    function userId() {
        try { return api()?.getCurrentUserId() || ''; } catch (_) { return ''; }
    }
    function id(value) { return String(value || '').replace(/-/g, '').toLowerCase(); }
    function val(value, camel, pascal, fallback) {
        return value && value[camel] !== undefined ? value[camel]
            : value && value[pascal] !== undefined ? value[pascal] : fallback;
    }
    async function getJson(path) {
        const client = api();
        if (!client?.fetch || !client.getUrl) throw new Error('Jellyfin ApiClient is unavailable');
        const response = await client.fetch({ url: client.getUrl(path), type: 'GET' });
        if (!response.ok) throw new Error('Jellyfin HTTP ' + response.status);
        return response.json();
    }
    function twemojiFlag(iso2) {
        if (!/^[A-Z]{2}$/.test(iso2)) return '';
        const codepoints = [...iso2].map(x => (0x1f1e6 + x.charCodeAt(0) - 65).toString(16));
        return 'https://cdnjs.cloudflare.com/ajax/libs/twemoji/14.0.2/svg/' + codepoints.join('-') + '.svg';
    }
    function nativeFlag(iso2) {
        return [...iso2].map(x => String.fromCodePoint(0x1f1e6 + x.charCodeAt(0) - 65)).join('');
    }
    function stylesOnce() {
        if (document.getElementById('jf-ranked-performer-flag-styles')) return;
        const style = document.createElement('style');
        style.id = 'jf-ranked-performer-flag-styles';
        style.textContent = `
          .jf-spp-portrait .jf-ranked-birth-flag,.jf-bpt-portrait .jf-ranked-birth-flag{
            position:absolute;right:5px;bottom:5px;z-index:5;pointer-events:none;
            width:26px;height:23px;display:flex;align-items:center;justify-content:center;
            border-radius:4px;background:rgba(14,14,14,.48);box-shadow:0 1px 4px rgba(0,0,0,.3)
          }
          .jf-spp-portrait .jf-ranked-birth-flag img.jf-ranked-flag-icon,
          .jf-bpt-portrait .jf-ranked-birth-flag img.jf-ranked-flag-icon{
            width:22px;height:22px;object-fit:contain;display:block;max-width:22px;max-height:22px
          }
        `;
        document.head.appendChild(style);
    }
    function portrait(tile) {
        return tile.querySelector('.jf-bpt-portrait, .jf-spp-portrait');
    }
    function decorate(tile, record) {
        const cover = portrait(tile);
        if (!cover) return;
        let badge = cover.querySelector(':scope > .jf-ranked-birth-flag');
        const country = String(record?.country || '').toUpperCase();
        if (!/^[A-Z]{2}$/.test(country)) {
            badge?.remove();
            return;
        }
        if (badge?.dataset.country === country) return;
        badge?.remove();
        badge = document.createElement('span');
        badge.className = 'jf-ranked-birth-flag';
        badge.dataset.country = country;
        badge.title = record.birthPlace || country;
        const picture = document.createElement('img');
        picture.className = 'jf-ranked-flag-icon';
        picture.alt = country;
        picture.decoding = 'async';
        picture.loading = 'lazy';
        picture.src = twemojiFlag(country);
        picture.addEventListener('error', () => {
            picture.remove();
            badge.textContent = nativeFlag(country);
        }, { once: true });
        badge.appendChild(picture);
        cover.appendChild(badge);
    }
    function findTiles() {
        return Array.from(document.querySelectorAll(TILE_SELECTOR))
            .filter(tile => /^[a-f0-9]{32}$/.test(id(tile.dataset.personId)));
    }
    function queue() {
        if (!enabled || document.hidden || !userId()) return;
        if (timer) clearTimeout(timer);
        timer = setTimeout(() => { timer = null; void refresh(); }, 90);
    }
    async function refresh() {
        if (!enabled || document.hidden) return;
        if (requestRunning) { pendingRefresh = true; return; }
        const current = userId();
        if (!current || current !== configuredUser) return;
        const tiles = findTiles();
        const now = Date.now();
        const requested = new Set();
        for (const tile of tiles) {
            const personId = id(tile.dataset.personId);
            const cached = cache.get(personId);
            if (cached && cached.expires > now) decorate(tile, cached);
            else requested.add(personId);
        }
        if (!requested.size) return;
        requestRunning = true;
        try {
            const keys = Array.from(requested);
            for (let offset = 0; offset < keys.length; offset += MAX_BATCH) {
                if (userId() !== current || !enabled) break;
                const batch = keys.slice(offset, offset + MAX_BATCH);
                const response = await getJson('JFToStashSync/RankedPerformerBirthCountries?ids=' + batch.join(','));
                const loadedAt = Date.now();
                for (const pid of batch) {
                    const value = response?.[pid];
                    cache.set(pid, {
                        country: String(val(value, 'country', 'Country', '') || '').toUpperCase(),
                        birthPlace: String(val(value, 'birthPlace', 'BirthPlace', '') || ''),
                        expires: loadedAt + CACHE_MS
                    });
                }
            }
            if (current === userId()) {
                for (const tile of findTiles()) {
                    const cached = cache.get(id(tile.dataset.personId));
                    if (cached) decorate(tile, cached);
                }
            }
        } catch (e) {
            // No busy retries when the endpoint or country data is unavailable.
            console.warn('[JFToStashSync] Could not load ranked performer birth countries.', e);
            const retryAt = Date.now() + 30000;
            for (const pid of requested) if (!cache.has(pid)) cache.set(pid, { country: '', expires: retryAt });
        } finally {
            requestRunning = false;
            if (pendingRefresh) { pendingRefresh = false; queue(); }
        }
    }
    async function configure() {
        const current = userId();
        if (!current || loadingConfig || (current === configuredUser)) return;
        loadingConfig = true;
        try {
            const config = await (window.__jfHomeWarmup?.getConfig?.() || getJson('JFToStashSync/PlayerOCounterConfig'));
            if (current !== userId()) return;
            configuredUser = current;
            cache.clear();
            enabled = val(config, 'showRankedPerformerBirthFlags', 'ShowRankedPerformerBirthFlags', false) === true;
            if (enabled) { stylesOnce(); queue(); }
            else document.querySelectorAll('.jf-ranked-birth-flag').forEach(x => x.remove());
        } catch (e) {
            console.warn('[JFToStashSync] Could not read performer flag settings.', e);
        } finally { loadingConfig = false; }
    }
    function changed() {
        if (userId() !== configuredUser) {
            enabled = false;
            configuredUser = '';
            cache.clear();
            document.querySelectorAll('.jf-ranked-birth-flag').forEach(x => x.remove());
            void configure();
            return;
        }
        queue();
    }
    function start() {
        void configure();
        const observer = new MutationObserver(changes => {
            if (!enabled) return;
            for (const change of changes) {
                for (const node of change.addedNodes) {
                    if (node.nodeType === 1 &&
                        (node.matches?.(TILE_SELECTOR) || node.querySelector?.(TILE_SELECTOR))) {
                        queue();
                        return;
                    }
                }
            }
        });
        observer.observe(document.body, { childList: true, subtree: true });
        window.addEventListener('hashchange', changed);
        document.addEventListener('viewshow', changed, true);
        document.addEventListener('visibilitychange', () => { if (!document.hidden) changed(); });
        window.setInterval(() => { if (userId() !== configuredUser) changed(); }, 10000);
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
}());
