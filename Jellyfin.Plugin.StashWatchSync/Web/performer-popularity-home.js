/* JF To Stash Sync — prepared performer Popularity by gender, Jellyfin 12 */
(function () {
    'use strict';
    const T = (text) => window.__jfToStashI18n?.t(text) ?? String(text ?? '');
    if (window.__jfToStashPerformerPopularityLoaded) return;
    window.__jfToStashPerformerPopularityLoaded = true;

    const PREFIX = 'jf-spp-';
    const ROW_IDS = ['FEMALE', 'INTERSEX', 'MALE', 'NON_BINARY', 'TRANSGENDER_FEMALE', 'TRANSGENDER_MALE'];
    const LABELS = {
        FEMALE: 'Female', INTERSEX: 'Intersex', MALE: 'Male', NON_BINARY: 'Non-binary',
        TRANSGENDER_FEMALE: 'Transgender female', TRANSGENDER_MALE: 'Transgender male'
    };
    const BATCH_SIZE = 60;
    const FAVORITES_PAGE_SIZE = 200;
    const FAVORITES_REFRESH_MS = 30000;
    // Only actual Person portraits are eligible. A video/movie cover with cardImageContainer alone never is.
    const NATIVE_PERSON_PORTRAITS = [
        'a.cardImageContainer.birthage-container',
        '.personCard .cardImageContainer',
        '.card[data-type="Person"] .cardImageContainer',
        '.card[data-itemtype="Person"] .cardImageContainer',
        '.card[data-item-type="Person"] .cardImageContainer',
        '.peopleSection .cardImageContainer',
        '.peopleContainer .cardImageContainer',
        '.personItemsContainer .cardImageContainer'
    ].join(',');
    const CACHE_MS = 10 * 60 * 1000; // browser-only, not the remote StashDB ranking cache
    const state = {
        userId: '', enabled: false, outlineEnabled: false, genders: [], entries: new Map(), people: new Map(),
        favorites: new Set(), favoritesExpires: 0, favoritesRunning: null, favoritesGeneration: 0,
        expires: 0, running: null, error: '', status: '', updatedAt: '', generation: 0
    };
    let configPending = false;
    let mountPending = false;
    let wasOnHome = false;
    let activeModal = null;
    let favoriteRefreshTimer = null;

    // Read-only check shared with the featured banner. The existing favorite
    // refresh loop owns this state; the banner must not query /Persons again.
    window.__jfToStashIsFavoriteActor = (personId, expectedUserId) =>
        state.outlineEnabled && !!expectedUserId &&
        expectedUserId === state.userId &&
        state.favorites.has(normalizedId(personId));

    function notifyFavoritesChanged() {
        if (typeof document.dispatchEvent === 'function' && typeof Event === 'function')
            document.dispatchEvent(new Event('jf-to-stash-favorites-updated'));
    }

    function apiClient() { return window.ApiClient; }
    function val(obj, camel, pascal, fallback) {
        return obj && obj[camel] !== undefined ? obj[camel] :
            obj && obj[pascal] !== undefined ? obj[pascal] : fallback;
    }
    function userId() {
        try { return apiClient() && apiClient().getCurrentUserId() || ''; }
        catch (_) { return ''; }
    }
    function normalizedId(value) { return String(value || '').replace(/-/g, '').toLowerCase(); }
    async function fetchJson(path) {
        const api = apiClient();
        if (!api || !api.fetch || !api.getUrl) throw new Error(T('Jellyfin API недоступен.'));
        let timer;
        const request = (async () => {
            const response = await api.fetch({ url: api.getUrl(path), type: 'GET' });
            let body = null;
            try { body = await response.json(); } catch (_) { /* may not contain JSON */ }
            if (!response.ok) throw new Error((body && (body.message || body.Message)) || 'Jellyfin HTTP ' + response.status);
            return body;
        })();
        const timeout = new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error('Превышено время ожидания ответа Jellyfin (25 с).')), 25000);
        });
        try { return await Promise.race([request, timeout]); }
        finally { clearTimeout(timer); }
    }
    function visible(el) {
        return !!(el && el.isConnected && el.getClientRects().length &&
            getComputedStyle(el).visibility !== 'hidden' && getComputedStyle(el).display !== 'none');
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
        for (const selector of ['.homeSectionsContainer', '.homePage .homeSections',
            '.homePage .sectionsContainer', '.homeView .homeSectionsContainer']) {
            const found = Array.from(document.querySelectorAll(selector)).find(visible);
            if (found) return found;
        }
        const home = Array.from(document.querySelectorAll('.homePage, .homeView'))
            .find(el => visible(el) && el.querySelector('.verticalSection'));
        if (home) return home.querySelector('.verticalSection').parentElement;
        if (/(?:^|[\/#?!])home(?:[\/#?&]|$)/i.test(location.pathname + location.hash)) {
            const main = Array.from(document.querySelectorAll('main, [role="main"]'))
                .find(el => visible(el) && el.querySelector('.MuiBox-root, .MuiStack-root, .MuiContainer-root'));
            if (main) return main.querySelector('.MuiContainer-root') || main;
        }
        return null;
    }
    function el(tag, cls, value) {
        const node = document.createElement(tag);
        if (cls) node.className = cls;
        if (value !== undefined) node.textContent = T(value);
        return node;
    }
    function stylesOnce() {
        if (document.getElementById(PREFIX + 'styles')) return;
        const style = el('style');
        style.id = PREFIX + 'styles';
        style.textContent = `
          .jf-spp-section{margin:1.5em 0 2em;min-width:0}
          .jf-spp-section .jf-spp-header{display:flex;align-items:center;justify-content:space-between;gap:1em;padding:0 3.3%;margin-bottom:.8em}
          .jf-spp-section .jf-spp-header h2{margin:0;font-size:1.45em;font-weight:600}
          .jf-spp-action{background:transparent;border:0;color:var(--theme-primary-color,#64b5f6);cursor:pointer;font:inherit;padding:.45em;border-radius:5px}
          .jf-spp-action:hover{text-decoration:underline}
          .jf-spp-action:focus-visible,.jf-spp-tile:focus-visible{outline:2px solid currentColor;outline-offset:3px}
          .jf-spp-section .jf-spp-scroller{display:flex;overflow-x:auto;scroll-snap-type:x proximity;scroll-padding-inline:3.3%;gap:1rem;padding:.45em 3.3% 1em;scrollbar-width:thin}
          .jf-spp-tile{position:relative;display:block;min-width:148px;width:clamp(148px,14vw,200px);flex-shrink:0;color:inherit;text-decoration:none;scroll-snap-align:start;cursor:pointer}
          .jf-spp-portrait{position:relative;aspect-ratio:2/3;background:#292929;border-radius:5px;overflow:hidden}
          .jf-spp-portrait img{width:100%;height:100%;object-fit:cover;display:block}
          .jf-spp-rank{position:absolute;top:.4rem;left:.4rem;background:#161616df;color:#fff;border-radius:5px;padding:.24em .5em;font-size:.83em;font-weight:bold}
          .jf-spp-tile .jf-spp-info{display:block;width:100%;box-sizing:border-box;padding:.3em .3em .5em;min-width:0;text-align:center}
          .jf-spp-tile .jf-spp-name{display:block;width:100%;box-sizing:border-box;margin:0 auto;padding:.24em 2px .06em;max-width:100%;overflow:hidden;white-space:nowrap;text-overflow:ellipsis;text-align:center;font-size:1em;line-height:1.35}
          /* Only favorites of the currently signed-in Jellyfin user receive a border. */
          html.jf-performer-outline-on .jf-spp-favorite-native{
            outline:2px solid rgba(236,139,145,.8) !important;outline-offset:-3px
          }
          html.jf-performer-outline-on .jf-spp-tile.jf-spp-favorite .jf-spp-portrait::after{
            content:"";position:absolute;inset:0;box-sizing:border-box;
            border:2px solid rgba(236,139,145,.8);border-radius:inherit;
            pointer-events:none;z-index:2
          }
          .jf-spp-section .jf-spp-status{padding:0 3.3%;font-size:.85em;opacity:.78}
          .jf-spp-section .jf-spp-status:empty{display:none}
          .jf-spp-message{padding:.65em 3.3%;opacity:.8}
          #jf-spp-modal{position:fixed;inset:0;z-index:30000;background:rgba(0,0,0,.8);display:flex;align-items:center;justify-content:center;padding:2vw;box-sizing:border-box}
          #jf-spp-modal .jf-spp-panel{width:min(1100px,100%);height:min(90vh,950px);display:flex;flex-direction:column;overflow:hidden;background:var(--dialog-background,#1e1e1e);color:var(--text-color,#fff);border-radius:9px;box-shadow:0 12px 60px #000a}
          #jf-spp-modal .jf-spp-modalhead{display:flex;align-items:center;justify-content:space-between;gap:1em;padding:1em 1.2em}
          #jf-spp-modal .jf-spp-modalhead h2{margin:0}
          #jf-spp-modal .jf-spp-grid{overflow-y:auto;display:grid;grid-template-columns:repeat(auto-fill,minmax(145px,1fr));gap:1.5em 1em;padding:.5em 1.2em 1.5em;flex:1;align-content:start}
          #jf-spp-modal .jf-spp-tile{min-width:0;width:100%}
          @media(max-width:600px){.jf-spp-section .jf-spp-scroller{gap:.7rem}.jf-spp-tile{min-width:125px;width:125px}}
        `;
        document.head.appendChild(style);
    }
    function imageDescriptor(person) {
        const images = val(person, 'imageTags', 'ImageTags', {}) || {};
        const primary = images.Primary || images.primary || val(person, 'primaryImageTag', 'PrimaryImageTag', '');
        const thumb = images.Thumb || images.thumb;
        return primary ? { type: 'Primary', tag: primary } : thumb ? { type: 'Thumb', tag: thumb } : null;
    }
    function imageUrl(person) {
        const api = apiClient();
        if (!api || !person) return '';
        const pid = val(person, 'id', 'Id', '');
        const image = imageDescriptor(person);
        if (!pid || !image) return '';
        const options = { type: image.type, tag: image.tag, maxWidth: 350, quality: 85 };
        try {
            if (api.getScaledImageUrl) return api.getScaledImageUrl(pid, options);
            return api.getUrl('Items/' + encodeURIComponent(pid) + '/Images/' + image.type,
                { tag: image.tag, maxWidth: 350, quality: 85 });
        } catch (_) { return ''; }
    }
    function imageSignature(person) {
        const image = imageDescriptor(person);
        return image ? image.type + ':' + image.tag : '';
    }
    function tile(item, rank) {
        const a = el('a', 'jf-spp-tile');
        const pid = val(item, 'id', 'Id', '');
        const name = val(item, 'name', 'Name', 'Актёр');
        a.href = (location.hash.startsWith('#!') ? '#!/details?id=' : '#/details?id=') + encodeURIComponent(pid);
        a.title = name + ' · StashDB #' + rank;
        a.dataset.personId = normalizedId(pid);
        if (state.outlineEnabled && state.favorites.has(a.dataset.personId)) a.classList.add('jf-spp-favorite');
        const cover = el('div', 'jf-spp-portrait');
        const url = imageUrl(item);
        if (url) {
            const img = el('img');
            img.loading = 'lazy';
            img.alt = '';
            img.src = url;
            cover.appendChild(img);
        }
        cover.appendChild(el('span', 'jf-spp-rank', '#' + rank));
        a.appendChild(cover);
        const info = el('div', 'jf-spp-info');
        info.appendChild(el('div', 'jf-spp-name', name));
        a.appendChild(info);
        return a;
    }
    function entries(gender) {
        const results = [];
        for (const row of state.entries.get(gender) || []) {
            const person = state.people.get(row.id);
            if (person) results.push({ item: person, rank: row.rank });
        }
        return results.sort((a, b) => a.rank - b.rank);
    }
    function favoritePersonId(card) {
        // Prefer the actual details URL over DOM metadata, since other card types use data-id too.
        const anchor = card.matches && card.matches('a[href]') ? card :
            card.closest && card.closest('a[href]') || card.querySelector && card.querySelector('a[href]');
        const href = anchor && anchor.getAttribute && anchor.getAttribute('href') || '';
        const match = /(?:[?&]id=)([a-f0-9-]{32,36})(?=[&#]|$)/i.exec(href);
        return match ? normalizedId(match[1]) : '';
    }
    function updateFavoriteOutlines() {
        if (!state.outlineEnabled) return;
        // Cards inserted by Jellyfin's SPA are re-evaluated without scanning the library.
        document.querySelectorAll(NATIVE_PERSON_PORTRAITS).forEach(card => {
            card.classList.toggle('jf-spp-favorite-native', state.favorites.has(favoritePersonId(card)));
        });
        document.querySelectorAll('.jf-spp-tile[data-person-id]').forEach(card => {
            card.classList.toggle('jf-spp-favorite', state.favorites.has(card.dataset.personId));
        });
    }
    function clearFavoriteOutlines() {
        document.querySelectorAll('.jf-spp-favorite-native').forEach(card => card.classList.remove('jf-spp-favorite-native'));
        document.querySelectorAll('.jf-spp-tile.jf-spp-favorite').forEach(card => card.classList.remove('jf-spp-favorite'));
        notifyFavoritesChanged();
    }
    async function loadFavorites(current, generation) {
        const found = new Set();
        // /Persons with IsFavorite filters on the Jellyfin server; we never download all actors.
        for (let offset = 0; ; offset += FAVORITES_PAGE_SIZE) {
            const params = new URLSearchParams({
                UserId: current, IsFavorite: 'true', EnableUserData: 'true',
                Limit: String(FAVORITES_PAGE_SIZE), StartIndex: String(offset)
            });
            const result = await fetchJson('Persons?' + params);
            if (generation !== state.favoritesGeneration || current !== userId()) return;
            const items = val(result, 'items', 'Items', []);
            if (!Array.isArray(items)) throw new Error('Некорректный список избранных актёров Jellyfin.');
            for (const person of items) {
                // Do not highlight an item merely because it was returned by another query.
                const kind = String(val(person, 'type', 'Type', 'Person')).toLowerCase();
                const data = val(person, 'userData', 'UserData', {});
                const favorite = val(data, 'isFavorite', 'IsFavorite', undefined);
                const id = normalizedId(val(person, 'id', 'Id', ''));
                if (kind === 'person' && /^[a-f0-9]{32}$/.test(id) && favorite !== false) found.add(id);
            }
            const total = Number(val(result, 'totalRecordCount', 'TotalRecordCount', NaN));
            if (items.length < FAVORITES_PAGE_SIZE || Number.isFinite(total) && offset + items.length >= total) break;
            if (offset >= 20000) throw new Error('Слишком большой список избранных актёров.');
        }
        if (generation === state.favoritesGeneration && current === state.userId && current === userId()) {
            state.favorites = found;
            state.favoritesExpires = Date.now() + FAVORITES_REFRESH_MS;
            updateFavoriteOutlines();
            notifyFavoritesChanged();
        }
    }
    function refreshFavorites(force) {
        if (!state.outlineEnabled || !state.userId || state.userId !== userId()) return;
        if (state.favoritesRunning) return state.favoritesRunning;
        if (!force && Date.now() < state.favoritesExpires) return;
        const generation = ++state.favoritesGeneration;
        const current = state.userId;
        const request = (async () => {
            try { await loadFavorites(current, generation); }
            catch (error) {
                if (generation !== state.favoritesGeneration || current !== state.userId) return;
                // Fail closed: an unavailable API must not mark non-confirmed people as favorites.
                state.favorites.clear();
                state.favoritesExpires = Date.now() + 15000;
                updateFavoriteOutlines();
                notifyFavoritesChanged();
                console.warn('[JFToStashSync] Cannot check Jellyfin favorite actors.', error);
            }
        })();
        state.favoritesRunning = request;
        request.finally(() => {
            if (state.favoritesRunning === request) state.favoritesRunning = null;
        });
        return request;
    }
    function requestFavoritesRefresh(delay) {
        if (!state.outlineEnabled || !state.userId) return;
        if (favoriteRefreshTimer) clearTimeout(favoriteRefreshTimer);
        favoriteRefreshTimer = setTimeout(() => {
            favoriteRefreshTimer = null;
            state.favoritesExpires = 0;
            refreshFavorites(true);
        }, delay);
    }
    function onFavoriteInteraction(event) {
        if (!state.outlineEnabled || !event.target || !event.target.closest) return;
        const control = event.target.closest('button, [role="button"], [data-action]');
        if (!control) return;
        const hint = [control.getAttribute('aria-label'), control.getAttribute('title'),
            control.getAttribute('data-action'), control.className && String(control.className)]
            .filter(Boolean).join(' ').toLowerCase();
        if (/favou?rite|избран|btnuserdata|markfavorite|togglefavorite/.test(hint)) {
            requestFavoritesRefresh(1200);
        }
    }
    function removeRows() {
        document.querySelectorAll('[data-jf-spp-row="true"]').forEach(node => node.remove());
        closeModal();
    }
    function makeRow(gender) {
        const section = el('section', 'verticalSection jf-spp-section');
        section.id = PREFIX + gender.toLowerCase().replace(/_/g, '-');
        section.dataset.jfSppRow = 'true';
        const header = el('div', 'jf-spp-header');
        header.appendChild(el('h2', '', T('⭐ StashDB Performer Popularity · ') + T(LABELS[gender])));
        const seeAll = el('button', 'jf-spp-action', T('Показать все ›'));
        seeAll.type = 'button';
        seeAll.addEventListener('click', () => openModal(gender));
        header.appendChild(seeAll);
        section.appendChild(header);
        const status = el('div', 'jf-spp-status');
        status.setAttribute('role', 'status');
        section.appendChild(status);
        section.appendChild(el('div', 'jf-spp-scroller'));
        return section;
    }
    function paint() {
        const host = root();
        if (!host || !state.enabled || !state.genders.length) return;
        stylesOnce();
        const target = document.getElementById('jf-stashdb-popularity-section');
        let insertion = target && target.parentElement === host ? target.nextSibling : host.firstChild;
        for (const gender of state.genders) {
            const rowId = PREFIX + gender.toLowerCase().replace(/_/g, '-');
            let section = document.getElementById(rowId);
            if (!section || section.parentElement !== host) {
                if (section) section.remove();
                section = makeRow(gender);
            }
            if (section !== insertion) host.insertBefore(section, insertion);
            // The next gender is inserted after the current section.
            insertion = section.nextSibling;
            const rows = entries(gender);
            const status = section.querySelector('.jf-spp-status');
            if (status) status.textContent = state.error || state.status;
            const scroller = section.querySelector('.jf-spp-scroller');
            const notice = state.error || state.status || T('Нет совпадающих актёров. Обновите индекс Performer Popularity в запланированных задачах.');
            const signature = rows.map(x => normalizedId(val(x.item, 'id', 'Id', '')) + ':' + x.rank + ':' +
                imageSignature(x.item) + ':' + val(x.item, 'name', 'Name', '')).join('|') + (rows.length ? '' : notice);
            if (section.dataset.signature !== signature) {
                const oldScroll = scroller.scrollLeft;
                scroller.replaceChildren();
                rows.slice(0, 24).forEach(x => scroller.appendChild(tile(x.item, x.rank)));
                if (!rows.length) scroller.appendChild(el('div', 'jf-spp-message', notice));
                section.dataset.signature = signature;
                scroller.scrollLeft = oldScroll;
                // Keep "Показать все" consistent if a portrait changes while the dialog is open.
                if (activeModal && activeModal.dataset.gender === gender) {
                    const grid = activeModal.querySelector('.jf-spp-grid');
                    if (grid) {
                        const scrollTop = grid.scrollTop;
                        grid.replaceChildren(...rows.map(x => tile(x.item, x.rank)));
                        if (!rows.length) grid.appendChild(el('div', 'jf-spp-message', T('Нет актёров для этой категории.')));
                        grid.scrollTop = scrollTop;
                    }
                }
            }
            section.querySelector('.jf-spp-action').hidden = !rows.length;
        }
    }
    async function loadPrepared(generation) {
        const result = await fetchJson('JFToStashSync/PerformerPopularityPrepared');
        if (generation !== state.generation) return;
        const status = val(result, 'status', 'Status', '');
        if (status !== 'ready') {
            state.status = val(result, 'message', 'Message', T('Нет подготовленного индекса актёров.'));
            state.expires = Date.now() + 30000;
            return;
        }
        state.updatedAt = val(result, 'updatedAt', 'UpdatedAt', '');
        const rows = val(result, 'entries', 'Entries', []);
        if (!Array.isArray(rows)) throw new Error('Некорректный индекс актёров.');
        const idSet = new Set();
        for (const gender of state.genders) state.entries.set(gender, []);
        for (const row of rows) {
            const gender = val(row, 'gender', 'Gender', '');
            const id = normalizedId(val(row, 'personId', 'PersonId', ''));
            const rank = Number(val(row, 'rank', 'Rank', 0));
            if (!state.entries.has(gender) || !/^[a-f0-9]{32}$/.test(id) || !Number.isInteger(rank) || rank <= 0) continue;
            state.entries.get(gender).push({ id, rank });
            idSet.add(id);
        }
        const ids = Array.from(idSet);
        for (let offset = 0; offset < ids.length; offset += BATCH_SIZE * 2) {
            if (generation !== state.generation) return;
            const batches = [ids.slice(offset,offset+BATCH_SIZE),ids.slice(offset+BATCH_SIZE,offset+BATCH_SIZE*2)]
                .filter(x=>x.length);
            const pages = await Promise.all(batches.map(async batch => {
                const params = new URLSearchParams({ UserId:state.userId, Ids:batch.join(','),
                    EnableImages:'true', EnableUserData:'true', Limit:String(batch.length) });
                return fetchJson('Items?' + params);
            }));
            if (generation !== state.generation) return;
            for (const data of pages) {
                const people = val(data,'items','Items',[]);
                if (!Array.isArray(people)) throw new Error('Некорректные метаданные актёров.');
                for (const person of people) {
                    const pid = normalizedId(val(person,'id','Id',''));
                    if (idSet.has(pid) && String(val(person,'type','Type','')).toLowerCase()==='person')
                        state.people.set(pid,person);
                }
            }
            state.status = T('Загружены карточки актёров: ') + Math.min(offset+BATCH_SIZE*2,ids.length) + T(' из ') + ids.length;
            paint();
        }
        // The prepared-index statistics belong in diagnostics, not on the home page.
        state.status = '';
        state.expires = Date.now() + CACHE_MS;
    }
    async function ensureData() {
        if (state.running || Date.now() < state.expires) return state.running;
        const generation = ++state.generation;
        state.running = (async () => {
            state.error = '';
            state.status = T('Загрузка готового индекса популярности актёров…');
            state.entries.clear();
            state.people.clear();
            try { await loadPrepared(generation); }
            catch (error) {
                if (generation !== state.generation) return;
                state.error = 'Performer Popularity: ' + (error && error.message || T('ошибка загрузки'));
                state.status = '';
                state.expires = Date.now() + 30000;
                console.warn('[JFToStashSync] ' + state.error, error);
            } finally {
                state.running = null;
                paint();
                window.__jfStashHomeLoader?.mark('performers');
            }
        })();
        paint();
        return state.running;
    }
    function closeModal() {
        if (activeModal) activeModal.remove();
        activeModal = null;
        document.removeEventListener('keydown', escape);
    }
    function escape(event) { if (event.key === 'Escape') closeModal(); }
    function openModal(gender) {
        if (activeModal) return;
        stylesOnce();
        const overlay = el('div');
        overlay.id = PREFIX + 'modal';
        overlay.dataset.gender = gender;
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.setAttribute('aria-label', T('⭐ Performer Popularity · ') + T(LABELS[gender]));
        overlay.addEventListener('click', e => { if (e.target === overlay) closeModal(); });
        const panel = el('div', 'jf-spp-panel');
        const header = el('div', 'jf-spp-modalhead');
        header.appendChild(el('h2', '', T('⭐ Performer Popularity · ') + T(LABELS[gender])));
        const close = el('button', 'jf-spp-action', T('✕ Закрыть'));
        close.type = 'button';
        close.addEventListener('click', closeModal);
        header.appendChild(close);
        panel.appendChild(header);
        const grid = el('div', 'jf-spp-grid');
        const rows = entries(gender);
        rows.forEach(x => grid.appendChild(tile(x.item, x.rank)));
        if (!rows.length) grid.appendChild(el('div', 'jf-spp-message', T('Нет актёров для этой категории.')));
        panel.appendChild(grid);
        overlay.appendChild(panel);
        document.body.appendChild(overlay);
        activeModal = overlay;
        document.addEventListener('keydown', escape);
        close.focus();
    }
    async function prepare() {
        if (configPending || !userId()) return;
        const current = userId();
        if (state.userId === current && state.configured) return;
        configPending = true;
        try {
            const result = await (window.__jfHomeWarmup?.getConfig?.() || fetchJson('JFToStashSync/PlayerOCounterConfig'));
            if (current !== userId()) return;
            const enabled = val(result, 'performerPopularityEnabled', 'PerformerPopularityEnabled', false) === true;
            const outlineEnabled = val(result, 'highlightActorPortraits', 'HighlightActorPortraits', false) === true;
            const genders = val(result, 'performerPopularityGenders', 'PerformerPopularityGenders', []) || [];
            if (state.userId !== current) {
                document.documentElement.classList.remove('jf-performer-outline-on');
                ++state.favoritesGeneration;
                state.favoritesRunning = null;
                state.favorites.clear();
                state.favoritesExpires = 0;
                clearFavoriteOutlines();
                ++state.generation;
                state.entries.clear();
                state.people.clear();
                state.expires = 0;
                state.updatedAt = '';
                removeRows();
            }
            state.userId = current;
            state.enabled = enabled;
            state.outlineEnabled = outlineEnabled;
            stylesOnce();
            document.documentElement.classList.toggle('jf-performer-outline-on', outlineEnabled);
            if (outlineEnabled) refreshFavorites(true);
            else { state.favorites.clear(); clearFavoriteOutlines(); }
            state.genders = ROW_IDS.filter(g => Array.isArray(genders) && genders.includes(g));
            state.configured = true;
            if (state.enabled && state.genders.length) ensureData(); // Prewarm while the user browses other pages.
        } catch (error) { console.warn('[JFToStashSync] Cannot load performer Popularity configuration.', error); }
        finally { configPending = false; }
    }
    function tick() {
        // A background tab is not a Jellyfin SPA navigation. Preserve cached rows.
        if (document.hidden) return;
        if (!userId()) {
            document.documentElement.classList.remove('jf-performer-outline-on');
            ++state.favoritesGeneration;
            state.favorites.clear();
            clearFavoriteOutlines();
        }
        if (!state.configured || state.userId !== userId()) { prepare(); return; }
        if (state.outlineEnabled) { updateFavoriteOutlines(); refreshFavorites(false); }
        if (!state.enabled || !state.genders.length) return;
        if (!root()) {
            // Keep favorite portrait outlines active across Jellyfin tabs,
            // but never show our rating rows outside the main Home tab.

            wasOnHome = false;
            removeRows();
            return;
        }
        wasOnHome = true;
        if (mountPending) return;
        mountPending = true;
        window.requestAnimationFrame(() => {
            mountPending = false;
            paint();
            if (!state.running && Date.now() >= state.expires) ensureData();
        });
    }
    function start() {
        tick();
        document.addEventListener('jf-to-stash-user-ready', tick);
        window.addEventListener('hashchange', () => { tick(); requestFavoritesRefresh(250); });
        window.addEventListener('popstate', () => { tick(); requestFavoritesRefresh(250); });
        document.addEventListener('viewshow', () => { tick(); requestFavoritesRefresh(250); }, true);
        document.addEventListener('pageshow', tick, true);
        document.addEventListener('click', onFavoriteInteraction, true);
        document.addEventListener('userdataupdated', () => requestFavoritesRefresh(400), true);
        // Actor favorites may change in another tab; refresh only their outlines.
        // Preserve prepared performer cards when returning to this browser tab.
        document.addEventListener('visibilitychange', () => {
            if (!document.hidden) {
                if (state.outlineEnabled) requestFavoritesRefresh(250);
                tick();
            }
        });
        window.setInterval(tick, 2000);
        console.info('[JFToStashSync] Performer Popularity v1.2.0.15 initialized.');
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
}());
