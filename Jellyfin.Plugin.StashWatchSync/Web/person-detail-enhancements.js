/* JF To Stash Sync v1.2.0.56 — Person details, Info / Co-stars, portraits and overview links. */
(function () {
    'use strict';
    const T = (text) => window.__jfToStashI18n?.t(text) ?? String(text ?? '');
    if (window.__jfToStashPersonDetailsLoaded) return;
    window.__jfToStashPersonDetailsLoaded = true;

    const ROOT_CLASS = 'jf-person-detail-enhanced';
    const BACKDROP_CLASS = 'jf-person-detail-banner';
    // Jellyfin keeps *two* native portraits: the primary desktop portrait and a
    // second hide-desktop/hide-tv portrait inside .detailRibbon for mobile.
    // Never unhide both, reparent either, or restore a cloned portrait.
    const SELECTOR_OVERVIEW = '.overview-controls';
    const SELECTOR_BACKDROP = '#itemBackdrop';
    const CACHE_MS = 5 * 60 * 1000;
    let currentKey = '';
    let currentPerson = null;
    let sequence = 0;
    let pending = null;
    let timer = null;
    let backdropState = null;
    const cache = new Map();
    // URL conversion lives with the confirmed Person page, rather than with
    // video-player controls. Jellyfin may render nested <p> tags by innerHTML
    // or via the HTML parser, which moves their contents OUTSIDE .overview.
    let linksConfigUser = '';
    let linksConfigLoaded = false;
    let linksEnabled = false;
    let linksConfigPending = null;
    let linksConfigRetryAfter = 0;
    let coTabsEnabled = false;
    let coMount = null;
    let coGeneration = 0;
    let coImages = new Set();
    let coImageObserver = null;
    let coActorLoading = false;


    function normalized(id) { return String(id || '').replace(/-/g, '').toLowerCase(); }
    function isDetailsRoute() {
        const path = String(location.hash || '').replace(/^#!?/, '') || String(location.pathname || '');
        return /(?:^|\/)details(?:\?|\/|$)/i.test(path);
    }
    function routeId() {
        if (!isDetailsRoute()) return '';
        const hash = String(location.hash || '').replace(/^#!?/, '');
        const query = hash.includes('?') ? hash.slice(hash.indexOf('?') + 1) : String(location.search || '').replace(/^\?/, '');
        const id = normalized(new URLSearchParams(query).get('id'));
        return /^[a-f0-9]{32}$/.test(id) ? id : '';
    }
    function api() { return window.ApiClient; }
    function activeUserId() {
        try { return normalized(api()?.getCurrentUserId?.()); }
        catch (_) { return ''; }
    }
    function textValue(obj, camel, pascal) {
        return obj && (obj[camel] !== undefined ? obj[camel] : obj[pascal]);
    }
    function imageTag(item) {
        const images = textValue(item, 'imageTags', 'ImageTags') || {};
        return images.Banner || images.banner || '';
    }
    function injectCss() {
        if (document.getElementById('jf-person-detail-enhancements-css')) return;
        const style = document.createElement('style');
        style.id = 'jf-person-detail-enhancements-css';
        style.textContent = `
          /* Desktop/TV: keep the genuine primary portrait in its own grid cell.
             In particular, NEVER override Jellyfin's hide-desktop/hide-tv rules
             on the separate responsive .detailRibbon portrait. Doing so shows a
             second picture in the middle and pushes the actor's name upwards. */
          html.${ROOT_CLASS}.layout-desktop .detailPagePrimaryContainer > .detailImageContainer:not(.hide-desktop):not(.hide-tv),
          html.${ROOT_CLASS}.layout-tv .detailPagePrimaryContainer > .detailImageContainer:not(.hide-desktop):not(.hide-tv) {
            display:block !important;visibility:visible !important;opacity:1 !important;
            /* Match the working detailImageContainer userscript: without these,
               Jellyfin/theme height and overflow constraints crop tall portraits.
               Only the *desktop primary* card is affected; mobile's native
               hide-desktop/hide-tv card stays under Jellyfin responsive CSS. */
            height:auto !important;max-height:none !important;overflow:visible !important;
          }
          html.${ROOT_CLASS} .overview-controls {
            display:flex !important;visibility:visible !important;opacity:1 !important;
            height:auto !important;overflow:visible !important;
          }
          html.${ROOT_CLASS} .overview-controls .overview-expand,
          html.${ROOT_CLASS} .overview-controls .overview-expand.hide {
            display:inline-flex !important;visibility:visible !important;opacity:1 !important;
            pointer-events:auto !important;
          }
          html.${ROOT_CLASS} .jf-co-tabs { display:flex;align-items:center;gap:.55em;
            margin:0 0 1em;border-bottom:1px solid rgba(170,170,170,.24);padding:0 0 .55em; }
          html.${ROOT_CLASS} .jf-co-tabs button {background:transparent;color:inherit;
            font-size:1.05em;font-weight:600;border:0;padding:.55em .85em;cursor:pointer;
            border-bottom:2px solid transparent;border-radius:.3em;}
          html.${ROOT_CLASS} .jf-co-tabs button[aria-selected="true"] {border-bottom-color:#e44c68;color:inherit;}
          html.${ROOT_CLASS} .detailSectionContent[data-jf-cotab="co"] > :not(.jf-co-tabs):not(.jf-co-panel) {display:none !important;}
          html.${ROOT_CLASS} .jf-co-panel[hidden] {display:none !important;}
          html.${ROOT_CLASS} .jf-co-cards {display:grid;grid-template-columns:repeat(auto-fill,minmax(116px,1fr));
            gap:1em .7em;margin:.6em 0 1.3em;}
          html.${ROOT_CLASS} .jf-co-actor {display:flex;flex-direction:column;align-items:center;
            background:transparent;border:0;color:inherit;cursor:pointer;text-align:center;
            padding:.15em;min-width:0;}
          html.${ROOT_CLASS} .jf-co-portrait {aspect-ratio:3/4;width:100%;border-radius:.5em;
            display:flex;align-items:center;justify-content:center;overflow:hidden;
            background:rgba(120,120,120,.14);font-size:2em;}
          html.${ROOT_CLASS} .jf-co-portrait img {width:100%;height:100%;object-fit:cover;}
          html.${ROOT_CLASS} .jf-co-name {font-size:.91em;margin-top:.4em;overflow-wrap:anywhere;}
          html.${ROOT_CLASS} .jf-co-count {font-size:.82em;opacity:.76;margin-top:.15em;}
          html.${ROOT_CLASS} .jf-co-scenes {display:grid;grid-template-columns:repeat(auto-fill,minmax(130px,1fr));gap:1em .65em;}
          html.${ROOT_CLASS} .jf-co-video {display:flex;flex-direction:column;text-align:center;color:inherit;text-decoration:none;}
          html.${ROOT_CLASS} .jf-co-video img {width:100%;aspect-ratio:2/3;object-fit:cover;
            border-radius:.45em;background:rgba(120,120,120,.13);}
          html.${ROOT_CLASS} .jf-co-video span {font-size:.9em;margin-top:.35em;overflow-wrap:anywhere;}
          html.${ROOT_CLASS} .jf-co-back, html.${ROOT_CLASS} .jf-co-more {border:0;border-radius:.35em;
            background:rgba(120,120,120,.22);color:inherit;padding:.65em .9em;cursor:pointer;margin:.5em 0 1em;}
          html.${ROOT_CLASS} .jf-co-status {opacity:.8;padding:.7em 0;}
          html.${ROOT_CLASS} #itemBackdrop.${BACKDROP_CLASS} {
            display:block !important;visibility:visible !important;opacity:1 !important;
            /* Full image width with preserved proportions. The original
               Pornhub Banner is very wide, so cover would crop it heavily. */
            background-size:100% auto !important;background-position:center top !important;
            background-repeat:no-repeat !important;
          }
        `;
        document.head.appendChild(style);
    }
    function getDetailsPage() {
        // Jellyfin may retain inactive view DOM nodes. Prefer the active details page.
        const pages = document.querySelectorAll('#itemDetailPage, .itemDetailPage');
        for (const page of pages) {
            if (page.closest('.hide, .hidden, .d-none, [aria-hidden="true"]')) continue;
            if (page.getClientRects && page.getClientRects().length === 0) continue;
            return page;
        }
        return null;
    }
    function unhideControls(page) {
        // The primary portrait is already rendered by Jellyfin. The desktop
        // visibility rule above applies only to that element and deliberately
        // does not alter the original responsive classes or inline layout.
        // Do not unhide hidden mobile/TV copies and do not insert clones.
        for (const controls of page.querySelectorAll(SELECTOR_OVERVIEW)) {
            for (const name of ['hide', 'hidden', 'd-none', 'is-hidden']) controls.classList.remove(name);
            for (const button of controls.querySelectorAll('.overview-expand')) {
                for (const name of ['hide', 'hidden', 'd-none', 'is-hidden']) button.classList.remove(name);
            }
        }
    }
    const LINKS_SELECTOR = 'a[data-jf-to-stash-overview-link="true"]';
    function restoreOverviewLinks() {
        document.querySelectorAll(LINKS_SELECTOR).forEach(link => {
            if (!link.parentNode) return;
            // Restore literal text, never innerHTML. A later Person refresh can
            // reconstruct its own description without depending on our anchors.
            link.replaceWith(document.createTextNode(link.textContent || link.href || ''));
        });
    }
    function ensureOverviewLinksConfiguration(user) {
        if (linksConfigUser !== user) {
            linksConfigUser = user;
            linksConfigLoaded = false;
            linksEnabled = false;
            coTabsEnabled = false;
            linksConfigPending = null;
            linksConfigRetryAfter = 0;
        }
        if (linksConfigLoaded || linksConfigPending || Date.now() < linksConfigRetryAfter) return;
        const client = api();
        if (!client?.fetch || !client?.getUrl) return;
        const current = user;
        const request = (async () => {
            const cfg = window.__jfHomeWarmup?.getConfig
                ? await window.__jfHomeWarmup.getConfig()
                : await (async () => {
                    const response = await client.fetch({url:client.getUrl('JFToStashSync/PlayerOCounterConfig'),type:'GET'});
                    if (!response.ok) throw new Error('Person links config HTTP ' + response.status);
                    return response.json();
                })();
            if (current !== linksConfigUser) return;
            linksEnabled = cfg?.personOverviewLinksEnabled === true || cfg?.PersonOverviewLinksEnabled === true;
            coTabsEnabled = cfg?.personCoStarsTabsEnabled === true || cfg?.PersonCoStarsTabsEnabled === true;
            linksConfigLoaded = true;
        })();
        linksConfigPending = request;
        request.catch(error => {
            if (current === linksConfigUser) {
                linksConfigRetryAfter = Date.now() + 5000;
                console.debug('[JFToStashSync] Person URL configuration not yet available', error);
            }
        }).finally(() => {
            if (current === linksConfigUser) {
                linksConfigPending = null;
                schedule();
            }
        });
    }
    function isMatchingPersonContent(page, person) {
        const id = normalized(textValue(person, 'id', 'Id'));
        if (!id || id !== routeId()) return false;
        const control = page.querySelector('.mainDetailButtons .btnUserRating[data-id], .mainDetailButtons .btnUserRating[data-itemid]');
        const controlId = normalized(control?.getAttribute('data-id') || control?.getAttribute('data-itemid'));
        if (controlId) return controlId === id;
        // Some themes omit the favourite button. A matching native name still
        // protects us from the previous actor's DOM during SPA navigation.
        const name = String(textValue(person, 'name', 'Name') || '').trim();
        const title = page.querySelector('h1.itemName bdi, h1.itemName');
        if (title && name) return title.textContent.trim().replace(/\s+/g, ' ').toLowerCase() === name.replace(/\s+/g, ' ').toLowerCase();
        return true;
    }
    function stripTrailingPunctuation(raw) {
        let url = raw, suffix = '';
        while (url && /[.,;:!?\]\}]/.test(url.slice(-1))) {
            suffix = url.slice(-1) + suffix;
            url = url.slice(0, -1);
        }
        while (url.endsWith(')')) {
            const opens = (url.match(/\(/g) || []).length;
            const closes = (url.match(/\)/g) || []).length;
            if (closes <= opens) break;
            suffix = ')' + suffix;
            url = url.slice(0, -1);
        }
        return {url, suffix};
    }
    function linkifyOverviewNode(node) {
        const text = node?.nodeValue || '';
        if (!/https?:\/\//i.test(text)) return;
        const parent = node.parentElement;
        if (!parent || parent.closest('a, button, script, style, code, pre, textarea, input, select')) return;
        const regexp = /https?:\/\/[^\s<>"'`]+/gi;
        const fragment = document.createDocumentFragment();
        let offset = 0, changed = false;
        for (const match of text.matchAll(regexp)) {
            const index = match.index;
            if (index > offset) fragment.appendChild(document.createTextNode(text.slice(offset, index)));
            const {url, suffix} = stripTrailingPunctuation(match[0]);
            let valid = false;
            try {
                const parsed = new URL(url);
                valid = parsed.protocol === 'http:' || parsed.protocol === 'https:';
                if (valid) {
                    const anchor = document.createElement('a');
                    anchor.href = parsed.href;
                    anchor.textContent = url;
                    anchor.target = '_blank';
                    anchor.rel = 'noopener noreferrer';
                    anchor.setAttribute('data-jf-to-stash-overview-link', 'true');
                    anchor.style.setProperty('text-decoration', 'underline', 'important');
                    anchor.style.setProperty('cursor', 'pointer', 'important');
                    anchor.style.setProperty('pointer-events', 'auto', 'important');
                    fragment.appendChild(anchor);
                    changed = true;
                }
            } catch (_) { /* Leave invalid addresses untouched. */ }
            if (!valid) fragment.appendChild(document.createTextNode(url));
            if (suffix) fragment.appendChild(document.createTextNode(suffix));
            offset = index + match[0].length;
        }
        if (!changed) return;
        if (offset < text.length) fragment.appendChild(document.createTextNode(text.slice(offset)));
        node.replaceWith(fragment);
    }
    function ensurePersonOverviewLinks(page, person, user) {
        ensureOverviewLinksConfiguration(user);
        if (!linksConfigLoaded) return;
        if (!linksEnabled) {
            restoreOverviewLinks();
            return;
        }
        if (!isMatchingPersonContent(page, person)) return;
        // This is intentional: with invalid <p class="overview"><bdi><p>...,
        // the browser HTML parser closes .overview before the URL paragraphs.
        // They remain inside .detailSectionContent as SIBLINGS. Traversing
        // .overview alone can never linkify those real-world DOM structures.
        const content = page.querySelector('.detailSectionContent');
        if (!content) return;
        const walker = document.createTreeWalker(content, NodeFilter.SHOW_TEXT);
        const nodes = [];
        while (walker.nextNode()) nodes.push(walker.currentNode);
        nodes.forEach(linkifyOverviewNode);
    }
    // Co-star navigation belongs to the actor information block. Keep every native
    // Jellyfin node in place; changing tabs only hides its direct children.
    function removeCoTabs() {
        ++coGeneration;
        coActorLoading = false;
        if (coImageObserver) { coImageObserver.disconnect(); coImageObserver = null; }
        for (const uri of coImages) URL.revokeObjectURL(uri);
        coImages.clear();
        if (coMount) {
            coMount.content.removeAttribute('data-jf-cotab');
            coMount.tabs.remove();
            coMount.panel.remove();
        }
        coMount = null;
    }
    function coElement(tag, className, label) {
        const e = document.createElement(tag);
        if (className) e.className = className;
        if (label !== undefined) e.textContent = T(label);
        return e;
    }
    async function coGet(path) {
        const client = api();
        if (!client?.fetch || !client?.getUrl) throw new Error(T('Jellyfin API недоступен.'));
        const response = await client.fetch({url:client.getUrl(path), type:'GET'});
        if (!response.ok) throw new Error('Jellyfin HTTP ' + response.status);
        return response.json();
    }
    function coPath(endpoint, params) {
        return 'JFToStashSync/' + endpoint + '?' + new URLSearchParams(params).toString();
    }
    function coMessage(panel, message) {
        panel.replaceChildren(coElement('div', 'jf-co-status', message));
    }
    function displayCoTab(which) {
        if (!coMount) return;
        const mount = coMount;
        mount.content.dataset.jfCotab = which;
        mount.infoButton.setAttribute('aria-selected', String(which === 'info'));
        mount.coButton.setAttribute('aria-selected', String(which === 'co'));
        mount.panel.hidden = which !== 'co';
        if (which === 'co' && !mount.loaded && !coActorLoading) void loadCoActors(mount);
    }
    async function loadCoImage(img, mount, id) {
        const gen = coGeneration;
        try {
            const client = api();
            const response = await client.fetch({
                url:client.getUrl(coPath('PersonCoStarImage', {personId:mount.personId, coStashId:id})), type:'GET'
            });
            if (!response.ok) { img.replaceWith(coElement('span','','👤')); return; }
            const blob = await response.blob();
            if (!blob.type.startsWith('image/') || gen !== coGeneration || coMount !== mount) return;
            const url = URL.createObjectURL(blob);
            coImages.add(url);
            if (img.isConnected && coMount === mount) img.src = url;
        } catch (_) { if (img.isConnected) img.replaceWith(coElement('span','','👤')); }
    }
    function coLazyImages(mount, container) {
        if (coImageObserver) coImageObserver.disconnect();
        coImageObserver = null;
        const images = container.querySelectorAll('img[data-co-id]');
        if (typeof IntersectionObserver !== 'function') {
            images.forEach(img => void loadCoImage(img, mount, img.dataset.coId));
            return;
        }
        const obs = new IntersectionObserver(items => {
            for (const entry of items) {
                if (!entry.isIntersecting) continue;
                obs.unobserve(entry.target);
                void loadCoImage(entry.target, mount, entry.target.dataset.coId);
            }
        }, {rootMargin:'160px'});
        coImageObserver = obs;
        images.forEach(img => obs.observe(img));
    }
    async function loadCoActors(mount) {
        if (coActorLoading || mount.loaded) return;
        coActorLoading = true;
        const gen = coGeneration;
        coMessage(mount.panel, 'Загрузка совместных съёмок из Stash…');
        try {
            const data = await coGet(coPath('PersonCoStars', {personId:mount.personId}));
            if (gen !== coGeneration || coMount !== mount) return;
            mount.loaded = true;
            const actors = Array.isArray(data.actors) ? data.actors : [];
            mount.panel.replaceChildren();
            if (data.partial) mount.panel.appendChild(coElement('div', 'jf-co-status',
                'Показаны результаты первых 10 000 сцен Stash. Для очень больших фильмографий список может быть неполным.'));
            if (!actors.length) {
                coMessage(mount.panel, 'Совместных видео, доступных в Jellyfin, не найдено. Проверьте Stash ID и синхронизацию ссылок на видео с Jellyfin.');
                return;
            }
            const grid = coElement('div','jf-co-cards');
            for (const actor of actors) {
                const button = coElement('button','jf-co-actor');
                button.type = 'button';
                button.title = actor.name || '';
                const portrait = coElement('span','jf-co-portrait','👤');
                if (actor.jellyfinPersonId || actor.hasImage) {
                    const img = coElement('img');
                    img.alt = actor.name || '';
                    img.loading = 'lazy';
                    img.onerror = () => { img.replaceWith(coElement('span','','👤')); };
                    if (actor.jellyfinPersonId) {
                        img.src = api().getUrl('Items/' + encodeURIComponent(actor.jellyfinPersonId) + '/Images/Primary',
                            {fillHeight:310, fillWidth:235, quality:85});
                    } else img.dataset.coId = actor.stashId;
                    portrait.replaceChildren(img);
                }
                button.append(portrait, coElement('span','jf-co-name',actor.name || T('Без имени')),
                    coElement('span','jf-co-count',String(actor.count) + T(' общих видео')));
                button.addEventListener('click', () => void showCoScenes(mount, actor));
                grid.appendChild(button);
            }
            mount.panel.appendChild(grid);
            coLazyImages(mount, grid);
        } catch (error) {
            if (gen === coGeneration && coMount === mount)
                coMessage(mount.panel, T('Не удалось загрузить совместные съёмки: ') + error.message);
        } finally { if (gen === coGeneration) coActorLoading = false; }
    }
    async function showCoScenes(mount, actor) {
        ++coGeneration;
        const gen = coGeneration;
        if (coImageObserver) { coImageObserver.disconnect(); coImageObserver = null; }
        mount.panel.replaceChildren();
        const back = coElement('button','jf-co-back','← Совместно с');
        back.type = 'button';
        back.addEventListener('click', () => {
            if (coMount !== mount) return;
            ++coGeneration;
            mount.loaded = false;
            void loadCoActors(mount);
        });
        mount.panel.append(back, coElement('h3','',actor.name || T('Совместные видео')));
        const status = coElement('div','jf-co-status','Загрузка видео…');
        const grid = coElement('div','jf-co-scenes');
        mount.panel.append(status, grid);
        async function nextPage(page) {
            status.textContent = T('Загрузка видео…');
            try {
                const result = await coGet(coPath('PersonCoStarScenes',
                    {personId:mount.personId, coStashId:actor.stashId, page}));
                if (gen !== coGeneration || coMount !== mount) return;
                const client = api();
                const serverId = client?.serverId?.() || '';
                for (const scene of result.items || []) {
                    // The API returns only verified, accessible Jellyfin video IDs.
                    const video = coElement('a','jf-co-video');
                    video.href = '#/details?id=' + encodeURIComponent(scene.jellyfinId) +
                        (serverId ? '&serverId=' + encodeURIComponent(serverId) : '');
                    const poster = coElement('img');
                    poster.loading = 'lazy';
                    poster.alt = scene.title || '';
                    poster.src = client.getUrl('Items/' + encodeURIComponent(scene.jellyfinId) +
                        '/Images/Primary', {fillHeight:320,fillWidth:215,quality:85});
                    const label = coElement('span','',scene.title || T('Видео ') + scene.sceneId);
                    video.append(poster, label);
                    if (scene.date) video.append(coElement('span','jf-co-count',scene.date));
                    grid.appendChild(video);
                }
                const shown = grid.children.length;
                const total = Number(result.count) || 0;
                status.textContent = T('Общих видео: ') + total;
                mount.panel.querySelector('.jf-co-more')?.remove();
                if (shown < total) {
                    const more = coElement('button','jf-co-more','Показать ещё');
                    more.type = 'button';
                    more.addEventListener('click', () => { more.disabled = true; void nextPage(page + 1); });
                    mount.panel.append(more);
                }
            } catch (error) {
                if (gen === coGeneration && coMount === mount)
                    status.textContent = T('Не удалось загрузить видео: ') + error.message;
            }
        }
        await nextPage(1);
    }
    function ensureCoTabs(page, person, user) {
        if (!linksConfigLoaded) return;
        if (!coTabsEnabled) { removeCoTabsIfNeeded(); return; }
        if (!isMatchingPersonContent(page, person)) return;
        const content = page.querySelector('.detailSectionContent');
        if (!content) return;
        const personId = normalized(textValue(person,'id','Id'));
        if (coMount && (coMount.content !== content || coMount.personId !== personId ||
                        !coMount.tabs.isConnected || !coMount.panel.isConnected)) removeCoTabs();
        if (coMount) return;
        const tabs = coElement('div','jf-co-tabs');
        tabs.setAttribute('role','tablist');
        const info = coElement('button','','Информация');
        info.type='button'; info.setAttribute('role','tab'); info.setAttribute('aria-selected','true');
        const co = coElement('button','','Совместно с');
        co.type='button'; co.setAttribute('role','tab'); co.setAttribute('aria-selected','false');
        tabs.append(info,co);
        const panel = coElement('div','jf-co-panel');
        panel.setAttribute('role','tabpanel');
        panel.hidden = true;
        content.prepend(tabs);
        content.append(panel);
        coMount = {content,tabs,panel,infoButton:info,coButton:co,personId,loaded:false};
        const mount = coMount;
        info.addEventListener('click', () => { if (coMount === mount) displayCoTab('info'); });
        co.addEventListener('click', () => { if (coMount === mount) displayCoTab('co'); });
        displayCoTab('info');
    }
    function removeCoTabsIfNeeded() { if (coMount) removeCoTabs(); }
    function restoreBackdrop() {
        if (!backdropState) return;
        const { node, applied, oldValue, oldPriority } = backdropState;
        node.classList.remove(BACKDROP_CLASS);
        // Restore our own inline override only. If Jellyfin has already replaced
        // the background while navigating, do not clobber its newer value.
        if (node.style.getPropertyValue('background-image') === applied) {
            if (oldValue) node.style.setProperty('background-image', oldValue, oldPriority);
            else node.style.removeProperty('background-image');
        }
        backdropState = null;
    }
    function renderBackdrop(person, page) {
        const backdrop = page.querySelector(SELECTOR_BACKDROP) || document.querySelector(SELECTOR_BACKDROP);
        const tag = imageTag(person);
        if (!backdrop || !tag) {
            restoreBackdrop();
            return;
        }
        const client = api();
        const id = normalized(textValue(person, 'id', 'Id'));
        if (!client?.getUrl || !id) return;
        const url = client.getUrl('Items/' + encodeURIComponent(id) + '/Images/Banner', {
            tag: tag, maxWidth: 1920, quality: 90
        });
        const applied = 'url("' + url.replace(/"/g, '%22') + '")';
        if (backdropState?.node === backdrop && backdropState.applied === applied) return;
        restoreBackdrop();
        backdropState = {
            node: backdrop,
            applied: applied,
            oldValue: backdrop.style.getPropertyValue('background-image'),
            oldPriority: backdrop.style.getPropertyPriority('background-image')
        };
        backdrop.classList.add(BACKDROP_CLASS);
        backdrop.style.setProperty('background-image', applied, 'important');
    }
    async function loadPerson(id, user) {
        const key = user + ':' + id;
        const cached = cache.get(key);
        if (cached && cached.expires > Date.now()) return cached.item;
        const client = api();
        if (!client?.getUrl || !client?.fetch) return null;
        const r = await client.fetch({
            url: client.getUrl('Users/' + encodeURIComponent(user) + '/Items/' + encodeURIComponent(id)),
            type: 'GET'
        });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const item = await r.json();
        cache.set(key, { item, expires: Date.now() + CACHE_MS });
        return item;
    }
    function clean() {
        restoreOverviewLinks();
        removeCoTabs();
        document.documentElement.classList.remove(ROOT_CLASS);
        restoreBackdrop();
        currentPerson = null;
    }
    async function checkRoute() {
        const id = routeId();
        const user = activeUserId();
        const key = id && user ? user + ':' + id : '';
        if (key !== currentKey) {
            currentKey = key;
            ++sequence;
            clean();
            pending = null;
        }
        if (!key) return;
        if (currentPerson) {
            const page = getDetailsPage();
            if (page) {
                document.documentElement.classList.add(ROOT_CLASS);
                unhideControls(page);
                renderBackdrop(currentPerson, page);
                ensurePersonOverviewLinks(page, currentPerson, user);
                ensureCoTabs(page, currentPerson, user);
            }
            return;
        }
        if (pending) return;
        const generation = sequence;
        pending = loadPerson(id, user)
            .then(item => {
                if (generation !== sequence || currentKey !== key) return;
                if (String(textValue(item, 'type', 'Type') || '').toLowerCase() !== 'person') {
                    // Never affect movie, series or video details pages.
                    clean();
                    return;
                }
                currentPerson = item;
                const page = getDetailsPage();
                if (page) {
                    document.documentElement.classList.add(ROOT_CLASS);
                    unhideControls(page);
                    renderBackdrop(item, page);
                    ensurePersonOverviewLinks(page, item, user);
                    ensureCoTabs(page, item, user);
                }
            })
            .catch(() => { /* Jellyfin may be signing in or moving between pages. */ })
            .finally(() => { if (generation === sequence) pending = null; });
        return pending;
    }
    function schedule() {
        if (timer !== null) return;
        timer = setTimeout(() => { timer = null; void checkRoute(); }, 80);
    }
    function start() {
        injectCss();
        new MutationObserver(schedule).observe(document.body, { childList: true, characterData: true, subtree: true });
        window.addEventListener('hashchange', schedule);
        window.addEventListener('popstate', schedule);
        void checkRoute();
        setInterval(() => { if (routeId()) void checkRoute(); }, 1500);
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
})();
