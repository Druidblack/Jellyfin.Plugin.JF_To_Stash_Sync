/* JF To Stash Sync: cached Pornhub actor statistics, no external web requests on page visits. */
(function () {
    'use strict';
    const T = (text) => window.__jfToStashI18n?.t(text) ?? String(text ?? '');
    if (window.__jfPornhubActorStatsLoaded) return;
    window.__jfPornhubActorStatsLoaded = true;
    const prefix = 'jf-ph-';
    let enabled = false, currentUser = '', cache = new Map(), uniqueNames = new Map();
    let loading = false, lastRefresh = 0, timer = null;
    const value = (o, a, b) => o && (o[a] !== undefined ? o[a] : o[b]);
    const norm = x => String(x || '').replace(/-/g, '').toLowerCase();
    const nameKey = x => String(x || '').trim().replace(/\s+/g, ' ').toLowerCase();
    function userId() { try { return window.ApiClient?.getCurrentUserId?.() || ''; } catch (_) { return ''; } }
    async function json(path) {
        const c = window.ApiClient;
        if (!c?.fetch) throw new Error('Jellyfin API is unavailable');
        const r = await c.fetch({url: c.getUrl(path), type: 'GET'});
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.json();
    }
    function styleOnce() {
        if (document.getElementById(prefix + 'style')) return;
        const style = document.createElement('style');
        style.id = prefix + 'style';
        style.textContent = `
          .jf-ph-lines{display:flex;flex-direction:column;align-items:center;gap:2px;width:100%;box-sizing:border-box;
            padding:2px 2px 6px;margin:0 auto;font-size:.73em;line-height:1.4;color:var(--theme-text-secondary-color,#aaa);
            text-align:center;max-width:100%}
          .jf-ph-line{display:flex;flex-wrap:wrap;justify-content:center;align-items:center;column-gap:.55em;row-gap:.15em;max-width:100%}
          .jf-ph-line span{white-space:nowrap;max-width:100%}
          .portraitCard .jf-ph-lines{width:100%}
          .jf-spp-tile .jf-ph-lines{padding:2px 0 5px}
          .jf-ph-identity{font-weight:500}
          .jf-ph-verified-badge{display:inline-flex;align-items:center;justify-content:center;
            width:1.42em;height:1.42em;box-sizing:border-box;border-radius:50%;background:#2587e4;
            color:#fff;font-size:1.06em;font-weight:900;line-height:1;text-shadow:0 1px 2px #0005}
        `;
        document.head.appendChild(style);
    }
    function detailsId(href) {
        const m = String(href || '').match(/[?&]id=([0-9a-f-]{32,36})(?:[&#]|$)/i);
        return m ? norm(m[1]) : '';
    }
    function personId(card) {
        if (card.classList.contains('jf-spp-tile')) return norm(card.dataset.personId);
        for (const id of [card.dataset.personId, card.dataset.itemId, card.getAttribute('data-id'),
            card.querySelector('[data-person-id]')?.dataset.personId]) {
            if (/^[0-9a-f-]{32,36}$/i.test(id || '')) return norm(id);
        }
        for (const link of card.querySelectorAll('a[href*="details?"]')) {
            const id = detailsId(link.getAttribute('href'));
            if (id) return id;
        }
        const image = card.querySelector('.cardImageContainer, img[src*="/Items/"]');
        const imageText = (image?.getAttribute('style') || '') + ' ' + (image?.getAttribute('src') || '');
        const m = imageText.match(/\/Items\/([0-9a-f-]{32,36})\/Images\//i);
        return m ? norm(m[1]) : '';
    }
    function personCard(card) {
        return card.classList.contains('jf-spp-tile') || card.matches('.personCard, .card[data-type="Person"], .card[data-itemtype="Person"]') ||
            !!(card.querySelector('.cardImageIcon.person, .birthage-container') || card.closest('.peopleSection,.personItemsContainer,.peopleContainer'));
    }
    function resolve(card) {
        const id = personId(card);
        if (id) return cache.get(id) || null;
        if (card.classList.contains('jf-spp-tile')) return null;
        const text = card.querySelector('.cardText,.cardText-first,[data-name]');
        return uniqueNames.get(nameKey(text?.textContent || text?.getAttribute('data-name'))) || null;
    }
    function fmt(value) { const n = Number(value); return Number.isFinite(n) && n >= 0 ? n.toLocaleString() : String(value); }
    function compact(value) {
        const n = Number(value);
        if (!Number.isFinite(n) || n < 0) return String(value);
        if (n >= 1e9) return (n / 1e9).toFixed(1).replace(/\.0$/, '') + 'B';
        if (n >= 1e6) return (n / 1e6).toFixed(1).replace(/\.0$/, '') + 'M';
        if (n >= 1e3) return (n / 1e3).toFixed(1).replace(/\.0$/, '') + 'K';
        return String(n);
    }
    function lines(item) {
        const rows = [];
        const credentials = [];
        if (value(item,'awardWinner','AwardWinner') === true) credentials.push(['🏆', T('Pornhub award winner')]);
        if (value(item,'verified','Verified') === true) credentials.push(['✓', T('Verified Pornhub performer'), 'verified']);
        if (credentials.length) rows.push(credentials);
        const audience = [];
        const ranking = [
            ['ModelRank', T('Model rank')],
            ['WeeklyRank', T('Weekly rank')],
            ['MonthlyRank', T('Monthly rank')],
            ['LastMonthRank', T('Last month rank')],
            ['YearlyRank', T('Yearly rank')]
        ];
        const rankedValues = ranking
            .map(([key, label]) => ({label, n: value(item, key[0].toLowerCase() + key.slice(1), key)}))
            .filter(({n}) => n !== null && n !== undefined);
        const overall = value(item, 'modelRank', 'ModelRank');
        if (overall !== null && overall !== undefined) {
            // Only the overall rank is visible. All available periods, including
            // the overall rank, are accessible from its native hover tooltip.
            rows.push([[T('Rank #') + String(overall), rankedValues
                .map(({label, n}) => label + ': #' + String(n)).join('\n')]]);
        }
        for (const [name, label, ico] of [['VideoViews', T('Video views'), '▶'], ['Subscribers', T('Subscribers'), '👥']]) {
            const n = value(item, name[0].toLowerCase()+name.slice(1), name);
            if (n !== null && n !== undefined) audience.push([ico + ' ' + compact(n), label + ': ' + fmt(n)]);
        }
        if (audience.length) rows.push(audience);
        return rows;
    }
    function draw(card) {
        if (!personCard(card)) return;
        const parent = card.classList.contains('jf-spp-tile') ? card.querySelector('.jf-spp-info') : card.querySelector('.cardBox');
        if (!parent) return;
        const old = parent.querySelector('.jf-ph-lines');
        const item = enabled ? resolve(card) : null;
        const rows = item ? lines(item) : [];
        if (!rows.length) { if (old) old.remove(); return; }
        const sig = JSON.stringify(rows);
        if (old?.dataset.signature === sig) return;
        if (old) old.remove();
        const node = document.createElement('div');
        node.className = 'jf-ph-lines';
        node.dataset.signature = sig;
        node.setAttribute('aria-label', T('Pornhub actor statistics'));
        for (const fields of rows) {
            const row = document.createElement('div');
            row.className = 'jf-ph-line';
            for (const [text, title, kind] of fields) {
                const cell = document.createElement('span');
                cell.textContent = text;
                cell.title = title;
                if (kind === 'verified') {
                    cell.className = 'jf-ph-verified-badge';
                    cell.setAttribute('aria-label', title);
                }
                row.appendChild(cell);
            }
            node.appendChild(row);
        }
        parent.appendChild(node);
    }
    function drawAll() {
        if (!enabled) { document.querySelectorAll('.jf-ph-lines').forEach(x => x.remove()); return; }
        styleOnce();
        document.querySelectorAll('.jf-spp-tile, .card.portraitCard, .card.personCard').forEach(draw);
    }
    function schedule() {
        if (timer) return;
        timer = setTimeout(() => { timer = null; drawAll(); }, 250);
    }
    async function refresh() {
        const id = userId();
        if (!id || loading) return;
        if (id !== currentUser) { cache = new Map(); uniqueNames = new Map(); currentUser = id; enabled = false; }
        loading = true;
        try {
            const conf = await (window.__jfHomeWarmup?.getConfig?.() || json('JFToStashSync/PlayerOCounterConfig'));
            enabled = !!value(conf,'pornhubActorStatsEnabled','PornhubActorStatsEnabled');
            cache = new Map(); uniqueNames = new Map();
            if (enabled) {
                const entries = await json('JFToStashSync/PornhubActorCached');
                const names = new Map();
                for (const item of Array.isArray(entries) ? entries : []) {
                    const actor = norm(value(item,'personId','PersonId'));
                    if (!/^[a-f0-9]{32}$/.test(actor)) continue;
                    cache.set(actor, item);
                    const name = nameKey(value(item,'personName','PersonName'));
                    if (!name) continue;
                    if (names.has(name)) names.set(name, null);
                    else names.set(name, item);
                }
                uniqueNames = names;
            }
            lastRefresh = Date.now();
            drawAll();
        } catch (_) { /* Jellyfin may still be signing in. */ }
        finally { loading = false; }
    }
    function start() {
        if (!document.body) return;
        new MutationObserver(schedule).observe(document.body, {childList: true, subtree: true});
        refresh();
        // Poll ONLY local Jellyfin cache, never Pornhub. Update after manual tests on another page.
        setInterval(() => {
            if (userId() !== currentUser || Date.now() - lastRefresh >= 60000) refresh();
            else schedule();
        }, 12000);
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, {once:true});
    else start();
})();
