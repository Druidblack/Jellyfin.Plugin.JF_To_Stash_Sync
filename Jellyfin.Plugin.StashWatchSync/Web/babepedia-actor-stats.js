/* JF To Stash Sync — Babepedia statistics from the on-disk cache only.
   No external requests are made from Jellyfin Web. */
(function () {
    'use strict';
    const T = (text) => window.__jfToStashI18n?.t(text) ?? String(text ?? '');
    if (window.__jfBabepediaStatsLoaded) return;
    window.__jfBabepediaStatsLoaded = true;
    const ROOT = 'jf-bp-';
    let enabled = false;
    let cache = new Map();
    let uniqueNames = new Map();
    let currentUser = '';
    let timer = null;
    let loading = false;
    let lastRefresh = 0;

    const norm = id => String(id || '').replace(/-/g, '').toLowerCase();
    const nameKey = name => String(name || '').trim().replace(/\s+/g, ' ').toLowerCase();
    const value = (obj, a, b) => obj && (obj[a] !== undefined ? obj[a] : obj[b]);
    function api() { return window.ApiClient; }
    function uid() { try { return api() && api().getCurrentUserId() || ''; } catch (_) { return ''; } }
    async function json(path) {
        const client = api();
        if (!client || !client.fetch) throw new Error('No Jellyfin API');
        const response = await client.fetch({ url: client.getUrl(path), type: 'GET' });
        if (!response.ok) throw new Error('HTTP ' + response.status);
        return response.json();
    }
    function styleOnce() {
        if (document.getElementById(ROOT + 'style')) return;
        const style = document.createElement('style');
        style.id = ROOT + 'style';
        style.textContent = `
          .jf-bp-statline{font-size:.77em;line-height:1.4;color:var(--theme-text-secondary-color,#aaa);
            display:flex;justify-content:center;align-items:center;flex-wrap:wrap;gap:.25em .55em;
            text-align:center;margin:0 auto;padding:.22em .14em .32em;max-width:100%;box-sizing:border-box}
          .jf-bp-statline span{white-space:nowrap}
          .jf-bp-statline a{color:inherit;text-decoration:none;pointer-events:auto}
          .jf-bp-statline a:hover{text-decoration:underline}
          .portraitCard .jf-bp-statline{width:100%;}
          .jf-spp-tile .jf-bp-statline{width:100%;padding:.12em 0 .4em}
        `;
        document.head.appendChild(style);
    }
    function detailsId(href) {
        if (!href) return '';
        const found = String(href).match(/[?&]id=([0-9a-f-]{32,36})(?:[&#]|$)/i);
        return found ? norm(found[1]) : '';
    }
    function personId(card) {
        if (card.classList.contains('jf-spp-tile')) return norm(card.dataset.personId);
        for (const candidate of [card.dataset.personId, card.dataset.itemId,
            card.getAttribute('data-id'), card.querySelector('[data-person-id]')?.dataset.personId]) {
            if (candidate && /^[a-f0-9-]{32,36}$/i.test(candidate)) return norm(candidate);
        }
        for (const a of card.querySelectorAll('a[href*="details?id="],a[href*="details?"],a[href*="details#"]')) {
            const id = detailsId(a.getAttribute('href'));
            if (id) return id;
        }
        // Some Jellyfin portrait cards are non-clickable (cursor:default) and have
        // no data-id. Their primary image URL still embeds the Jellyfin Person ID.
        const image = card.querySelector('.cardImageContainer, img[src*="/Items/"]');
        const rawUrl = (image?.getAttribute('style') || '') + ' ' + (image?.getAttribute('src') || '');
        const found = rawUrl.match(/\/Items\/([0-9a-f-]{32,36})\/Images\//i);
        if (found) return norm(found[1]);
        return '';
    }
    function isPersonCard(card) {
        if (card.classList.contains('jf-spp-tile')) return true;
        return !!(card.matches('.personCard, .card[data-type="Person"],.card[data-itemtype="Person"]') ||
            card.querySelector('.cardImageIcon.person, .birthage-container') ||
            card.closest('.peopleSection,.personItemsContainer,.peopleContainer'));
    }
    function resolve(card) {
        const id = personId(card);
        if (id && cache.has(id)) return cache.get(id);
        // Only as a fallback for native Person cards with no item ID in the rendered markup;
        // the name must resolve to exactly one cached Person.
        if (!card.classList.contains('jf-spp-tile') && !id) {
            const title = card.querySelector('.cardText, .cardText-first, [data-name]');
            const key = nameKey(title?.textContent || title?.getAttribute('data-name'));
            return uniqueNames.get(key) || null;
        }
        return null;
    }
    function badges(item) {
        const parts = [];
        const rating = value(item, 'rating', 'Rating');
        const rank = value(item, 'rank', 'Rank');
        const favorites = value(item, 'favorites', 'Favorites');
        const votes = value(item, 'votes', 'Votes');
        const voteCount = votes !== null && votes !== undefined && votes !== '' && Number.isFinite(Number(votes)) && Number(votes) >= 0
            ? Number(votes).toLocaleString() : null;
        const ratingTooltip = voteCount === null ? T('Rating') : T('Rating · Votes: ') + voteCount;
        if (rating !== null && rating !== undefined) parts.push(['★', Number(rating).toLocaleString(undefined, { maximumFractionDigits: 1 }) + '/10', ratingTooltip]);
        if (rank !== null && rank !== undefined) parts.push(['#', Number(rank).toLocaleString(), T('Rank')]);
        if (favorites !== null && favorites !== undefined) parts.push(['♥', Number(favorites).toLocaleString(), T('Favorites on Babepedia')]);
        return parts;
    }
    function draw(card) {
        if (!isPersonCard(card)) return;
        const item = enabled ? resolve(card) : null;
        const parent = card.classList.contains('jf-spp-tile') ? card.querySelector('.jf-spp-info') : card.querySelector('.cardBox');
        const old = parent?.querySelector('.jf-bp-statline');
        if (!parent || !item || !badges(item).length) { if (old) old.remove(); return; }
        // Avoid touching the existing portrait, overlay badges or primary clickable anchor.
        const signature = JSON.stringify([value(item,'rating','Rating'), value(item,'rank','Rank'), value(item,'favorites','Favorites'), value(item,'votes','Votes')]);
        if (old && old.dataset.signature === signature) return;
        if (old) old.remove();
        const row = document.createElement('div');
        row.className = 'jf-bp-statline';
        row.dataset.signature = signature;
        row.setAttribute('aria-label', T('Babepedia statistics'));
        for (const [symbol, number, description] of badges(item)) {
            const span = document.createElement('span');
            span.title = description;
            span.textContent = symbol + ' ' + number;
            row.appendChild(span);
        }
        parent.appendChild(row);
    }
    function drawAll() {
        if (!enabled) {
            document.querySelectorAll('.jf-bp-statline').forEach(x => x.remove());
            return;
        }
        styleOnce();
        document.querySelectorAll('.jf-spp-tile, .card.portraitCard, .card.personCard').forEach(draw);
    }
    function schedule() {
        if (timer) return;
        timer = setTimeout(() => { timer = null; drawAll(); }, 250);
    }
    async function refresh() {
        if (loading || !uid()) return;
        const next = uid();
        if (next !== currentUser) { currentUser = next; cache = new Map(); uniqueNames = new Map(); enabled = false; }
        loading = true;
        try {
            const conf = await (window.__jfHomeWarmup?.getConfig?.() || json('JFToStashSync/PlayerOCounterConfig'));
            enabled = !!value(conf, 'babepediaActorStatsEnabled', 'BabepediaActorStatsEnabled');
            cache = new Map();
            uniqueNames = new Map();
            if (enabled) {
                const entries = await json('JFToStashSync/BabepediaActorCached');
                const names = new Map();
                for (const entry of Array.isArray(entries) ? entries : []) {
                    const id = norm(value(entry, 'personId', 'PersonId'));
                    if (!/^[a-f0-9]{32}$/.test(id)) continue;
                    cache.set(id, entry);
                    const n = nameKey(value(entry, 'personName', 'PersonName'));
                    if (n && !names.has(n)) names.set(n, entry);
                    else if (n) names.set(n, null); // ambiguous name: do not guess
                }
                uniqueNames = names;
            }
            lastRefresh = Date.now();
            drawAll();
        } catch (_) { /* Web API may be unavailable during sign-in or navigation. */ }
        finally { loading = false; }
    }
    const observer = new MutationObserver(schedule);
    function start() {
        if (!document.body) return;
        observer.observe(document.body, { childList: true, subtree: true });
        refresh();
        // Periodic cache-only refresh covers manual tests performed on another page.
        setInterval(() => {
            if (uid() !== currentUser || Date.now() - lastRefresh >= 60000) refresh();
            else schedule();
        }, 12000);
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
})();
