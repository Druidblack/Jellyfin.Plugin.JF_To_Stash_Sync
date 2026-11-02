/* JF To Stash Sync — featured Pornhub-style actor banner above custom home rows */
(function () {
    'use strict';
    const T = (text) => window.__jfToStashI18n?.t(text) ?? String(text ?? '');
    if (window.__jfPornhubFeaturedBannerLoaded) return;
    window.__jfPornhubFeaturedBannerLoaded = true;

    const PREFIX = 'jf-phhero-';
    const SECTION_ID = PREFIX + 'section';
    const BATCH_SIZE = 60;
    const CACHE_MS = 5 * 60 * 1000;
    const COUNTRY_CHUNK = 80;
    const state = {
        userId: '',
        enabled: false,
        performerPopularityEnabled: false,
        order: 'Random',
        rankingSource: 'Pornhub',
        showFields: {},
        slideSeconds: 15,
        maxItems: 30,
        showProgress: true,
        pauseOnHover: false,
        remainingMs: 0,
        timerStartedAt: 0,
        highlightFavorites: false,
        candidates: [],
        countries: new Map(),
        stats: new Map(),
        babepedia: new Map(),
        expires: 0,
        running: null,
        generation: 0,
        index: 0,
        timer: null,
        rotateAt: 0,
        hovered: false,
        error: ''
    };
    let mountPending = false;
    // Preserve the mounted, fully rendered banner between Jellyfin SPA pages.
    let retainedSection = null;

    function api() { return window.ApiClient; }
    function val(obj, camel, pascal, fallback) {
        return obj && obj[camel] !== undefined ? obj[camel] :
            obj && obj[pascal] !== undefined ? obj[pascal] : fallback;
    }
    function normalizedId(value) { return String(value || '').replace(/-/g, '').toLowerCase(); }
    function userId() {
        try { return api() && api().getCurrentUserId() || ''; }
        catch (_) { return ''; }
    }
    async function fetchJson(path) {
        const client = api();
        if (!client || !client.fetch || !client.getUrl) throw new Error('Jellyfin API is unavailable.');
        const response = await client.fetch({ url: client.getUrl(path), type: 'GET' });
        let body = null;
        try { body = await response.json(); } catch (_) { /* ignore */ }
        if (!response.ok) throw new Error((body && (body.message || body.Message)) || ('HTTP ' + response.status));
        return body;
    }
    function visible(el) {
        return !!(el && el.isConnected && el.getClientRects().length &&
            getComputedStyle(el).visibility !== 'hidden' && getComputedStyle(el).display !== 'none');
    }
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
    function root() {
        if (!isMainHomeTab()) return null;
        for (const selector of ['.homeSectionsContainer', '.homePage .homeSections', '.homePage .sectionsContainer', '.homeView .homeSectionsContainer']) {
            const found = Array.from(document.querySelectorAll(selector)).find(visible);
            if (found) return found;
        }
        const home = Array.from(document.querySelectorAll('.homePage, .homeView')).find(el => visible(el) && el.querySelector('.verticalSection'));
        if (home) return home.querySelector('.verticalSection').parentElement;
        return null;
    }
    function firstCustomRow(host) {
        if (!host) return null;
        // querySelector returns the first matching row in real DOM order.
        return host.querySelector('#jf-stashdb-trending-section, #jf-stashdb-popularity-section, .jf-spp-section, .jf-bpt-section');
    }
    function ensureStyles() {
        if (document.getElementById(PREFIX + 'styles')) return;
        const style = document.createElement('style');
        style.id = PREFIX + 'styles';
        style.textContent = `
          .${PREFIX}section{margin:1.4em 0 1.8em;padding:0 3.3%;box-sizing:border-box;min-width:0}
          .${PREFIX}card{position:relative;min-height:360px;border-radius:16px;overflow:hidden;background:#141414;color:#fff;box-shadow:0 10px 32px rgba(0,0,0,.34)}
          .${PREFIX}card::before{content:"";position:absolute;inset:0;background:var(--${PREFIX}bg) center/cover no-repeat;
            transform-origin:center center;animation:${PREFIX}kenBurns var(--${PREFIX}zoom-duration,15000ms) cubic-bezier(.25,.1,.25,1) both;
            backface-visibility:hidden;will-change:transform}
          @keyframes ${PREFIX}kenBurns{from{transform:scale(1)}to{transform:scale(1.10)}}
          .${PREFIX}card.${PREFIX}zoomPaused::before{animation-play-state:paused}
          .${PREFIX}shade{position:absolute;inset:0;background:linear-gradient(180deg,rgba(8,8,8,.16),rgba(8,8,8,.48) 42%,rgba(8,8,8,.84) 100%)}
          .${PREFIX}content{display:flex;text-decoration:none !important;color:#fff !important;position:relative;display:flex;flex-direction:column;justify-content:space-between;min-height:360px;padding:1.1rem 1.35rem 1rem;gap:1rem}
          .${PREFIX}top{display:flex;align-items:flex-start;justify-content:flex-end;gap:.55rem}
          .${PREFIX}nav{display:flex;gap:.45rem;align-items:center}
          .${PREFIX}btn{appearance:none;border:0;background:rgba(20,20,20,.55);color:#fff;padding:.5rem .72rem;border-radius:999px;cursor:pointer;font:inherit;backdrop-filter:blur(8px)}
          .${PREFIX}btn:hover{background:rgba(20,20,20,.72)}
          .${PREFIX}body{display:flex;flex-wrap:wrap;align-items:flex-end;justify-content:space-between;gap:1rem 1.25rem}
          .${PREFIX}identity{display:flex;flex-direction:column;align-items:flex-start;gap:.55rem;max-width:min(700px,100%)}
          .${PREFIX}logo{display:block;max-width:min(720px,85vw);max-height:172px;object-fit:contain;object-position:left center;border-radius:8px;filter:drop-shadow(0 2px 10px rgba(0,0,0,.45))}
          .${PREFIX}logo.${PREFIX}favorite{outline:2px solid rgba(236,139,145,.8);outline-offset:-2px;box-shadow:0 0 0 2px rgba(236,139,145,.14)}
          .${PREFIX}nameRow{display:flex;align-items:center;gap:.55rem;flex-wrap:wrap}
          .${PREFIX}name{margin:0;font-size:clamp(1.65rem,3.25vw,2.7rem);line-height:1.08;font-weight:700;text-decoration:none !important;text-shadow:0 2px 8px rgba(0,0,0,.45)}
          .${PREFIX}flag{width:1.55rem;height:1.1rem;display:inline-block;border-radius:2px;box-shadow:0 0 0 1px rgba(255,255,255,.14)}
          .${PREFIX}badges{display:flex;gap:.45rem;flex-wrap:wrap}
          .${PREFIX}badge{display:inline-flex;align-items:center;justify-content:center;border-radius:50%;width:1.65rem;height:1.65rem;font-size:1.1rem;line-height:1;color:#fff;box-sizing:border-box;text-shadow:0 1px 2px #0009}
          .${PREFIX}badge.${PREFIX}verified{background:#2587e4;color:#fff;font-size:1.1rem;font-weight:900}
          .${PREFIX}badge.${PREFIX}award{background:rgba(10,10,10,.32);color:#ffe49b}
          .${PREFIX}stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(100px,1fr));gap:.6rem;min-width:min(100%,540px);max-width:640px}
          .${PREFIX}box{display:flex;flex-direction:column;align-items:center;justify-content:center;padding:.72rem .65rem .68rem;border-radius:10px;background:rgba(12,12,12,.52);backdrop-filter:blur(8px);text-align:center;min-height:78px}
          .${PREFIX}box[title]{cursor:help}
          .${PREFIX}big{font-size:1.42rem;font-weight:700;line-height:1.1}
          .${PREFIX}title{font-size:.82rem;line-height:1.22;opacity:.88;margin-top:.24rem}
          .${PREFIX}empty{font-size:.88em;opacity:.82}
          .${PREFIX}navFloating{position:absolute;top:1rem;right:1rem;z-index:5;display:flex;align-items:center;gap:.5rem}
          .${PREFIX}counter{font-size:.83em;padding:.5rem .7rem;background:rgba(20,20,20,.55);border-radius:999px;color:#fff;backdrop-filter:blur(8px);white-space:nowrap;font-variant-numeric:tabular-nums}
          .${PREFIX}progress{position:absolute;bottom:0;left:0;right:0;height:3px;background:rgba(255,255,255,.22);overflow:hidden;z-index:6;pointer-events:none}
          .${PREFIX}progressfill{height:100%;width:0%;background:rgba(255,255,255,.88);transition:none}
          /* The featured banner's enabled Ken Burns effect also applies when
             the browser reports reduced motion; keep it user-controlled. */
          @media (max-width: 900px){
            .${PREFIX}content{padding:1rem 1rem .95rem}
            .${PREFIX}body{align-items:stretch}
            .${PREFIX}stats{max-width:none;min-width:100%}
          }
          @media (max-width: 640px){
            .${PREFIX}card,.${PREFIX}content{min-height:unset}
            .${PREFIX}logo{max-width:82vw;max-height:144px}
            .${PREFIX}name{font-size:1.55rem}
            .${PREFIX}stats{grid-template-columns:repeat(2,minmax(0,1fr))}
            .${PREFIX}section{padding:0 3%}
            .${PREFIX}top{justify-content:space-between}
          }
        `;
        document.head.appendChild(style);
    }
    function imageTag(person, type) {
        const tags = val(person, 'imageTags', 'ImageTags', {}) || {};
        return tags[type] || tags[type.toLowerCase()] || '';
    }
    function imageUrl(person, type, maxWidth) {
        const client = api();
        if (!client || !person) return '';
        const itemId = val(person, 'id', 'Id', '');
        const tag = imageTag(person, type);
        if (!itemId || !tag) return '';
        const options = { tag: tag, quality: 90 };
        if (maxWidth) options.maxWidth = maxWidth;
        try {
            return client.getUrl('Items/' + encodeURIComponent(itemId) + '/Images/' + type, options);
        } catch (_) {
            return '';
        }
    }
    function detailsHref(personId) {
        return (location.hash.startsWith('#!') ? '#!/details?id=' : '#/details?id=') + encodeURIComponent(personId);
    }
    function compact(value) {
        const n = Number(value);
        if (!Number.isFinite(n) || n < 0) return String(value || '—');
        if (n >= 1e9) return (n / 1e9).toFixed(1).replace(/\.0$/, '') + 'B';
        if (n >= 1e6) return (n / 1e6).toFixed(1).replace(/\.0$/, '') + 'M';
        if (n >= 1e3) return (n / 1e3).toFixed(1).replace(/\.0$/, '') + 'K';
        return String(n);
    }
    function fmt(value) {
        const n = Number(value);
        return Number.isFinite(n) && n >= 0 ? n.toLocaleString() : String(value || '—');
    }
    function valueOrDash(value) {
        return value === null || value === undefined || value === '' ? '—' : String(value);
    }
    const METRIC_FIELDS = [
        ['PornhubModelRank', 'modelRank', 'ModelRank', 'Model rank', 'rank', 'ph'],
        ['PornhubWeeklyRank', 'weeklyRank', 'WeeklyRank', 'Weekly rank', 'rank', 'ph'],
        ['PornhubMonthlyRank', 'monthlyRank', 'MonthlyRank', 'Monthly rank', 'rank', 'ph'],
        ['PornhubLastMonthRank', 'lastMonthRank', 'LastMonthRank', 'Last month', 'rank', 'ph'],
        ['PornhubYearlyRank', 'yearlyRank', 'YearlyRank', 'Yearly rank', 'rank', 'ph'],
        ['PornhubVideoViews', 'videoViews', 'VideoViews', 'Video views', 'counter', 'ph'],
        ['PornhubSubscribers', 'subscribers', 'Subscribers', 'Subscribers', 'counter', 'ph'],
        ['BabepediaRating', 'rating', 'Rating', 'Babepedia rating', 'decimal', 'bp'],
        ['BabepediaRank', 'rank', 'Rank', 'Babepedia rank', 'rank', 'bp'],
        ['BabepediaFavorites', 'favorites', 'Favorites', 'Babepedia favorites', 'counter', 'bp'],
        ['BabepediaVotes', 'votes', 'Votes', 'Babepedia votes', 'counter', 'bp']
    ];
    function rankScore(candidate) {
        const source = state.rankingSource === 'Babepedia' ? candidate.babepedia : candidate.stat;
        if (!source) return null;
        if (state.rankingSource === 'Babepedia') {
            const rating = Number(val(source, 'rating', 'Rating', NaN));
            if (Number.isFinite(rating)) return { group: 0, value: -rating };
            const rank = Number(val(source, 'rank', 'Rank', NaN));
            if (Number.isFinite(rank) && rank > 0) return { group: 1, value: rank };
            return null;
        }
        const rank = Number(val(source, 'modelRank', 'ModelRank', NaN));
        return Number.isFinite(rank) && rank > 0 ? { group: 0, value: rank } : null;
    }
    function arrange(values) {
        if (state.order !== 'Ranked') return shuffle(values);
        // For ordinal ranks #1 is higher quality than #100; for scores a larger value is better.
        return values.slice().sort(function (a, b) {
            const av = rankScore(a), bv = rankScore(b);
            if (!av && !bv) return String(val(a.person, 'name', 'Name', '')).localeCompare(String(val(b.person, 'name', 'Name', '')));
            if (!av) return 1;
            if (!bv) return -1;
            return av.group - bv.group || av.value - bv.value || String(val(a.person, 'name', 'Name', '')).localeCompare(String(val(b.person, 'name', 'Name', '')));
        });
    }
    function titleOrNull(lines) {
        const text = lines.filter(Boolean).join('\n').trim();
        return text || '';
    }
    function iso2ToFlagUrl(iso2) {
        const value = String(iso2 || '').trim().toUpperCase();
        if (!/^[A-Z]{2}$/.test(value)) return '';
        const cps = [];
        for (const ch of value) cps.push((0x1F1E6 + ch.charCodeAt(0) - 65).toString(16));
        return 'https://cdnjs.cloudflare.com/ajax/libs/twemoji/14.0.2/svg/' + cps.join('-') + '.svg';
    }
    function createBox(big, title, tooltip) {
        const box = document.createElement('div');
        box.className = PREFIX + 'box';
        if (tooltip) box.title = tooltip;
        const num = document.createElement('div');
        num.className = PREFIX + 'big';
        num.textContent = big;
        const ttl = document.createElement('div');
        ttl.className = PREFIX + 'title';
        ttl.textContent = T(title);
        box.appendChild(num);
        box.appendChild(ttl);
        return box;
    }
    function shuffle(values) {
        const copy = values.slice();
        for (let i = copy.length - 1; i > 0; i--) {
            const j = Math.floor(Math.random() * (i + 1));
            const t = copy[i];
            copy[i] = copy[j];
            copy[j] = t;
        }
        return copy;
    }
    function durationMs() { return state.slideSeconds * 1000; }
    function updateProgress(remainingMs, running) {
        const fill = document.querySelector('#' + SECTION_ID + ' .' + PREFIX + 'progressfill');
        if (!fill) return;
        const total = durationMs();
        const percent = Math.max(0, Math.min(100, 100 * (1 - remainingMs / total)));
        fill.style.transition = 'none';
        fill.style.width = percent + '%';
        if (running && remainingMs > 0) {
            // Force a reflow to apply the starting width before the transition.
            void fill.offsetWidth;
            fill.style.transition = 'width ' + remainingMs + 'ms linear';
            fill.style.width = '100%';
        }
    }
    function syncZoomPlayback() {
        const card = retainedSection?.querySelector('.' + PREFIX + 'card');
        if (card) card.classList.toggle(PREFIX + 'zoomPaused',
            document.visibilityState === 'hidden' || !isMainHomeTab() || (state.pauseOnHover && state.hovered));
    }
    function stopRotation() {
        if (state.timer) clearTimeout(state.timer);
        state.timer = null;
        state.timerStartedAt = 0;
        state.rotateAt = 0;
        state.remainingMs = 0;
    }
    function pauseRotation() {
        if (!state.timer) return;
        const remaining = Math.max(1, state.rotateAt - Date.now());
        clearTimeout(state.timer);
        state.timer = null;
        state.timerStartedAt = 0;
        state.rotateAt = 0;
        state.remainingMs = remaining;
        updateProgress(remaining, false);
        syncZoomPlayback();
    }
    function resumeRotation() {
        if (state.timer || !state.enabled || state.candidates.length <= 1 ||
            !document.getElementById(SECTION_ID) || document.visibilityState === 'hidden' ||
            !isMainHomeTab() || (state.pauseOnHover && state.hovered)) return;
        const ms = state.remainingMs > 0 ? state.remainingMs : durationMs();
        state.remainingMs = ms;
        state.timerStartedAt = Date.now();
        state.rotateAt = Date.now() + ms;
        updateProgress(ms, true);
        syncZoomPlayback();
        state.timer = setTimeout(function () {
            state.timer = null;
            if (!isMainHomeTab() || (state.pauseOnHover && state.hovered)) {
                state.remainingMs = 1;
                return;
            }
            if (!state.candidates.length) return;
            state.index = (state.index + 1) % state.candidates.length;
            paint();
        }, ms);
    }
    function scheduleRotation() {
        stopRotation();
        if (!state.enabled || state.candidates.length <= 1 || !document.getElementById(SECTION_ID)) return;
        state.remainingMs = durationMs();
        updateProgress(state.remainingMs, false);
        resumeRotation();
    }
    function removeSection() {
        // Detach, do not destroy. The user sees the previous banner immediately
        // when Jellyfin creates a new Home container.
        pauseRotation();
        state.hovered = false;
        syncZoomPlayback();
        const existing = document.getElementById(SECTION_ID);
        if (existing) { retainedSection = existing; existing.remove(); }
    }
    function preloadActorImages() {
        if (!state.candidates.length) return;
        const count = state.candidates.length;
        const indices = [state.index, (state.index+1)%count, (state.index+2)%count, (state.index+count-1)%count];
        window.__jfHomeWarmup?.preloadImages?.(indices.flatMap(i => {
            const c = state.candidates[i];
            return [c.banner,c.logo];
        }));
    }
    function updateVisibleFlag() {
        const candidate = currentCandidate();
        const row = document.querySelector('#'+SECTION_ID+' .'+PREFIX+'nameRow');
        if (!candidate || !row) return;
        const existing = row.querySelector('.'+PREFIX+'flag');
        if (existing) existing.remove();
        if (candidate.country && candidate.countryUrl) {
            const flag = document.createElement('img');
            flag.className = PREFIX+'flag';
            flag.alt = candidate.country;
            flag.src = candidate.countryUrl;
            flag.title = candidate.birthPlace || candidate.country;
            row.insertBefore(flag,row.querySelector('.'+PREFIX+'badge'));
        }
        const section = document.getElementById(SECTION_ID);
        if (section) {
            // Country is included in render signature. Keep it in sync after
            // the in-place update to avoid a later full rerender.
            const next = currentCandidate();
            section.dataset.renderSignature = JSON.stringify([
                next.id,next.banner,next.logo,next.country,next.stat,next.babepedia,
                state.showFields,state.candidates.length,state.showProgress,state.slideSeconds,state.pauseOnHover
            ]);
        }
    }
    function currentCandidate() {
        if (!state.candidates.length) return null;
        if (state.index >= state.candidates.length) state.index = 0;
        return state.candidates[state.index] || null;
    }

    function syncLogoFavoriteOutline() {
        const section = document.getElementById(SECTION_ID);
        const logo = section?.querySelector('.' + PREFIX + 'logo');
        if (!logo) return;
        const candidate = currentCandidate();
        // Favorite state must always belong to the current Jellyfin user.
        const isFavorite = !!(state.enabled && state.highlightFavorites && candidate &&
            typeof window.__jfToStashIsFavoriteActor === 'function' &&
            window.__jfToStashIsFavoriteActor(candidate.id, state.userId));
        logo.classList.toggle(PREFIX + 'favorite', isFavorite);
    }
    function mountSection() {
        const host = root();
        if (!host) return null;
        let section = document.getElementById(SECTION_ID) || retainedSection;
        // When ranking rows are disabled (or have not mounted yet), keep the
        // hero BEFORE Jellyfin's native Home sections, never append it below.
        const anchor = firstCustomRow(host) || Array.from(host.children).find(el =>
            el !== section && el.id !== 'jf-mediahero-section' && el.id !== SECTION_ID) || null;
        if (!section) {
            section = document.createElement('section');
            section.id = SECTION_ID;
            section.className = 'verticalSection ' + PREFIX + 'section';
            retainedSection = section;
            // Stable container event listeners keep keyboard selection tied
            // to the actor banner even after the slide itself is rebuilt.
            function setHoverInsideCard(event) {
                const card = section.querySelector('.' + PREFIX + 'card');
                if (!card) return;
                const rect = card.getBoundingClientRect();
                const inside = event.clientX >= rect.left && event.clientX < rect.right &&
                    event.clientY >= rect.top && event.clientY < rect.bottom;
                if (inside === state.hovered) return;
                state.hovered = inside;
                if (state.pauseOnHover) {
                    if (inside) pauseRotation();
                    else resumeRotation();
                }
                syncZoomPlayback();
            }
            // The section includes outer padding. Only the visible banner card
            // (including its arrow controls) can pause the slideshow.
            section.addEventListener('pointerenter', setHoverInsideCard);
            section.addEventListener('pointermove', setHoverInsideCard);
            section.addEventListener('pointerleave', function () {
                if (!state.hovered) return;
                state.hovered = false;
                if (state.pauseOnHover) resumeRotation();
                syncZoomPlayback();
            });
            if (anchor) host.insertBefore(section, anchor);
            else host.appendChild(section);
        } else if (section.parentElement !== host) {
            if (anchor) host.insertBefore(section, anchor);
            else host.appendChild(section);
        } else if (anchor && section.nextElementSibling !== anchor) {
            host.insertBefore(section, anchor);
        }
        retainedSection = section;
        return section;
    }
    function renderInto(section, candidate) {
        const signature = candidate ? JSON.stringify([
            candidate.id, candidate.banner, candidate.logo, candidate.country,
            candidate.stat, candidate.babepedia, state.showFields, state.candidates.length,
            state.showProgress, state.slideSeconds, state.pauseOnHover
        ]) : ('empty:' + state.error);
        if (section.dataset.renderSignature === signature && section.childElementCount) return;
        section.dataset.renderSignature = signature;
        section.replaceChildren();
        if (!candidate) {
            const empty = document.createElement('div');
            empty.className = PREFIX + 'empty';
            empty.textContent = state.error || T('Нет актёров с изображением Banner в Jellyfin.');
            section.appendChild(empty);
            stopRotation();
            return;
        }
        const person = candidate.person;
        const stat = candidate.stat || {};
        const personId = val(person, 'id', 'Id', '');
        const name = val(person, 'name', 'Name', 'Actor');
        const card = document.createElement('div');
        card.className = PREFIX + 'card';
        card.style.setProperty('--' + PREFIX + 'zoom-duration', durationMs() + 'ms');
        card.style.setProperty('--' + PREFIX + 'bg', 'url("' + candidate.banner.replace(/"/g, '%22') + '")');
        const shade = document.createElement('div');
        shade.className = PREFIX + 'shade';
        card.appendChild(shade);
        const content = document.createElement('a');
        content.className = PREFIX + 'content';
        content.href = detailsHref(personId);
        content.title = name;
        card.appendChild(content);
        const top = document.createElement('div');
        top.className = PREFIX + 'top';
        content.appendChild(top);
        const body = document.createElement('div');
        body.className = PREFIX + 'body';
        content.appendChild(body);
        const identity = document.createElement('div');
        identity.className = PREFIX + 'identity';
        if (candidate.logo) {
            const logo = document.createElement('img');
            logo.className = PREFIX + 'logo';
            logo.loading = 'lazy';
            logo.alt = name;
            logo.src = candidate.logo;
            identity.appendChild(logo);
        }
        const nameRow = document.createElement('div');
        nameRow.className = PREFIX + 'nameRow';
        const heading = document.createElement('h2');
        heading.className = PREFIX + 'name';
        heading.textContent = name;
        nameRow.appendChild(heading);
        if (candidate.country && candidate.countryUrl) {
            const flag = document.createElement('img');
            flag.className = PREFIX + 'flag';
            flag.alt = candidate.country;
            flag.src = candidate.countryUrl;
            flag.title = candidate.birthPlace || candidate.country;
            nameRow.appendChild(flag);
        }
        // Both award icons belong immediately to the right of the name/flag.
        if (val(stat, 'verified', 'Verified', false)) {
            const verified = document.createElement('span');
            verified.className = PREFIX + 'badge ' + PREFIX + 'verified';
            verified.textContent = '✓';
            verified.title = T('Verified Pornhub performer');
            verified.setAttribute('aria-label', T('Verified'));
            nameRow.appendChild(verified);
        }
        if (val(stat, 'awardWinner', 'AwardWinner', false)) {
            const award = document.createElement('span');
            award.className = PREFIX + 'badge ' + PREFIX + 'award';
            award.textContent = '🏆';
            award.title = T('Pornhub award winner');
            award.setAttribute('aria-label', T('Award winner'));
            nameRow.appendChild(award);
        }
        identity.appendChild(nameRow);
        body.appendChild(identity);
        const stats = document.createElement('div');
        stats.className = PREFIX + 'stats';
        const ranking = ['modelRank','weeklyRank','monthlyRank','lastMonthRank','yearlyRank'];
        const labels = ['Model','Weekly','Monthly','Last month','Yearly'].map(T);
        const rankTooltip = titleOrNull(ranking.map(function (key, i) {
            const raw = val(stat, key, key[0].toUpperCase() + key.slice(1), null);
            return raw === null || raw === undefined ? '' : labels[i] + ': ' + raw;
        }));
        for (const [field, lower, upper, label, kind, provider] of METRIC_FIELDS) {
            if (!state.showFields[field]) continue;
            const source = provider === 'bp' ? candidate.babepedia : candidate.stat;
            if (!source) continue;
            const raw = val(source, lower, upper, null);
            if (raw === null || raw === undefined || raw === '') continue;
            const big = kind === 'counter' ? compact(raw) : String(raw);
            const tooltip = field === 'PornhubModelRank' ? rankTooltip :
                kind === 'counter' ? T(label) + ': ' + fmt(raw) : T(label) + ': ' + String(raw);
            stats.appendChild(createBox(big, label, tooltip));
        }
        if (stats.childElementCount) body.appendChild(stats);
        // Navigation is separate from the clickable actor link.
        const nav = document.createElement('div');
        nav.className = PREFIX + 'navFloating';
        if (state.candidates.length > 1) {
            for (const [symbol, step, description] of [['‹',-1,'Previous actor'],['›',1,'Next actor']]) {
                const button = document.createElement('button');
                button.type = 'button';
                button.className = PREFIX + 'btn';
                button.textContent = symbol;
                button.title = T(description);
                button.addEventListener('click', function () {
                    state.index = (state.index + step + state.candidates.length) % state.candidates.length;
                    paint();
                });
                nav.appendChild(button);
            }
        }
        const counter = document.createElement('span');
        counter.className = PREFIX + 'counter';
        counter.textContent = (state.index + 1) + ' / ' + state.candidates.length;
        counter.setAttribute('aria-label',T('Actor ') + (state.index + 1) + T(' of ') + state.candidates.length);
        nav.appendChild(counter);
        card.appendChild(nav);
        if (state.showProgress && state.candidates.length > 1) {
            const progress = document.createElement('div');
            progress.className = PREFIX + 'progress';
            progress.setAttribute('aria-hidden', 'true');
            const fill = document.createElement('div');
            fill.className = PREFIX + 'progressfill';
            progress.appendChild(fill);
            card.appendChild(progress);
        }
        section.appendChild(card);
        scheduleRotation();
        syncZoomPlayback();
    }
    function paint() {
        if (!state.enabled || !isMainHomeTab()) {
            removeSection();
            return;
        }
        ensureStyles();
        const section = mountSection();
        if (!section) return;
        preloadActorImages();
        renderInto(section, currentCandidate());
        syncLogoFavoriteOutline();
        // If the detached section was reused, keep its remaining slide time.
        if (!state.timer) resumeRotation();
        syncZoomPlayback();
    }
    async function fetchPeople(ids, generation) {
        const found = new Map();
        for (let offset = 0; offset < ids.length; offset += BATCH_SIZE * 2) {
            if (generation !== state.generation) return found;
            const batches = [ids.slice(offset,offset+BATCH_SIZE),ids.slice(offset+BATCH_SIZE,offset+BATCH_SIZE*2)]
                .filter(x=>x.length);
            const pages = await Promise.all(batches.map(async batch => {
                const params = new URLSearchParams({ UserId:state.userId, Ids:batch.join(','),
                    EnableImages:'true', Limit:String(batch.length) });
                return fetchJson('Items?' + params);
            }));
            if (generation !== state.generation) return found;
            for (const data of pages) {
                const items = val(data,'items','Items',[]);
                if (!Array.isArray(items)) continue;
                for (const person of items) {
                    const pid = normalizedId(val(person,'id','Id',''));
                    if (pid && String(val(person,'type','Type','')).toLowerCase()==='person') found.set(pid,person);
                }
            }
        }
        return found;
    }
    async function fetchCountries(ids) {
        const map = new Map();
        for (let offset = 0; offset < ids.length; offset += COUNTRY_CHUNK) {
            const batch = ids.slice(offset, offset + COUNTRY_CHUNK);
            const response = await fetchJson('JFToStashSync/RankedPerformerBirthCountries?ids=' + encodeURIComponent(batch.join(',')));
            Object.keys(response || {}).forEach(function (key) {
                const entry = response[key] || {};
                const country = val(entry, 'country', 'Country', '');
                const birthPlace = val(entry, 'birthPlace', 'BirthPlace', '');
                map.set(normalizedId(key), { country: country, birthPlace: birthPlace, countryUrl: iso2ToFlagUrl(country) });
            });
        }
        return map;
    }
    async function cachedStats(path, map) {
        try {
            const rows = await fetchJson(path);
            map.clear();
            for (const item of Array.isArray(rows) ? rows : []) {
                const pid = normalizedId(val(item, 'personId', 'PersonId', ''));
                if (pid) map.set(pid, item);
            }
        } catch (_) { map.clear(); }
    }
    async function ensureData() {
        if (!state.userId || state.running || Date.now() < state.expires) return state.running;
        const generation = ++state.generation;
        state.running = (async function () {
            state.error = '';
            try {
                const config = await (window.__jfHomeWarmup?.getConfig?.() || fetchJson('JFToStashSync/PlayerOCounterConfig'));
                if (generation !== state.generation) return;
                state.enabled = !!val(config, 'featuredPornhubBannerEnabled', 'FeaturedPornhubBannerEnabled', false);
                state.highlightFavorites = !!val(config, 'highlightActorPortraits', 'HighlightActorPortraits', false);
                const seconds = Number(val(config, 'featuredBannerSlideSeconds', 'FeaturedBannerSlideSeconds', 15));
                const maximum = Number(val(config, 'featuredBannerMaxItems', 'FeaturedBannerMaxItems', 30));
                state.slideSeconds = Number.isFinite(seconds) ? Math.max(5, Math.min(120, seconds)) : 15;
                state.maxItems = Number.isFinite(maximum) ? Math.max(1, Math.min(100, Math.floor(maximum))) : 30;
                state.showProgress = val(config, 'featuredBannerShowProgress', 'FeaturedBannerShowProgress', true) !== false;
                state.pauseOnHover = !!val(config, 'featuredBannerPauseOnHover', 'FeaturedBannerPauseOnHover', false);
                state.order = val(config, 'featuredBannerOrder', 'FeaturedBannerOrder', 'Random') === 'Ranked' ? 'Ranked' : 'Random';
                state.rankingSource = val(config, 'featuredBannerRankingSource', 'FeaturedBannerRankingSource', 'Pornhub') === 'Babepedia' ? 'Babepedia' : 'Pornhub';
                state.showFields = {};
                for (const [field] of METRIC_FIELDS) state.showFields[field] =
                    !!val(config, 'featuredBannerShow' + field, 'FeaturedBannerShow' + field, false);
                if (!state.enabled) {
                    state.candidates = [];
                    state.expires = Date.now() + 30000;
                    paint();
                    return;
                }
                const ready = await fetchJson('JFToStashSync/FeaturedBannerCandidates');
                if (generation !== state.generation) return;
                const ids = val(ready, 'ids', 'Ids', []).map(normalizedId)
                    .filter(id => /^[a-f0-9]{32}$/.test(id));
                if (!ids.length) {
                    state.candidates = [];
                    state.expires = Date.now() + 60000;
                    paint();
                    return;
                }
                // These endpoints return only on-disk caches; they do not contact Pornhub/Babepedia.
                await Promise.all([
                    cachedStats('JFToStashSync/PornhubActorCached', state.stats),
                    cachedStats('JFToStashSync/BabepediaActorCached', state.babepedia)
                ]);
                const people = await fetchPeople(ids, generation);
                if (generation !== state.generation) return;
                const candidates = [];
                people.forEach(function (person, pid) {
                    const banner = imageUrl(person, 'Banner', 1600);
                    if (!banner) return;
                    candidates.push({
                        id: pid, person: person,
                        stat: state.stats.get(pid) || null,
                        babepedia: state.babepedia.get(pid) || null,
                        banner: banner, logo: imageUrl(person, 'Logo', 1100)
                    });
                });
                // Sort all eligible people first, then limit slides. Avoid extra
                // birthplace requests for actors outside the selected carousel.
                const selected = arrange(candidates).slice(0, state.maxItems);
                const prior = state.candidates[state.index]?.id;
                // First frame is ready now: no need to wait for the optional
                // country lookup to paint actor, logo, banner and cached ranks.
                state.candidates = selected.map(candidate => {
                    const country = state.countries.get(candidate.id) || {};
                    return Object.assign({}, candidate, {
                        country: country.country || '',
                        birthPlace: country.birthPlace || '',
                        countryUrl: country.countryUrl || ''
                    });
                });
                const oldIndex = state.candidates.findIndex(x => x.id === prior);
                state.index = oldIndex >= 0 ? oldIndex : 0;
                state.expires = Date.now() + CACHE_MS;
                preloadActorImages();
                paint();
                window.__jfStashHomeLoader?.mark('actor');
                // Birth flags are enhancement data, not a rendering dependency.
                try {
                    const countries = selected.length ? await fetchCountries(selected.map(x => x.id)) : new Map();
                    if (generation !== state.generation) return;
                    state.countries = countries;
                    for (const candidate of state.candidates) {
                        const info = countries.get(candidate.id) || {};
                        candidate.country = info.country || '';
                        candidate.birthPlace = info.birthPlace || '';
                        candidate.countryUrl = info.countryUrl || '';
                    }
                    updateVisibleFlag();
                } catch (error) {
                    console.debug('[JFToStashSync] Actor birthplace flags unavailable:', error?.message);
                }
            } catch (error) {
                if (generation !== state.generation) return; // Ignore an earlier user's response.
                state.error = error && error.message || 'Failed to load featured actor banners.';
                state.candidates = [];
                state.expires = Date.now() + 60000;
                console.warn('[JFToStashSync] ' + state.error, error);
                paint();
            } finally {
                if (generation === state.generation) {
                    state.running = null;
                    window.__jfStashHomeLoader?.mark('actor');
                }
            }
        })();
        return state.running;
    }
    function scheduleMount() {
        if (mountPending) return;
        mountPending = true;
        queueMicrotask(function () {
            mountPending = false;
            if (!isMainHomeTab()) removeSection();
            else {
                paint();
                if (Date.now() >= state.expires && !state.running) ensureData();
            }
        });
    }
    function onVisibility() {
        if (document.visibilityState !== 'visible') {
            pauseRotation();
            syncZoomPlayback();
            return;
        }
        if (!isMainHomeTab()) return;
        if (Date.now() >= state.expires) ensureData();
        else {
            scheduleMount();
            resumeRotation();
        }
        syncZoomPlayback();
    }
    function start() {
        if (state.userId) ensureData();
        document.addEventListener('jf-to-stash-user-ready', function () {
            const fresh = userId();
            if (fresh && fresh !== state.userId) {
                ++state.generation;
                state.userId = fresh;
                state.running = null;
                state.expires = 0;
                state.candidates = [];
                state.stats.clear();
                state.babepedia.clear();
                state.countries.clear();
                stopRotation();
                if (retainedSection) retainedSection.remove();
                retainedSection = null;
            }
            if (fresh) ensureData();
        });
        new MutationObserver(scheduleMount).observe(document.body, { childList: true, subtree: true });
        window.addEventListener('keydown', function (event) {
            if (event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey ||
                !state.enabled || state.candidates.length < 2 || !isMainHomeTab() ||
                document.visibilityState === 'hidden' ||
                !state.hovered ||
                ['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName) ||
                document.activeElement?.isContentEditable) return;
            if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
            event.preventDefault();
            const step = event.key === 'ArrowLeft' ? -1 : 1;
            state.index = (state.index + step + state.candidates.length) % state.candidates.length;
            paint();
        });
        window.addEventListener('hashchange', scheduleMount, true);
        window.addEventListener('popstate', scheduleMount, true);
        document.addEventListener('jf-to-stash-favorites-updated', syncLogoFavoriteOutline);
        document.addEventListener('visibilitychange', onVisibility, true);
        setInterval(function () {
            const currentUser = userId();
            if (currentUser !== state.userId) {
                ++state.generation;
                state.userId = currentUser;
                state.expires = 0;
                state.candidates = [];
                stopRotation();
                if (retainedSection) retainedSection.remove();
                retainedSection = null;
            }
            // Even outside Home, keep the next actor slide ready in memory.
            if (state.userId && Date.now() >= state.expires) ensureData();
            if (!isMainHomeTab()) {
                removeSection();
                return;
            }
            paint();
        }, 12000);
    }
    state.userId = userId();
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
})();
