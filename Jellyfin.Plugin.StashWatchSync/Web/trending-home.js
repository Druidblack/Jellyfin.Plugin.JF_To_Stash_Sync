/* JF To Stash Sync — StashDB Trending on Jellyfin Web (opt-in), v1.2.0.15.
 * Source: Jellyfin server's native StashDB GraphQL integration. Cards and access are Jellyfin-local.
 */
(function () {
    'use strict';
    const T = (text) => window.__jfToStashI18n?.t(text) ?? String(text ?? '');
    if (window.__jfToStashTrendingLoaded) return;
    window.__jfToStashTrendingLoaded = true;

    const SECTION_ID = 'jf-stashdb-trending-section';
    const MODAL_ID = 'jf-stashdb-trending-modal';
    const BATCH_SIZE = 60;
    const CACHE_MS = 10 * 60 * 1000;
    const REQUEST_TIMEOUT_MS = 25000;
    const data = {
        config: null, userId: '', byScene: new Map(), entries: new Map(),
        expires: 0, running: null, error: '', status: '', generation: 0, updatedAt: ''
    };
    let configPending = false;
    let mountPending = false;
    let wasOnHome = false;
    let activeModal = null;

    function apiClient() { return window.ApiClient; }
    async function fetchJson(path, timeoutMs = REQUEST_TIMEOUT_MS) {
        const api = apiClient();
        if (!api || typeof api.fetch !== 'function' || typeof api.getUrl !== 'function') {
            throw new Error('Jellyfin API недоступен. Обновите страницу.');
        }
        // Jellyfin ApiClient implementations may not forward AbortController's signal;
        // Promise.race bounds both the HTTP request and JSON body parsing.
        let timer;
        const attempt = (async () => {
            const response = await api.fetch({ url: api.getUrl(path), type: 'GET' });
            let json = null;
            try { json = await response.json(); } catch (_) { /* may be non-JSON */ }
            if (!response.ok) {
                const message = json && (json.message || json.Message);
                throw new Error(message || ('Jellyfin HTTP ' + response.status));
            }
            return json;
        })();
        const timeout = new Promise((_, reject) => {
            timer = window.setTimeout(() => reject(new Error(
                'Превышено время ожидания (' + Math.round(timeoutMs / 1000) + ' с): ' +
                (path.startsWith('JFToStashSync/TrendingPrepared') ? 'готовый индекс Trending' : 'метаданные видео')
            )), timeoutMs);
        });
        try { return await Promise.race([attempt, timeout]); }
        finally { window.clearTimeout(timer); }
    }
    function val(object, camel, pascal, fallback) {
        return object && object[camel] !== undefined ? object[camel] :
            object && object[pascal] !== undefined ? object[pascal] : fallback;
    }
    function currentUserId() {
        const api = apiClient();
        try { return api && api.getCurrentUserId && api.getCurrentUserId() || ''; }
        catch (_) { return ''; }
    }
    function id(value) { return String(value || '').replace(/-/g, '').toLowerCase(); }
    function visible(el) {
        return !!(el && el.isConnected && el.getClientRects().length &&
            getComputedStyle(el).visibility !== 'hidden' &&
            getComputedStyle(el).display !== 'none');
    }
    // Jellyfin 12 uses the same /home view for Home and Favorites. Only tab=0
    // (or no tab parameter) is the actual main Home page for custom rows.
    // Prefer the hash router; pathname is a fallback for legacy router builds.
    function isMainHomeTab() {
        const fragment = String(location.hash || '').replace(/^#!?/, '');
        const route = fragment || String(location.pathname || '');
        const queryAt = route.indexOf('?');
        const path = queryAt < 0 ? route : route.slice(0, queryAt);
        if (!/(?:^|\/)home\/?$/i.test(path)) return false;
        const query = queryAt < 0 ? (!fragment ? String(location.search || '').replace(/^\?/, '') : '') : route.slice(queryAt + 1);
        const tab = new URLSearchParams(query).get('tab');
        return tab === null || tab === '' || tab === '0';
    }
    function isHomeRoute() {
        const fragment = String(location.hash || '').replace(/^#!?/, '');
        const route = fragment || String(location.pathname || '');
        return /(?:^|\/)home\/?(?:\?|$)/i.test(route);
    }
    function root() {
        if (!isMainHomeTab()) return null;
        // Legacy/classic Jellyfin 12 home sections; do not append to an arbitrary page.
        const selectors = [
            '.homeSectionsContainer',
            '.homePage .homeSections',
            '.homePage .sectionsContainer',
            '.homeView .homeSectionsContainer'
        ];
        for (const selector of selectors) {
            const match = Array.from(document.querySelectorAll(selector)).find(visible);
            if (match) return match;
        }
        // Some builds wrap sections directly inside the home page.
        const home = Array.from(document.querySelectorAll('.homePage, .homeView'))
            .find(el => visible(el) && el.querySelector('.verticalSection'));
        if (home) {
            const section = home.querySelector('.verticalSection');
            if (section && section.parentElement) return section.parentElement;
        }
        // Jellyfin 12 Modern uses a React/MUI home view instead of .homePage.
        // Scope the fallback strictly to the Home route to avoid altering details/admin pages.
        if (/(?:^|[\/#?!])home(?:[\/#?&]|$)/i.test(location.pathname + location.hash)) {
            const main = Array.from(document.querySelectorAll('main, [role="main"]'))
                .find(el => visible(el) && el.querySelector('.MuiBox-root, .MuiStack-root, .MuiContainer-root'));
            if (main) return main.querySelector('.MuiContainer-root') || main;
        }
        return null;
    }
    function elem(tag, cls, text) {
        const el = document.createElement(tag);
        if (cls) el.className = cls;
        if (text !== undefined) el.textContent = T(text);
        return el;
    }
    function cssOnce() {
        if (document.getElementById('jf-stashdb-trending-styles')) return;
        const style = document.createElement('style');
        style.id = 'jf-stashdb-trending-styles';
        style.textContent = `
            #${SECTION_ID}{margin:1.5em 0 2em;min-width:0}
            #${SECTION_ID} .jf-sdt-header{display:flex;align-items:center;justify-content:space-between;gap:1em;padding:0 3.3%;margin-bottom:.8em}
            #${SECTION_ID} .jf-sdt-header h2{margin:0;font-size:1.45em;font-weight:600}
            .jf-sdt-action{background:transparent;border:0;color:var(--theme-primary-color,#64b5f6);cursor:pointer;font:inherit;padding:.45em;border-radius:5px}
            .jf-sdt-action:focus-visible,.jf-sdt-tile:focus-visible{outline:2px solid currentColor;outline-offset:3px}
            .jf-sdt-action:hover{text-decoration:underline}
            #${SECTION_ID} .jf-sdt-scroller{display:flex;overflow-x:auto;scroll-snap-type:x proximity;scroll-padding-inline:3.3%;gap:1rem;padding:.45em 3.3% 1em;scrollbar-width:thin}
            .jf-sdt-tile{position:relative;display:block;min-width:148px;width:clamp(148px,14vw,200px);flex-shrink:0;color:inherit;text-decoration:none;scroll-snap-align:start;cursor:pointer}
            .jf-sdt-poster{position:relative;aspect-ratio:2/3;background:#292929;border-radius:5px;overflow:hidden}
            .jf-sdt-poster img{width:100%;height:100%;object-fit:cover;display:block}
            /* Non-layout-shifting borders for the current Jellyfin user's video states. */
            .jf-sdt-tile.jf-video-state-favorite .jf-sdt-poster::after,
            .jf-sdt-tile.jf-video-state-played .jf-sdt-poster::after,
            .jf-sdt-tile.jf-video-state-both .jf-sdt-poster::after{
                content:"";position:absolute;inset:0;pointer-events:none;z-index:2;
                box-sizing:border-box;border:2px solid transparent;border-radius:inherit
            }
            .jf-sdt-tile.jf-video-state-favorite .jf-sdt-poster::after{border-color:rgba(236,139,145,.85)}
            .jf-sdt-tile.jf-video-state-played .jf-sdt-poster::after{border-color:rgba(134,187,241,.85)}
            .jf-sdt-tile.jf-video-state-both .jf-sdt-poster::after{
                border-top-color:rgba(236,139,145,.85);border-left-color:rgba(236,139,145,.85);
                border-bottom-color:rgba(134,187,241,.85);border-right-color:rgba(134,187,241,.85)
            }
            .jf-sdt-rank{z-index:3}

            .jf-sdt-rank{position:absolute;top:.4rem;left:.4rem;background:#161616df;color:#fff;border-radius:5px;padding:.24em .5em;font-size:.83em;font-weight:bold}
            .jf-sdt-info{padding:.3em .3em .5em;min-width:0;text-align:center}
            .jf-sdt-name,.jf-sdt-year{max-width:100%;overflow:hidden;white-space:nowrap;text-overflow:ellipsis;text-align:center}
            .jf-sdt-name{margin:0;padding:.24em 2px .06em;font-size:1em;line-height:1.35}
            .jf-sdt-year{margin:0;padding:.06em 2px;font-size:86%;line-height:1.35;color:var(--jf-palette-text-secondary,var(--text-secondary-color,#aaa))}
            .jf-sdt-message{padding:.65em 3.3%;opacity:.8}
            #${SECTION_ID} .jf-sdt-status{padding:0 3.3%;font-size:.85em;opacity:.78}
            #${SECTION_ID} .jf-sdt-status:empty{display:none}
            #${MODAL_ID}{position:fixed;inset:0;z-index:30000;background:rgba(0,0,0,.8);display:flex;align-items:center;justify-content:center;padding:2vw;box-sizing:border-box}
            #${MODAL_ID} .jf-sdt-panel{width:min(1100px,100%);height:min(90vh,950px);display:flex;flex-direction:column;overflow:hidden;background:var(--dialog-background,#1e1e1e);color:var(--text-color,#fff);border-radius:9px;box-shadow:0 12px 60px #000a}
            #${MODAL_ID} .jf-sdt-modalhead{display:flex;gap:1em;align-items:center;justify-content:space-between;padding:1em 1.2em;flex-wrap:wrap}
            #${MODAL_ID} .jf-sdt-modalhead h2{margin:0}
            #${MODAL_ID} select{padding:.6em;border-radius:5px;background:#333;color:#fff;border:1px solid #777}
            #${MODAL_ID} .jf-sdt-grid{overflow-y:auto;display:grid;grid-template-columns:repeat(auto-fill,minmax(145px,1fr));gap:1.5em 1em;padding:.5em 1.2em 1.5em;flex:1;align-content:start}
            #${MODAL_ID} .jf-sdt-tile{min-width:0;width:100%}
            #${MODAL_ID} .jf-sdt-footer{padding:.7em 1.2em;display:flex;align-items:center;gap:1em;justify-content:center}
            @media(max-width:600px){#${SECTION_ID} .jf-sdt-scroller{gap:.7rem} .jf-sdt-tile{min-width:125px;width:125px}}
        `;
        document.head.appendChild(style);
    }
    function imageDescriptor(item) {
        const images = val(item, 'imageTags', 'ImageTags', {}) || {};
        const primary = images.Primary || images.primary || val(item, 'primaryImageTag', 'PrimaryImageTag', '');
        const thumb = images.Thumb || images.thumb;
        const backdropTags = val(item, 'backdropImageTags', 'BackdropImageTags', []) || [];
        const backdrop = images.Backdrop || images.backdrop || (Array.isArray(backdropTags) ? backdropTags[0] : '');
        if (primary) return { type: 'Primary', tag: primary };
        if (thumb) return { type: 'Thumb', tag: thumb };
        if (backdrop) return { type: 'Backdrop', tag: backdrop, index: 0 };
        return null;
    }
    function imageUrl(item) {
        const api = apiClient();
        if (!api || !item) return '';
        const itemId = val(item, 'id', 'Id', '');
        const image = imageDescriptor(item);
        if (!itemId || !image) return '';
        // Jellyfin changes the image tag whenever an image is replaced. The tag makes
        // the URL unique so the browser never reuses the previous cover from cache.
        const options = { type: image.type, tag: image.tag, maxWidth: 350, quality: 85 };
        if (image.index !== undefined) options.index = image.index;
        try {
            if (typeof api.getScaledImageUrl === 'function') return api.getScaledImageUrl(itemId, options);
            const params = { tag: image.tag, maxWidth: 350, quality: 85 };
            if (image.index !== undefined) params.imageIndex = image.index;
            return api.getUrl('Items/' + encodeURIComponent(itemId) + '/Images/' + image.type, params);
        } catch (_) { return ''; }
    }
    function imageSignature(item) {
        const image = imageDescriptor(item);
        return image ? image.type + ':' + image.tag : '';
    }
    function detailsUrl(item) {
        const itemId = val(item, 'id', 'Id', '');
        // Uses Jellyfin's own details route, preserving any reverse-proxy path.
        return (location.hash.startsWith('#!') ? '#!/details?id=' : '#/details?id=') + encodeURIComponent(itemId);
    }
    function tile(item, rank) {
        const a = elem('a', 'jf-sdt-tile');
        a.href = detailsUrl(item);
        a.title = val(item, 'name', 'Name', 'Видео') + ' · StashDB #' + rank;
        const userData = val(item, 'userData', 'UserData', {}) || {};
        const favorite = !!val(userData, 'isFavorite', 'IsFavorite', false);
        const played = !!val(userData, 'played', 'Played', false);
        if (data.config && data.config.videoStateOutlines) {
            if (favorite && played) a.classList.add('jf-video-state-both');
            else if (favorite) a.classList.add('jf-video-state-favorite');
            else if (played) a.classList.add('jf-video-state-played');
        }
        const cover = elem('div', 'jf-sdt-poster');
        const img = imageUrl(item);
        if (img) {
            const poster = elem('img');
            poster.loading = 'lazy';
            poster.alt = '';
            poster.src = img;
            cover.appendChild(poster);
        }
        cover.appendChild(elem('span', 'jf-sdt-rank', '#' + rank));
        a.appendChild(cover);
        // Use Jellyfin's native title/secondary-text classes for the familiar
        // centered media-card appearance in both the home row and See All dialog.
        const info = elem('div', 'jf-sdt-info');
        info.appendChild(elem('div', 'jf-sdt-name cardText cardTextCentered cardText-first',
            val(item, 'name', 'Name', 'Без названия')));
        const year = Number(val(item, 'productionYear', 'ProductionYear', 0));
        if (Number.isInteger(year) && year >= 1800 && year <= 3000) {
            info.appendChild(elem('div', 'jf-sdt-year cardText cardTextCentered cardText-secondary', year));
        }
        a.appendChild(info);
        return a;
    }
    async function loadPrepared(generation) {
        // This request ONLY reads the cached index; it never starts a StashDB query
        // or traverses a library. The server filters item IDs by logged-in user.
        const prepared = await fetchJson('JFToStashSync/TrendingPrepared');
        if (generation !== data.generation) return;
        const state = val(prepared, 'status', 'Status', 'not_ready');
        if (state !== 'ready') {
            data.status = String(val(prepared, 'message', 'Message',
                T('Индекс Trending пока не построен. Запустите запланированную задачу Jellyfin.')));
            data.expires = Date.now() + 60000;
            return;
        }
        const rows = val(prepared, 'entries', 'Entries', []);
        if (!Array.isArray(rows)) throw new Error(T('Некорректный ответ сохранённого индекса Trending.'));
        data.updatedAt = String(val(prepared, 'updatedAt', 'UpdatedAt', ''));
        data.entries.clear();
        data.byScene.clear();
        for (const row of rows) {
            const itemId = id(val(row, 'itemId', 'ItemId', ''));
            const rank = Number(val(row, 'rank', 'Rank', 0));
            if (itemId && rank > 0 && !data.entries.has(itemId)) data.entries.set(itemId, rank);
        }
        const ids = Array.from(data.entries.keys());
        if (!ids.length) {
            data.status = T('Индекс обновлён. В доступных вам библиотеках нет совпадений с StashDB Trending.');
            data.expires = Date.now() + CACHE_MS;
            return;
        }
        data.status = 'Загрузка карточек: 0 из ' + ids.length;
        paint();
        // Two bounded in-flight item batches rather than waiting serially.
        // Results remain ranked by the server-provided prepared index.
        for (let offset = 0; offset < ids.length; offset += BATCH_SIZE * 2) {
            if (generation !== data.generation) return;
            const slices = [ids.slice(offset, offset+BATCH_SIZE), ids.slice(offset+BATCH_SIZE, offset+BATCH_SIZE*2)]
                .filter(batch => batch.length);
            const pages = await Promise.all(slices.map(async batch => {
                const params = new URLSearchParams({
                    UserId:data.userId, Ids:batch.join(','),
                    EnableUserData:'true', EnableImages:'true', Limit:String(batch.length)
                });
                return fetchJson('Items?' + params);
            }));
            if (generation !== data.generation) return;
            for (const payload of pages) {
                const items = val(payload, 'items', 'Items', []);
                if (!Array.isArray(items)) throw new Error('Некорректный ответ Jellyfin /Items.');
                for (const item of items) {
                    const itemId = id(val(item, 'id', 'Id', ''));
                    if (data.entries.has(itemId)) data.byScene.set(itemId, [item]);
                }
            }
            data.status = 'Карточек загружено: ' + Math.min(offset + BATCH_SIZE*2, ids.length) + ' из ' + ids.length;
            paint();
        }
        // Successful refresh: don't display internal index counts/timestamps on the home row.
        data.status = '';
        data.expires = Date.now() + CACHE_MS;
    }
    function matched() {
        const results = [];
        for (const [itemId, rank] of data.entries) {
            for (const item of (data.byScene.get(itemId) || [])) results.push({ item, rank });
        }
        return results.sort((a, b) => a.rank - b.rank);
    }
    function makeSection() {
        const section = elem('section', 'verticalSection');
        section.id = SECTION_ID;
        const header = elem('div', 'jf-sdt-header');
        header.appendChild(elem('h2', '', '🔥 StashDB Trending'));
        const seeAll = elem('button', 'jf-sdt-action', T('Показать все ›'));
        seeAll.type = 'button';
        seeAll.addEventListener('click', openModal);
        header.appendChild(seeAll);
        section.appendChild(header);
        const status = elem('div', 'jf-sdt-status');
        status.setAttribute('role', 'status');
        status.setAttribute('aria-live', 'polite');
        section.appendChild(status);
        section.appendChild(elem('div', 'jf-sdt-scroller'));
        return section;
    }
    function paint() {
        const container = root();
        if (!container || !data.config || !data.config.enabled) return;
        cssOnce();
        let section = document.getElementById(SECTION_ID);
        if (!section || section.parentElement !== container) {
            if (section) section.remove();
            section = makeSection();
            container.insertBefore(section, container.firstElementChild);
        }
        const scroll = section.querySelector('.jf-sdt-scroller');
        if (!scroll) return;
        const results = matched();
        const status = section.querySelector('.jf-sdt-status');
        if (status) status.textContent = data.error || data.status || (results.length ? '' : T('Нет данных Trending.'));
        const notice = data.error || data.status || T('Совпадений нет. Запустите задачу обновления индекса Trending в Jellyfin.');
        const signature = results.map(row => id(val(row.item, 'id', 'Id', '')) + ':' + row.rank + ':' +
            imageSignature(row.item) + ':' + val(row.item, 'name', 'Name', '') + ':' +
            val(row.item, 'productionYear', 'ProductionYear', '') + ':' +
            val(val(row.item, 'userData', 'UserData', {}), 'isFavorite', 'IsFavorite', false) + ':' +
            val(val(row.item, 'userData', 'UserData', {}), 'played', 'Played', false)).join('|') +
            (results.length ? '' : notice);
        if (section.dataset.signature !== signature) {
            section.dataset.signature = signature;
            const previous = scroll.scrollLeft;
            scroll.replaceChildren();
            results.slice(0, 24).forEach(row => scroll.appendChild(tile(row.item, row.rank)));
            if (!results.length) scroll.appendChild(elem('div', 'jf-sdt-message', notice));
            scroll.scrollLeft = previous;
            if (activeModal) paintModal();
        }
        section.querySelector('.jf-sdt-action').hidden = !results.length;
    }
    async function ensureData(force) {
        if (data.running) return data.running;
        if (!force && Date.now() < data.expires) { paint(); return; }
        const generation = ++data.generation;
        data.running = (async () => {
            data.error = '';
            data.status = T('Получение готового индекса Trending…');
            data.entries.clear();
            data.byScene.clear();
            try {
                await loadPrepared(generation);
                if (generation === data.generation) {
                    console.info('[JFToStashSync] Trending prepared: ' + matched().length + ' matched Jellyfin items.');
                }
            } catch (error) {
                if (generation !== data.generation) return;
                data.error = 'StashDB Trending: ' + (error && error.message || T('ошибка загрузки'));
                data.status = '';
                data.expires = Date.now() + 30000;
                console.warn('[JFToStashSync] ' + data.error, error);
            } finally {
                data.running = null;
                paint();
                window.__jfStashHomeLoader?.mark('trending');
            }
        })();
        paint();
        return data.running;
    }
    function matchesFilter(row, filter) {
        const userData = val(row.item, 'userData', 'UserData', {}) || {};
        const played = !!val(userData, 'played', 'Played', false);
        const fav = !!val(userData, 'isFavorite', 'IsFavorite', false);
        return filter === 'all' || (filter === 'played' && played) ||
            (filter === 'unplayed' && !played) || (filter === 'favorite' && fav);
    }
    function paintModal() {
        if (!activeModal || !activeModal.isConnected) return;
        const grid = activeModal.querySelector('.jf-sdt-grid');
        const select = activeModal.querySelector('select');
        const footer = activeModal.querySelector('.jf-sdt-footer');
        const filter = select.value;
        const rows = matched().filter(row => matchesFilter(row, filter));
        grid.replaceChildren(...rows.map(row => tile(row.item, row.rank)));
        if (!rows.length) grid.appendChild(elem('div', 'jf-sdt-message', data.error || T('Нет подходящих видео среди загруженных Trending.')));
        footer.replaceChildren();
        const counts = elem('span', '', T('Показано: ') + rows.length + T(' · Из подготовленного индекса'));
        footer.appendChild(counts);
    }
    function closeModal() {
        if (activeModal) activeModal.remove();
        activeModal = null;
        document.removeEventListener('keydown', onEscape);
    }
    function onEscape(event) { if (event.key === 'Escape') closeModal(); }
    function openModal() {
        if (activeModal) return;
        cssOnce();
        const overlay = elem('div');
        overlay.id = MODAL_ID;
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.setAttribute('aria-label', 'StashDB Trending');
        overlay.addEventListener('click', e => { if (e.target === overlay) closeModal(); });
        const panel = elem('div', 'jf-sdt-panel');
        const header = elem('div', 'jf-sdt-modalhead');
        header.appendChild(elem('h2', '', '🔥 StashDB Trending'));
        const select = elem('select');
        [['all', 'Все'], ['unplayed', 'Не просмотрено'], ['played', 'Просмотрено'], ['favorite', 'Избранное']]
            .forEach(([value, title]) => {
                const option = elem('option', '', title);
                option.value = value;
                select.appendChild(option);
            });
        select.setAttribute('aria-label', T('Фильтр видео'));
        select.addEventListener('change', paintModal);
        header.appendChild(select);
        const close = elem('button', 'jf-sdt-action', T('✕ Закрыть'));
        close.type = 'button';
        close.addEventListener('click', closeModal);
        header.appendChild(close);
        panel.appendChild(header);
        panel.appendChild(elem('div', 'jf-sdt-grid'));
        panel.appendChild(elem('div', 'jf-sdt-footer'));
        overlay.appendChild(panel);
        document.body.appendChild(overlay);
        activeModal = overlay;
        document.addEventListener('keydown', onEscape);
        paintModal();
        close.focus();
    }
    async function prepare() {
        if (configPending || !currentUserId()) return;
        const user = currentUserId();
        if (data.config && data.userId === user) return;
        configPending = true;
        try {
            const config = await (window.__jfHomeWarmup?.getConfig?.() || fetchJson('JFToStashSync/PlayerOCounterConfig'));
            // An explicit server-side opt-in; default OFF.
            const enabled = val(config, 'trendingEnabled', 'TrendingEnabled', false) === true;
            if (data.userId !== user) {
                ++data.generation;
                data.byScene.clear();
                data.entries.clear();
                data.expires = 0;
                data.status = '';
                const oldSection = document.getElementById(SECTION_ID);
                if (oldSection) oldSection.remove();
                closeModal();
            }
            data.userId = user;
            data.config = {
                enabled,
                videoStateOutlines: val(config, 'highlightRankedVideoStates', 'HighlightRankedVideoStates', false) === true,
                libraryId: String(val(config, 'trendingLibraryId', 'TrendingLibraryId', '') || ''),
                pages: Math.max(1, Math.min(10, Number(val(config, 'trendingPages', 'TrendingPages', 5)) || 5))
            };
            data.expires = 0;
            if (enabled) ensureData(false); // Warm authenticated index/cards before the first Home visit.
        } catch (error) {
            console.warn('[JFToStashSync] Cannot load Trending configuration.', error);
        } finally { configPending = false; }
    }
    function tick() {
        // A background tab is not a Jellyfin SPA navigation. Preserve cached rows.
        if (document.hidden) return;
        if (!data.config || data.userId !== currentUserId()) { prepare(); return; }
        if (!data.config.enabled) return;
        if (!root()) {
            // Favorites is a different Home tab but shares its DOM host.
            // Remove injected UI there, without invalidating prepared data.
            document.getElementById(SECTION_ID)?.remove();

            wasOnHome = false;
            if (activeModal) closeModal();
            return;
        }
        wasOnHome = true;
        if (mountPending) return;
        mountPending = true;
        window.requestAnimationFrame(() => {
            mountPending = false;
            paint();
            if (!data.running && Date.now() >= data.expires) ensureData(false);
        });
    }
    function start() {
        tick();
        document.addEventListener('jf-to-stash-user-ready', tick);
        window.addEventListener('hashchange', tick);
        window.addEventListener('popstate', tick);
        document.addEventListener('viewshow', tick, true);
        document.addEventListener('pageshow', tick, true);
        // Resuming a browser tab must NOT invalidate the prepared cards.
        // SPA navigation preserves prepared client state; freshness TTL still applies.
        document.addEventListener('visibilitychange', () => {
            if (!document.hidden) tick();
        });
        // Jellyfin 12 has multiple SPA home layouts. Re-check lightly as they mount/unmount.
        window.setInterval(tick, 2000);
        console.info('[JFToStashSync] StashDB Trending v1.2.0.15 home row initialized.');
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
}());
