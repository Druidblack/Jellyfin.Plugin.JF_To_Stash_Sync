/* JF To Stash Sync — Babepedia Top 100 prepared actor rows, Jellyfin 12. */
(function () {
    'use strict';
    const T = (text) => window.__jfToStashI18n?.t(text) ?? String(text ?? '');
    if (window.__jfToStashBabepediaTopLoaded) return;
    window.__jfToStashBabepediaTopLoaded = true;
    const PREFIX = 'jf-bpt-';
    const CATEGORIES = [
        'pornstartop100', 'milfpornstartop100', 'asianpornstartop100',
        'latinapornstartop100', 'slavicpornstartop100', 'ebonypornstartop100',
        'caucasianpornstartop100', 'onlyfanstop100'
    ];
    const LABELS = {
        pornstartop100: 'Pornstars', milfpornstartop100: 'MILF',
        asianpornstartop100: 'Asian', latinapornstartop100: 'Latina',
        slavicpornstartop100: 'Slavic', ebonypornstartop100: 'Ebony',
        caucasianpornstartop100: 'Caucasian', onlyfanstop100: 'OnlyFans'
    };
    const state = {
        userId: '', configured: false, enabled: false, categories: [],
        entries: new Map(), persons: new Map(), error: '', status: '',
        expires: 0, generation: 0, running: null
    };
    let configPending = false;
    let modal = null;
    let onHome = false;
    function api() { return window.ApiClient; }
    function val(obj, a, b, fallback) { return obj && obj[a] !== undefined ? obj[a] : obj && obj[b] !== undefined ? obj[b] : fallback; }
    function uid() { try { return api()?.getCurrentUserId() || ''; } catch (_) { return ''; } }
    function id(value) { return String(value || '').replace(/-/g, '').toLowerCase(); }
    async function json(path) {
        const client = api();
        if (!client?.fetch || !client.getUrl) throw new Error(T('Jellyfin API недоступен')); 
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), 25000);
        try {
            const response = await client.fetch({ url: client.getUrl(path), type: 'GET', signal: controller.signal });
            const data = await response.json();
            if (!response.ok) throw new Error(data.message || data.Message || 'HTTP ' + response.status);
            return data;
        } finally { clearTimeout(timer); }
    }
    function visible(node) {
        return !!(node && node.isConnected && node.getClientRects().length &&
            getComputedStyle(node).display !== 'none');
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
        const home = Array.from(document.querySelectorAll('.homePage,.homeView'))
            .find(node => visible(node) && node.querySelector('.verticalSection'));
        if (home) return home.querySelector('.verticalSection').parentElement;
        if (/(?:^|[\/#?!])home(?:[\/#?&]|$)/i.test(location.pathname + location.hash)) {
            const main = Array.from(document.querySelectorAll('main,[role="main"]')).find(visible);
            if (main) return main.querySelector('.MuiContainer-root') || main;
        }
        return null;
    }
    function el(tag, cls, text) {
        const result = document.createElement(tag);
        if (cls) result.className = cls;
        if (text !== undefined) result.textContent = T(text);
        return result;
    }
    function addStyles() {
        if (document.getElementById(PREFIX + 'style')) return;
        const style = el('style');
        style.id = PREFIX + 'style';
        style.textContent = `
            .jf-bpt-section{margin:1.5em 0 2em;min-width:0}
            .jf-bpt-header{display:flex;align-items:center;justify-content:space-between;padding:0 3.3%;gap:1em;margin-bottom:.8em}
            .jf-bpt-header h2{font-size:1.45em;font-weight:600;margin:0}
            .jf-bpt-action{background:transparent;border:0;color:var(--theme-primary-color,#64b5f6);cursor:pointer;font:inherit;padding:.45em}
            .jf-bpt-status{padding:0 3.3%;font-size:.85em;opacity:.78}
            .jf-bpt-status:empty{display:none}
            .jf-bpt-scroll{display:flex;overflow-x:auto;scroll-snap-type:x proximity;scroll-padding-inline:3.3%;gap:1rem;padding:.45em 3.3% 1em;scrollbar-width:thin}
            .jf-bpt-tile{position:relative;display:block;min-width:148px;width:clamp(148px,14vw,200px);flex-shrink:0;color:inherit;text-decoration:none;scroll-snap-align:start;cursor:pointer}
            .jf-bpt-portrait{position:relative;aspect-ratio:2/3;background:#292929;border-radius:5px;overflow:hidden}
            .jf-bpt-portrait img{height:100%;width:100%;object-fit:cover;display:block}
            .jf-bpt-rank{position:absolute;top:.4rem;left:.4rem;background:#161616df;color:#fff;border-radius:5px;padding:.24em .5em;font-size:.83em;font-weight:bold}
            .jf-bpt-info{text-align:center;padding:.3em .2em .5em;min-width:0}
            .jf-bpt-name{overflow:hidden;white-space:nowrap;text-overflow:ellipsis;text-align:center;line-height:1.35}
            .jf-bpt-msg{padding:.7em 3.3%;opacity:.8}
            #jf-bpt-modal{position:fixed;inset:0;z-index:30000;background:rgba(0,0,0,.8);display:flex;align-items:center;justify-content:center;padding:2vw;box-sizing:border-box}
            #jf-bpt-modal .jf-bpt-panel{width:min(1100px,100%);height:min(90vh,950px);display:flex;flex-direction:column;overflow:hidden;background:var(--dialog-background,#1e1e1e);border-radius:9px;box-shadow:0 12px 60px #000a}
            #jf-bpt-modal .jf-bpt-modalhead{display:flex;align-items:center;justify-content:space-between;padding:1em 1.2em}
            #jf-bpt-modal .jf-bpt-grid{overflow:auto;display:grid;grid-template-columns:repeat(auto-fill,minmax(145px,1fr));gap:1.5em 1em;padding:.5em 1.2em 1.5em;flex:1;align-content:start}
            #jf-bpt-modal .jf-bpt-tile{min-width:0;width:100%}
            html.jf-performer-outline-on .jf-bpt-tile.jf-spp-favorite .jf-bpt-portrait::after{
                content:"";position:absolute;inset:0;box-sizing:border-box;border:2px solid rgba(236,139,145,.8);border-radius:inherit;pointer-events:none;z-index:2
            }
            @media(max-width:600px){.jf-bpt-scroll{gap:.7rem}.jf-bpt-tile{min-width:125px;width:125px}}
        `;
        document.head.appendChild(style);
    }
    function imageInfo(person) {
        const tags = val(person, 'imageTags', 'ImageTags', {}) || {};
        const tag = tags.Primary || tags.primary || val(person, 'primaryImageTag', 'PrimaryImageTag', '');
        return tag ? { type: 'Primary', tag } : null;
    }
    function imageUrl(person) {
        const img = imageInfo(person);
        if (!img) return '';
        const client = api(), pid = val(person, 'id', 'Id', '');
        try {
            if (client.getScaledImageUrl) return client.getScaledImageUrl(pid, { type: img.type, tag: img.tag, maxWidth: 350, quality: 85 });
            return client.getUrl('Items/' + encodeURIComponent(pid) + '/Images/Primary', { tag: img.tag, maxWidth: 350, quality: 85 });
        } catch (_) { return ''; }
    }
    function tile(person, rank) {
        const a = el('a', 'jf-bpt-tile jf-spp-tile');
        const pid = id(val(person, 'id', 'Id', ''));
        const name = val(person, 'name', 'Name', 'Актёр');
        a.dataset.personId = pid;
        a.href = (location.hash.startsWith('#!') ? '#!/details?id=' : '#/details?id=') + encodeURIComponent(pid);
        a.title = name + ' · Babepedia #' + rank;
        const data = val(person, 'userData', 'UserData', {});
        if (val(data, 'isFavorite', 'IsFavorite', false)) a.classList.add('jf-spp-favorite');
        const portrait = el('div', 'jf-bpt-portrait jf-spp-portrait');
        const url = imageUrl(person);
        if (url) { const img = el('img'); img.alt = ''; img.loading = 'lazy'; img.src = url; portrait.appendChild(img); }
        portrait.appendChild(el('span', 'jf-bpt-rank', '#' + rank));
        a.appendChild(portrait);
        const info = el('div', 'jf-bpt-info jf-spp-info');
        info.appendChild(el('div', 'jf-bpt-name', name));
        a.appendChild(info);
        return a;
    }
    function entries(category) {
        return (state.entries.get(category) || []).map(x =>
            ({ person: state.persons.get(x.id), rank: x.rank })).filter(x => x.person);
    }
    function removeRows() {
        document.querySelectorAll('[data-jf-bpt-row="true"]').forEach(node => node.remove());
        closeModal();
    }
    function paint() {
        const host = root();
        if (!host || !state.enabled || !state.categories.length) return;
        addStyles();
        const existing = Array.from(host.children).filter(x => x.classList?.contains('jf-spp-section'));
        let anchor = existing.length ? existing[existing.length - 1].nextSibling : host.firstChild;
        for (const category of state.categories) {
            const rows = entries(category);
            const sectionId = PREFIX + category;
            let section = document.getElementById(sectionId);
            if (!section || section.parentElement !== host) {
                if (section) section.remove();
                section = el('section', 'verticalSection jf-bpt-section');
                section.id = sectionId;
                section.dataset.jfBptRow = 'true';
                const header = el('div', 'jf-bpt-header');
                header.appendChild(el('h2', '', T('🏆 Babepedia Top 100 · ') + T(LABELS[category])));
                const all = el('button', 'jf-bpt-action', T('Показать все ›'));
                all.type = 'button'; all.addEventListener('click', () => showModal(category));
                header.appendChild(all); section.appendChild(header);
                section.appendChild(el('div', 'jf-bpt-status'));
                section.appendChild(el('div', 'jf-bpt-scroll'));
            }
            if (section !== anchor) host.insertBefore(section, anchor);
            anchor = section.nextSibling;
            const status = section.querySelector('.jf-bpt-status');
            status.textContent = state.error || state.status;
            const signature = rows.map(x => id(val(x.person, 'id', 'Id', '')) + ':' + x.rank + ':' +
                (imageInfo(x.person)?.tag || '') + ':' + val(x.person, 'name', 'Name', '')).join('|');
            if (section.dataset.signature !== signature) {
                const scroller = section.querySelector('.jf-bpt-scroll');
                const oldScroll = scroller.scrollLeft;
                scroller.replaceChildren();
                rows.slice(0, 24).forEach(x => scroller.appendChild(tile(x.person, x.rank)));
                if (!rows.length) scroller.appendChild(el('div', 'jf-bpt-msg', state.error || state.status || T('Актёры этой категории не найдены в Jellyfin.')));
                section.dataset.signature = signature;
                scroller.scrollLeft = oldScroll;
                if (modal?.dataset.category === category) refreshModal(category);
            }
            section.querySelector('.jf-bpt-action').hidden = !rows.length;
        }
    }
    function closeModal() { modal?.remove(); modal = null; document.removeEventListener('keydown', keydown); }
    function keydown(e) { if (e.key === 'Escape') closeModal(); }
    function refreshModal(category) {
        const grid = modal?.querySelector('.jf-bpt-grid');
        if (!grid) return;
        const scrollTop = grid.scrollTop;
        grid.replaceChildren(...entries(category).map(x => tile(x.person, x.rank)));
        grid.scrollTop = scrollTop;
    }
    function showModal(category) {
        if (modal) return;
        const overlay = el('div'); overlay.id = PREFIX + 'modal'; overlay.dataset.category = category;
        overlay.setAttribute('role', 'dialog'); overlay.setAttribute('aria-modal', 'true');
        overlay.addEventListener('click', event => { if (event.target === overlay) closeModal(); });
        const panel = el('div', 'jf-bpt-panel'); const header = el('div', 'jf-bpt-modalhead');
        header.appendChild(el('h2', '', T('🏆 Babepedia Top 100 · ') + T(LABELS[category])));
        const close = el('button', 'jf-bpt-action', T('✕ Закрыть')); close.type = 'button'; close.addEventListener('click', closeModal);
        header.appendChild(close); panel.appendChild(header);
        const grid = el('div', 'jf-bpt-grid');
        entries(category).forEach(x => grid.appendChild(tile(x.person, x.rank)));
        panel.appendChild(grid); overlay.appendChild(panel); document.body.appendChild(overlay);
        modal = overlay; document.addEventListener('keydown', keydown); close.focus();
    }
    async function loadData(generation) {
        const index = await json('JFToStashSync/BabepediaTopPrepared');
        if (generation !== state.generation) return;
        if (val(index, 'status', 'Status', '') !== 'ready') {
            state.status = val(index, 'message', 'Message', T('Запустите подготовку Babepedia Top 100'));
            state.expires = Date.now() + 30000; return;
        }
        const data = val(index, 'entries', 'Entries', []);
        if (!Array.isArray(data)) throw new Error(T('Некорректный индекс Top 100'));
        const ids = new Set();
        for (const category of state.categories) state.entries.set(category, []);
        for (const row of data) {
            const category = val(row, 'category', 'Category', '');
            const pid = id(val(row, 'personId', 'PersonId', ''));
            const rank = Number(val(row, 'rank', 'Rank', 0));
            if (!state.entries.has(category) || !/^[a-f0-9]{32}$/.test(pid) || !Number.isInteger(rank) || rank < 1 || rank > 100) continue;
            state.entries.get(category).push({ id: pid, rank }); ids.add(pid);
        }
        const pending = val(index, 'pendingCategories', 'PendingCategories', []);
        const list = Array.from(ids);
        for (let i = 0; i < list.length; i += 120) {
            if (generation !== state.generation) return;
            const batches = [list.slice(i,i+60),list.slice(i+60,i+120)].filter(x=>x.length);
            const pages = await Promise.all(batches.map(async batch => {
                const query = new URLSearchParams({ UserId:state.userId, Ids:batch.join(','),
                    EnableImages:'true', EnableUserData:'true', Limit:String(batch.length) });
                return json('Items?' + query);
            }));
            if (generation !== state.generation) return;
            for (const results of pages) {
                for (const person of val(results,'items','Items',[])) {
                    const pid = id(val(person,'id','Id',''));
                    if (ids.has(pid) && String(val(person,'type','Type','')).toLowerCase()==='person') state.persons.set(pid,person);
                }
            }
            state.status = T('Загружены актёры Babepedia: ') + Math.min(i+120,list.length) + T(' из ') + list.length;
            paint();
        }
        state.status = pending.length ? T('Некоторые категории ещё не подготовлены. Запустите задачу Babepedia Top 100.') : '';
        state.expires = Date.now() + 10 * 60 * 1000;
    }
    function ensureData() {
        if (state.running || Date.now() < state.expires) return;
        const generation = ++state.generation;
        state.running = (async () => {
            state.error = ''; state.status = T('Загрузка сохранённого Babepedia Top 100…');
            state.entries.clear(); state.persons.clear();
            try { await loadData(generation); }
            catch (err) {
                state.error = 'Babepedia Top 100: ' + (err?.message || T('ошибка'));
                state.status = ''; state.expires = Date.now() + 30000;
                console.warn('[JFToStashSync]', state.error);
            }
            finally { state.running = null; paint(); window.__jfStashHomeLoader?.mark('babepedia'); }
        })();
        paint();
    }
    async function configure() {
        if (configPending || !uid()) return;
        configPending = true;
        const current = uid();
        try {
            const config = await (window.__jfHomeWarmup?.getConfig?.() || json('JFToStashSync/PlayerOCounterConfig'));
            if (current !== uid()) return;
            if (state.userId !== current) {
                ++state.generation; state.entries.clear(); state.persons.clear(); state.expires = 0; removeRows();
            }
            state.userId = current;
            state.enabled = val(config, 'babepediaTopEnabled', 'BabepediaTopEnabled', false) === true;
            const cats = val(config, 'babepediaTopCategories', 'BabepediaTopCategories', []);
            state.categories = CATEGORIES.filter(x => Array.isArray(cats) && cats.includes(x));
            state.configured = true;
            if (!state.enabled) removeRows();
            else if (state.categories.length) ensureData(); // Prewarm off the home route.
        } catch (e) { console.warn('[JFToStashSync] Could not load Babepedia Top configuration', e); }
        finally { configPending = false; }
    }
    function tick() {
        // A background tab is not a Jellyfin SPA navigation. Preserve cached rows.
        if (document.hidden) return;
        if (!uid()) { state.configured = false; removeRows(); return; }
        if (!state.configured || state.userId !== uid()) { configure(); return; }
        if (!state.enabled || !state.categories.length) return;
        if (!root()) {
            // Favorites reuses the Home host; hide Top 100 without refetching.

            onHome = false; removeRows(); return;
        }
        onHome = true;
        paint();
        if (Date.now() >= state.expires && !state.running) ensureData();
    }
    function start() {
        tick();
        document.addEventListener('jf-to-stash-user-ready', tick);
        window.addEventListener('hashchange', tick);
        window.addEventListener('popstate', tick);
        document.addEventListener('viewshow', tick, true);
        // Switching tabs is not a route change: reuse the current prepared rows.
        document.addEventListener('visibilitychange', () => { if (!document.hidden) tick(); });
        window.setInterval(tick, 2000);
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
}());
