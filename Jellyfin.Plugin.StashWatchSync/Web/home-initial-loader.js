/* JF To Stash Sync — atomic Home presentation for Jellyfin's SPA.
 * First completed Home visit: full-screen loader while enabled custom rows and
 * banners prepare. Later visits: hide only the changing Home section host until
 * cached components are attached, never display a second full-screen loader.
 * Also handles a first Home visit AFTER reloading on Settings / any other route.
 */
(function () {
    'use strict';
    const T = (text) => window.__jfToStashI18n?.t(text) ?? String(text ?? '');
    if (window.__jfStashHomeLoader) return;

    const ID = 'jf-stash-first-home-loader';
    const STAGE_CLASS = 'jf-stash-home-staging';
    const HOST_CLASS = 'jf-stash-home-host-pending';
    const stagedHosts = new Set();
    const MAX_FIRST_WAIT_MS = 24000;
    const MAX_RETURN_WAIT_MS = 3500;
    const MODULES = Object.freeze({
        trending: '#jf-stashdb-trending-section',
        popularity: '#jf-stashdb-popularity-section',
        performers: '[data-jf-spp-row="true"]',
        babepedia: '[data-jf-bpt-row="true"]',
        actor: '#jf-phhero-section',
        media: '#jf-mediahero-section'
    });
    const settled = new Set();
    let expected = new Set();
    let configurationLoaded = false;
    let configRunning = false;
    let homeActive = false;
    let firstCompleted = false;
    let firstVisit = false;
    let enteringAt = 0;
    let generation = 0;
    let currentHost = null;
    let readyCandidateAt = 0;
    let finishPending = false;
    let checkTimer = 0;
    let configTimer = 0;
    let fadeTimer = 0;
    let framePending = false;
    let lastProgress = 0;
    let lastUser = '';

    function onHome() {
        const fragment = String(location.hash || '').replace(/^#!?/, '');
        const route = fragment || String(location.pathname || '');
        const queryAt = route.indexOf('?');
        const path = queryAt < 0 ? route : route.slice(0, queryAt);
        if (!/(?:^|\/)home\/?$/i.test(path)) return false;
        const query = queryAt < 0 ? (!fragment ? String(location.search || '').replace(/^\?/, '') : '') : route.slice(queryAt + 1);
        const tab = new URLSearchParams(query).get('tab');
        return tab === null || tab === '' || tab === '0';
    }
    function api() { return window.ApiClient; }
    function get(o, camel, pascal, fallback) {
        return o && o[camel] !== undefined ? o[camel] :
            o && o[pascal] !== undefined ? o[pascal] : fallback;
    }
    function userId() {
        try { return api()?.getCurrentUserId?.() || ''; } catch (_) { return ''; }
    }
    function isVisible(el) {
        // Opacity deliberately does NOT count: the loader stages the Home host
        // at opacity:0, and all other row scripts must still be able to find it.
        return !!(el && el.isConnected && el.getClientRects().length &&
            getComputedStyle(el).display !== 'none' && getComputedStyle(el).visibility !== 'hidden');
    }
    function host() {
        if (!onHome()) return null;
        for (const selector of [
            '.homeSectionsContainer', '.homePage .homeSections',
            '.homePage .sectionsContainer', '.homeView .homeSectionsContainer'
        ]) {
            const found = Array.from(document.querySelectorAll(selector)).find(isVisible);
            if (found) return found;
        }
        const home = Array.from(document.querySelectorAll('.homePage,.homeView'))
            .find(el => isVisible(el) && el.querySelector('.verticalSection'));
        if (home) return home.querySelector('.verticalSection')?.parentElement || null;
        // Match the same modern-Home fallback used by the Trending/Popularity rows.
        const main = Array.from(document.querySelectorAll('main,[role="main"]'))
            .find(el => isVisible(el) && el.querySelector('.MuiBox-root,.MuiStack-root,.MuiContainer-root'));
        return main?.querySelector('.MuiContainer-root') || main || null;
    }
    function addStyles() {
        if (document.getElementById(ID + '-style')) return;
        const style = document.createElement('style');
        style.id = ID + '-style';
        style.textContent = `
          /* Hide incomplete Home layout before banner/row insertion can paint.
             opacity (not visibility/display) preserves layout measurements and
             the host's discoverability by all existing row scripts. */
          html.${STAGE_CLASS} .homeSectionsContainer,
          html.${STAGE_CLASS} .homePage .homeSections,
          html.${STAGE_CLASS} .homePage .sectionsContainer,
          html.${STAGE_CLASS} .homeView .homeSectionsContainer,
          html.${STAGE_CLASS} .homePage .MuiContainer-root,
          html.${STAGE_CLASS} .homeView .MuiContainer-root,
          .${HOST_CLASS} {
            opacity:0!important;pointer-events:none!important;
          }
          #${ID}{position:fixed;inset:0;z-index:99999999;display:flex;align-items:center;justify-content:center;
            box-sizing:border-box;background:var(--theme-body-background,#111);color:var(--text-color,#eee);
            font-family:inherit;opacity:1;transition:opacity .28s ease;pointer-events:auto}
          #${ID}.jf-stash-loader-leaving{opacity:0;pointer-events:none}
          #${ID} .jf-stash-loading-body{display:flex;flex-direction:column;align-items:center;justify-content:center;
            gap:18px;width:min(360px,85vw);text-align:center}
          #${ID} .jf-stash-loading-symbol{width:65px;height:65px;border-radius:22px;
            background:var(--theme-primary-color,#528bb7);display:flex;align-items:center;justify-content:center;
            color:#fff;font-size:33px;box-shadow:0 8px 30px #0005}
          #${ID} .jf-stash-loading-title{font-size:1.2rem;font-weight:650;letter-spacing:.02em}
          #${ID} .jf-stash-loading-label{font-size:.9rem;opacity:.7;min-height:1.3em}
          #${ID} .jf-stash-loading-track{height:5px;width:100%;border-radius:9px;background:#7a7a7a65;overflow:hidden}
          #${ID} .jf-stash-loading-fill{height:100%;width:0;background:var(--theme-primary-color,#86b6dd);
            transition:width .25s ease;border-radius:9px}
          @media(prefers-reduced-motion:reduce){#${ID},#${ID} .jf-stash-loading-fill{transition:none}}
        `;
        (document.head || document.documentElement).appendChild(style);
    }
    function stageHost(root) {
        if (!root || !root.isConnected || root.classList.contains(HOST_CLASS)) return;
        root.classList.add(HOST_CLASS);
        stagedHosts.add(root);
    }
    function stage() {
        addStyles();
        document.documentElement.classList.add(STAGE_CLASS);
        stageHost(host());
    }
    function unstage() {
        document.documentElement.classList.remove(STAGE_CLASS);
        for (const node of stagedHosts) node.classList.remove(HOST_CLASS);
        stagedHosts.clear();
    }
    function showCurtain() {
        if (!firstVisit || !document.body || !onHome() || document.getElementById(ID)) return;
        const curtain = document.createElement('div');
        curtain.id = ID;
        curtain.setAttribute('role', 'status');
        curtain.setAttribute('aria-live', 'polite');
        const body = document.createElement('div');
        body.className = 'jf-stash-loading-body';
        const symbol = document.createElement('div');
        symbol.className = 'jf-stash-loading-symbol';
        symbol.textContent = '▶';
        const title = document.createElement('div');
        title.className = 'jf-stash-loading-title';
        title.textContent = 'Jellyfin';
        const label = document.createElement('div');
        label.className = 'jf-stash-loading-label';
        label.textContent = T('Подготовка главной страницы…');
        const track = document.createElement('div');
        track.className = 'jf-stash-loading-track';
        const bar = document.createElement('div');
        bar.className = 'jf-stash-loading-fill';
        track.appendChild(bar);
        body.append(symbol, title, label, track);
        curtain.appendChild(body);
        document.body.appendChild(curtain);
    }
    function hideCurtain(immediate) {
        const curtain = document.getElementById(ID);
        if (!curtain) return;
        if (fadeTimer) clearTimeout(fadeTimer);
        if (immediate) { curtain.remove(); return; }
        curtain.querySelector('.jf-stash-loading-fill')?.style.setProperty('width','100%');
        curtain.classList.add('jf-stash-loader-leaving');
        fadeTimer = setTimeout(() => { curtain.remove(); fadeTimer = 0; }, 300);
    }
    function sectionsMounted(root) {
        if (!root) return false;
        for (const key of expected) {
            if (!root.querySelector(MODULES[key])) return false;
        }
        const media = expected.has('media') ? root.querySelector(MODULES.media) : null;
        const actor = expected.has('actor') ? root.querySelector(MODULES.actor) : null;
        // A native Home section must not temporarily appear ABOVE the banners.
        // This is the visible jump that previously occurred during SPA returns.
        const native = Array.from(root.children).find(el =>
            el.classList?.contains('verticalSection') &&
            el.id !== 'jf-mediahero-section' && el.id !== 'jf-phhero-section' &&
            el.id !== 'jf-stashdb-trending-section' && el.id !== 'jf-stashdb-popularity-section' &&
            !el.classList.contains('jf-spp-section') && !el.classList.contains('jf-bpt-section'));
        const follows = (a,b) => !!(a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING);
        if (media && actor && !follows(media,actor)) return false;
        if (native && media && !follows(media,native)) return false;
        if (native && actor && !follows(actor,native)) return false;
        // The server's Recently Added / Continue Watching rows belong after
        // all custom ranking rows, not just after the two featured banners.
        if (native) {
            for (const key of expected) {
                if (key === 'actor' || key === 'media') continue;
                const node = root.querySelector(MODULES[key]);
                const section = node?.closest('.verticalSection') || node;
                if (section && section !== native && !follows(section,native)) return false;
            }
        }
        return true;
    }
    function updateProgress() {
        const curtain = document.getElementById(ID);
        if (!curtain || !configurationLoaded) return;
        const count = [...expected].filter(k => settled.has(k)).length;
        const percent = expected.size ? Math.floor(100*count/expected.size) : 100;
        lastProgress = Math.max(lastProgress, percent);
        curtain.querySelector('.jf-stash-loading-fill')?.style.setProperty('width',lastProgress + '%');
        const text = curtain.querySelector('.jf-stash-loading-label');
        if (text) text.textContent = count < expected.size ?
            `Загрузка разделов: ${count} / ${expected.size}` : 'Размещение баннеров и списков…';
    }
    function finish(reason) {
        if (!homeActive) return;
        const wasFirst = firstVisit;
        if (wasFirst) firstCompleted = true;
        if (reason === 'timeout') {
            console.warn('[JFToStashSync] Home preparation timed out; displaying available sections.');
        }
        if (checkTimer) { clearInterval(checkTimer); checkTimer = 0; }
        enteringAt = 0;
        readyCandidateAt = 0;
        finishPending = false;
        firstVisit = false;
        // Reveal the fully placed content in the same frame. The first-load
        // curtain fades over the already complete Home DOM.
        unstage();
        if (wasFirst) hideCurtain(false);
    }
    async function preloadImages(root, requestId) {
        const imgs = [...root.querySelectorAll(
            '#jf-mediahero-section .jf-mediahero-backdrop,' +
            '#jf-mediahero-section .jf-mediahero-logo,' +
            '#jf-phhero-section .jf-phhero-logo')];
        await Promise.race([
            Promise.all(imgs.map(img => img.complete ? Promise.resolve() : new Promise(resolve => {
                img.addEventListener('load', resolve, {once:true});
                img.addEventListener('error', resolve, {once:true});
            }))),
            new Promise(resolve => setTimeout(resolve, 1500))
        ]);
        return homeActive && requestId === generation;
    }
    function check() {
        if (!homeActive || !enteringAt || finishPending) return;
        if (!onHome()) { leave(); return; }
        showCurtain();
        const root = host();
        stageHost(root);
        if (root !== currentHost) {
            currentHost = root;
            readyCandidateAt = 0;
        }
        if (Date.now() - enteringAt >= (firstVisit ? MAX_FIRST_WAIT_MS : MAX_RETURN_WAIT_MS)) {
            finish('timeout');
            return;
        }
        if (!configurationLoaded) return;
        updateProgress();
        if (firstVisit && [...expected].some(key => !settled.has(key))) return;
        if (!sectionsMounted(root)) { readyCandidateAt = 0; return; }
        // The banners' mount observers run in microtasks or animation frames.
        // Two stable frames avoid revealing the Home between reordering passes.
        if (!readyCandidateAt) { readyCandidateAt = Date.now(); return; }
        if (Date.now() - readyCandidateAt < 45) return;
        finishPending = true;
        const requestId = generation;
        requestAnimationFrame(() => requestAnimationFrame(async () => {
            if (!homeActive || requestId !== generation) { finishPending = false; return; }
            if (!sectionsMounted(host())) { finishPending = false; readyCandidateAt = 0; return; }
            if (firstVisit) await preloadImages(host(), requestId);
            if (!homeActive || requestId !== generation) { finishPending = false; return; }
            if (!sectionsMounted(host())) { finishPending = false; readyCandidateAt = 0; return; }
            finish('ready');
        }));
    }
    async function readConfig() {
        if (configRunning || configurationLoaded || !homeActive) return;
        const client = api();
        if (!client?.fetch || !client?.getUrl || !userId()) return;
        const requestId = generation;
        configRunning = true;
        try {
            const config = window.__jfHomeWarmup?.getConfig
                ? await window.__jfHomeWarmup.getConfig()
                : await (async () => {
                    const response = await client.fetch({url:client.getUrl('JFToStashSync/PlayerOCounterConfig'),type:'GET'});
                    if (!response.ok) throw new Error('HTTP ' + response.status);
                    return response.json();
                })();
            if (!homeActive || requestId !== generation) return;
            const on = (camel,pascal) => !!get(config,camel,pascal,false);
            const selected = new Set();
            if (on('trendingEnabled','TrendingEnabled')) selected.add('trending');
            if (on('popularityEnabled','PopularityEnabled')) selected.add('popularity');
            if (on('performerPopularityEnabled','PerformerPopularityEnabled') &&
                (get(config,'performerPopularityGenders','PerformerPopularityGenders',[]) || []).length) selected.add('performers');
            if (on('babepediaTopEnabled','BabepediaTopEnabled') &&
                (get(config,'babepediaTopCategories','BabepediaTopCategories',[]) || []).length) selected.add('babepedia');
            if (on('featuredPornhubBannerEnabled','FeaturedPornhubBannerEnabled')) selected.add('actor');
            if (on('featuredMediaEnabled','FeaturedMediaEnabled')) selected.add('media');
            expected = selected;
            configurationLoaded = true;
            lastUser = userId();
            if (configTimer) { clearInterval(configTimer); configTimer = 0; }
            updateProgress();
            check();
        } catch (error) {
            console.debug('[JFToStashSync] Home configuration is not available yet:',error?.message);
        } finally { configRunning = false; }
    }
    function enter() {
        if (homeActive) return;
        ++generation;
        homeActive = true;
        firstVisit = !firstCompleted;
        currentHost = null;
        enteringAt = Date.now();
        readyCandidateAt = 0;
        finishPending = false;
        lastProgress = 0;
        stage(); // Before Jellyfin gets a chance to paint the new Home sections.
        showCurtain();
        if (checkTimer) clearInterval(checkTimer);
        checkTimer = setInterval(check, 110);
        if (!configTimer) configTimer = setInterval(readConfig, 550);
        readConfig();
        check();
    }
    function leave() {
        if (!homeActive) return;
        ++generation;
        homeActive = false;
        firstVisit = false;
        enteringAt = 0;
        currentHost = null;
        readyCandidateAt = 0;
        finishPending = false;
        if (checkTimer) { clearInterval(checkTimer); checkTimer = 0; }
        if (configTimer) { clearInterval(configTimer); configTimer = 0; }
        unstage();
        hideCurtain(true);
        // Navigating away before First Home was ready MUST NOT permanently
        // consume the one-time loading screen.
    }
    function navigate() {
        const user = userId();
        if (lastUser && user && user !== lastUser) {
            ++generation;
            configurationLoaded = false;
            expected = new Set();
            settled.clear();
            firstCompleted = false;
            if (homeActive) leave();
        }
        if (onHome()) enter();
        else leave();
        if (homeActive && !enteringAt) enter();
        if (homeActive && !firstVisit && firstCompleted && !checkTimer && !finishPending) {
            // On a Home host replacement without a route change, re-stage
            // before native-only content can paint in a new SPA container.
            const root = host();
            if (root !== currentHost || !sectionsMounted(root)) {
                leave();
                enter();
            }
        }
        if (homeActive && enteringAt) check();
    }
    window.__jfStashHomeLoader = {
        mark(name) {
            if (name && Object.prototype.hasOwnProperty.call(MODULES,name)) {
                settled.add(name);
                if (homeActive) check();
            }
        },
        status: () => ({
            ready:[...settled], expected:[...expected], completed:firstCompleted,
            active:homeActive, staging:document.documentElement.classList.contains(STAGE_CLASS)
        })
    };
    // Install route observers even if the first page is Settings. They must
    // continue running after the first completed Home visit to stage SPA returns.
    window.addEventListener('hashchange', navigate, true);
    window.addEventListener('popstate', navigate, true);
    document.addEventListener('viewshow', navigate, true);
    if (document.body) {
        new MutationObserver(() => {
            // MutationObserver runs before the next paint, including when the
            // browser navigates using an SPA router without a hashchange event.
            if (onHome() !== homeActive) navigate();
            else if (homeActive && enteringAt) {
                // A new Home container can be inserted after the hashchange.
                // Stage it in the MutationObserver microtask, before first paint.
                stageHost(host());
            } else if (homeActive && !enteringAt && !firstVisit && firstCompleted) {
                // Root may be replaced while the URL stays on /home.
                const root = host();
                if (!root || !sectionsMounted(root)) navigate();
            }
        }).observe(document.body,{childList:true,subtree:true});
    }
    // Stage immediately during script evaluation (not only DOMContentLoaded).
    navigate();
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', navigate, {once:true});
})();
