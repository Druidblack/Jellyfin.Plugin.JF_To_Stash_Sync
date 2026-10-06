(function () {
    'use strict';

    if (window.__jfToStashOCounterLoaded) {
        return;
    }
    window.__jfToStashOCounterLoaded = true;

    var BUTTON_ID = 'jfToStashOCounterButton';
    var ACTOR_BUTTON_ID = 'jfToStashPlayerActorsButton';
    var ACTOR_DIALOG_ID = 'jfToStashPlayerActorsDialog';
    var actorDialogCleanup = null;
    var DETAIL_LINK_BUTTON_ID = 'jfToStashSceneLinkButton';
    var configEnabled = false;
    var actorListEnabled = false;
    var actorGenderIconsEnabled = false;
    var personOverviewLinksEnabled = false;
    var personSocialIconsEnabled = false;
    var actorRoleCache = Object.create(null);
    var personDetailsCache = Object.create(null);
    var personDetailsPending = Object.create(null);
    var actorRolePending = Object.create(null);
    var observer = null;
    var scheduled = false;
    var detailStatusCache = Object.create(null);
    var detailStatusPending = Object.create(null);
    var detailJobPolling = Object.create(null);

    function isolateActorPopupWheel(event) {
        var panel = document.getElementById(ACTOR_DIALOG_ID);
        if (!panel || !panel.contains(event.target)) {
            return;
        }

        var list = panel.querySelector('[data-jf-to-stash-actor-list="true"]');
        if (!list) {
            return;
        }

        // Jellyfin Web can bind wheel handling globally for player volume.
        // Consume the wheel event at the window capture phase and scroll only
        // our list so the player never receives the same wheel input.
        event.preventDefault();
        event.stopImmediatePropagation();

        var deltaY = Number(event.deltaY || 0);
        if (deltaY) {
            list.scrollTop += deltaY;
        }
    }

    window.addEventListener('wheel', isolateActorPopupWheel, { capture: true, passive: false });

    function getApiClient() {
        return window.ApiClient || null;
    }

    async function apiJson(path, options) {
        var api = getApiClient();
        if (!api || typeof api.fetch !== 'function' || typeof api.getUrl !== 'function') {
            throw new Error('Jellyfin ApiClient is not available.');
        }

        var request = Object.assign({
            url: api.getUrl(path),
            type: 'GET'
        }, options || {});

        var response = await api.fetch(request);
        var body = null;
        try {
            body = await response.json();
        } catch (e) {
            body = null;
        }

        if (!response.ok) {
            var message = body && (body.message || body.Message);
            throw new Error(message || ('Jellyfin API request failed with HTTP ' + response.status + '.'));
        }

        return body;
    }

    function getCurrentUserId() {
        var api = getApiClient();
        try {
            return api && typeof api.getCurrentUserId === 'function' ? (api.getCurrentUserId() || '') : '';
        } catch (e) {
            return '';
        }
    }

    function getDeviceId() {
        var api = getApiClient();
        if (!api) {
            return '';
        }

        try {
            if (typeof api.deviceId === 'function') {
                return api.deviceId() || '';
            }
            return api.deviceId || api._deviceId || '';
        } catch (e) {
            return '';
        }
    }

    function parseItemId(value) {
        if (!value) {
            return '';
        }

        try {
            var decoded = decodeURIComponent(String(value));
            var match = decoded.match(/[?&#](?:id|itemId)=([0-9a-fA-F-]{32,36})(?:[&#]|$)/i);
            if (match && match[1]) {
                return match[1].replace(/-/g, '');
            }
        } catch (e) {
            // Ignore malformed URLs and continue with the other strategies.
        }

        return '';
    }

    function getItemIdFromOsd() {
        var osd = document.querySelector('.videoOsdBottom');
        if (!osd) {
            return '';
        }

        var links = osd.querySelectorAll('a[href*="id="]');
        for (var i = 0; i < links.length; i++) {
            var candidate = parseItemId(links[i].getAttribute('href'));
            if (candidate) {
                return candidate;
            }
        }

        var nodes = osd.querySelectorAll('[data-id], [data-itemid], [data-item-id]');
        for (var j = 0; j < nodes.length; j++) {
            var raw = nodes[j].getAttribute('data-id') ||
                nodes[j].getAttribute('data-itemid') ||
                nodes[j].getAttribute('data-item-id') || '';
            if (/^[0-9a-fA-F]{32}$/.test(raw.replace(/-/g, ''))) {
                return raw.replace(/-/g, '');
            }
        }

        return '';
    }

    function getItemIdFromPlayerRoute() {
        var route = window.location.hash || '';
        // A details page can remain behind the player overlay and may refer to a series,
        // season or another item. Only trust the browser URL when it is explicitly a
        // player/video route.
        if (!/(video|player)/i.test(route)) {
            return '';
        }

        return parseItemId(window.location.href);
    }

    async function getItemIdFromSession() {
        try {
            var sessions = await apiJson('Sessions');

            if (!Array.isArray(sessions)) {
                return '';
            }

            var deviceId = getDeviceId();
            var userId = getCurrentUserId();
            var session = null;

            if (deviceId) {
                session = sessions.find(function (entry) {
                    return entry && entry.DeviceId === deviceId && entry.NowPlayingItem && entry.NowPlayingItem.Id;
                });
            }

            if (!session && userId) {
                session = sessions.find(function (entry) {
                    return entry && entry.UserId === userId && entry.NowPlayingItem && entry.NowPlayingItem.Id;
                });
            }

            return session && session.NowPlayingItem && session.NowPlayingItem.Id
                ? String(session.NowPlayingItem.Id).replace(/-/g, '')
                : '';
        } catch (error) {
            console.warn('[JFToStashSync] Could not resolve the current player item from Jellyfin sessions.', error);
            return '';
        }
    }

    async function resolveCurrentItemId() {
        // Prefer identifiers tied to the active OSD/session. This avoids accidentally using
        // a movie/series details-page ID that happens to remain in the browser URL.
        return getItemIdFromOsd() || await getItemIdFromSession() || getItemIdFromPlayerRoute();
    }

    function findFavoriteButton() {
        var selectors = [
            '.videoOsdBottom .osdControls .buttons.focuscontainer-x > .btnUserRating',
            '#videoOsdPage .osdControls .buttons.focuscontainer-x > .btnUserRating',
            '.osdControls .buttons.focuscontainer-x > .btnUserRating',
            '.videoOsdBottom .btnUserRating',
            '#videoOsdPage .btnUserRating'
        ];

        for (var i = 0; i < selectors.length; i++) {
            var candidates = document.querySelectorAll(selectors[i]);
            for (var j = 0; j < candidates.length; j++) {
                var candidate = candidates[j];
                if (candidate && candidate.isConnected) {
                    return candidate;
                }
            }
        }

        return null;
    }

    function isVisibleElement(element) {
        if (!element || !element.isConnected) {
            return false;
        }

        try {
            var style = window.getComputedStyle(element);
            if (!style || style.display === 'none' || style.visibility === 'hidden') {
                return false;
            }
        } catch (e) {
            // If style inspection fails, fall back to geometry below.
        }

        return !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
    }

    function findDetailFavoriteButton() {
        var selectors = [
            '.mainDetailButtons.focuscontainer-x > .btnUserRating',
            '.mainDetailButtons > .btnUserRating'
        ];

        for (var i = 0; i < selectors.length; i++) {
            var candidates = document.querySelectorAll(selectors[i]);
            for (var j = 0; j < candidates.length; j++) {
                var candidate = candidates[j];
                if (!candidate || !candidate.isConnected) {
                    continue;
                }

                if (isVisibleElement(candidate)) {
                    return candidate;
                }
            }
        }

        // Jellyfin keeps old SPA pages connected but hidden. Do not bind a new scene-link
        // button to a hidden previous page; wait until the active page is visible.
        return null;
    }

    function normalizeActorGenderRole(role) {
        return String(role || '')
            .trim()
            .toLowerCase()
            .replace(/[_-]+/g, ' ')
            .replace(/\s+/g, ' ');
    }

    function getActorGenderIconSpec(role) {
        switch (normalizeActorGenderRole(role)) {
            case 'female':
                return {
                    title: 'Female',
                    gender: 'FEMALE',
                    color: '#ec4899',
                    icon: 'venus',
                    viewBox: '0 0 384 512',
                    path: 'M80 176a112 112 0 1 1 224 0 112 112 0 1 1 -224 0zM223.9 349.1C305.9 334.1 368 262.3 368 176 368 78.8 289.2 0 192 0S16 78.8 16 176c0 86.3 62.1 158.1 144.1 173.1-.1 1-.1 1.9-.1 2.9l0 64-32 0c-17.7 0-32 14.3-32 32s14.3 32 32 32l32 0 0 32c0 17.7 14.3 32 32 32s32-14.3 32-32l0-32 32 0c17.7 0 32-14.3 32-32s-14.3-32-32-32l-32 0 0-64c0-1 0-1.9-.1-2.9z'
                };
            case 'male':
                return {
                    title: 'Male',
                    gender: 'MALE',
                    color: '#3b82f6',
                    icon: 'mars',
                    viewBox: '0 0 512 512',
                    path: 'M320 32c0-17.7 14.3-32 32-32L480 0c17.7 0 32 14.3 32 32l0 128c0 17.7-14.3 32-32 32s-32-14.3-32-32l0-50.7-95 95c19.5 28.4 31 62.7 31 99.8 0 97.2-78.8 176-176 176S32 401.2 32 304 110.8 128 208 128c37 0 71.4 11.4 99.8 31l95-95-50.7 0c-17.7 0-32-14.3-32-32zM208 416a112 112 0 1 0 0-224 112 112 0 1 0 0 224z'
                };
            case 'transgender female':
                return {
                    title: 'Transgender Female',
                    gender: 'TRANSGENDER_FEMALE',
                    color: '#c084fc',
                    icon: 'transgender',
                    viewBox: '0 0 576 512',
                    path: 'M128-32c17.7 0 32 14.3 32 32s-14.3 32-32 32L97.9 32 136 70.1 151 55c9.4-9.4 24.6-9.4 33.9 0s9.4 24.6 0 33.9l-15 15 14.2 14.2c27.9-23.8 64.2-38.2 103.8-38.2 36.7 0 70.6 12.4 97.6 33.2L466.7 32 448 32c-17.7 0-32-14.3-32-32s14.3-32 32-32l96 0c17.7 0 32 14.3 32 32l0 96c0 17.7-14.3 32-32 32s-32-14.3-32-32l0-18.7-84.4 84.4c13 23.1 20.4 49.9 20.4 78.3 0 77.4-55 142-128 156.8l0 35.2 32 0c17.7 0 32 14.3 32 32s-14.3 32-32 32l-32 0 0 16c0 17.7-14.3 32-32 32s-32-14.3-32-32l0-16-32 0c-17.7 0-32-14.3-32-32s14.3-32 32-32l32 0 0-35.2c-73-14.8-128-79.4-128-156.8 0-31.4 9-60.7 24.7-85.4l-16.7-16.7-15 15c-9.4 9.4-24.6 9.4-33.9 0s-9.4-24.6 0-33.9l15-15-38.1-38.1 0 30.1c0 17.7-14.3 32-32 32S0 113.7 0 96L0 0C0-17.7 14.3-32 32-32l96 0zM288 336a96 96 0 1 0 0-192 96 96 0 1 0 0 192z'
                };
            case 'transgender male':
                return {
                    title: 'Transgender Male',
                    gender: 'TRANSGENDER_MALE',
                    color: '#6366f1',
                    icon: 'transgender',
                    viewBox: '0 0 576 512',
                    path: 'M128-32c17.7 0 32 14.3 32 32s-14.3 32-32 32L97.9 32 136 70.1 151 55c9.4-9.4 24.6-9.4 33.9 0s9.4 24.6 0 33.9l-15 15 14.2 14.2c27.9-23.8 64.2-38.2 103.8-38.2 36.7 0 70.6 12.4 97.6 33.2L466.7 32 448 32c-17.7 0-32-14.3-32-32s14.3-32 32-32l96 0c17.7 0 32 14.3 32 32l0 96c0 17.7-14.3 32-32 32s-32-14.3-32-32l0-18.7-84.4 84.4c13 23.1 20.4 49.9 20.4 78.3 0 77.4-55 142-128 156.8l0 35.2 32 0c17.7 0 32 14.3 32 32s-14.3 32-32 32l-32 0 0 16c0 17.7-14.3 32-32 32s-32-14.3-32-32l0-16-32 0c-17.7 0-32-14.3-32-32s14.3-32 32-32l32 0 0-35.2c-73-14.8-128-79.4-128-156.8 0-31.4 9-60.7 24.7-85.4l-16.7-16.7-15 15c-9.4 9.4-24.6 9.4-33.9 0s-9.4-24.6 0-33.9l15-15-38.1-38.1 0 30.1c0 17.7-14.3 32-32 32S0 113.7 0 96L0 0C0-17.7 14.3-32 32-32l96 0zM288 336a96 96 0 1 0 0-192 96 96 0 1 0 0 192z'
                };
            case 'non binary':
            case 'nonbinary':
                return {
                    title: 'Non Binary',
                    gender: 'NON_BINARY',
                    color: '#facc15',
                    icon: 'non-binary',
                    viewBox: '0 0 384 512',
                    path: 'M192 544c-97.2 0-176-78.8-176-176 0-86.3 62.1-158 144-173l0-47.2-49.7 24.8-3 1.3c-15.2 5.7-32.5-.8-39.9-15.7-7.4-14.8-2.2-32.6 11.5-41.3l2.8-1.6 38.8-19.4-38.8-19.4c-15.8-7.9-22.2-27.1-14.3-42.9 7.4-14.8 24.8-21.4 40-15.6l3 1.3 49.7 24.8 0-44.2c0-17.7 14.3-32 32-32s32 14.3 32 32l0 44.2 49.7-24.8 3-1.3c15.2-5.8 32.5 .8 39.9 15.6s2.2 32.7-11.5 41.3l-2.8 1.6-38.7 19.4 38.7 19.3c15.8 7.9 22.2 27.1 14.3 42.9-7.4 14.8-24.7 21.4-39.9 15.6l-3-1.3-49.7-24.8 0 47.2c81.9 15.1 144 86.8 144 173 0 97.2-78.8 176-176 176zm0-64a112 112 0 1 0 0-224 112 112 0 1 0 0 224z'
                };
            default:
                return null;
        }
    }

    function restoreActorGenderIconTarget(target) {
        if (!target || target.getAttribute('data-jf-to-stash-gender-icon') !== 'true') {
            return;
        }

        if (typeof target.__jfToStashOriginalRoleHtml === 'string') {
            target.innerHTML = target.__jfToStashOriginalRoleHtml;
        }
        target.removeAttribute('data-jf-to-stash-gender-icon');
        delete target.__jfToStashOriginalRoleHtml;
    }

    function restoreAllActorGenderIcons() {
        document.querySelectorAll('[data-jf-to-stash-gender-icon="true"]').forEach(function (target) {
            restoreActorGenderIconTarget(target);
        });
    }

    function createActorGenderIcon(role) {
        var spec = getActorGenderIconSpec(role);
        if (!spec) {
            return null;
        }

        var holder = document.createElement('span');
        holder.title = spec.title;
        holder.className = 'jfToStashGenderIcon';
        holder.style.display = 'inline-flex';
        holder.style.alignItems = 'center';
        holder.style.justifyContent = 'center';
        holder.style.lineHeight = '1';
        holder.style.verticalAlign = 'middle';
        holder.style.color = spec.color;

        var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('data-prefix', 'fas');
        svg.setAttribute('data-icon', spec.icon);
        svg.setAttribute('class', 'svg-inline--fa fa-' + spec.icon + ' gender-icon');
        svg.setAttribute('role', 'img');
        svg.setAttribute('viewBox', spec.viewBox);
        svg.setAttribute('aria-hidden', 'true');
        svg.setAttribute('data-gender', spec.gender);
        svg.setAttribute('width', '1.15em');
        svg.setAttribute('height', '1.15em');
        svg.setAttribute('focusable', 'false');

        var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('fill', 'currentColor');
        path.setAttribute('d', spec.path);
        svg.appendChild(path);
        holder.appendChild(svg);
        return holder;
    }

    function applyActorGenderIcons(itemId) {
        var roleMap = actorRoleCache[itemId];
        if (!roleMap) {
            return;
        }

        document.querySelectorAll('.personCard[data-type="Actor"]').forEach(function (card) {
            if (!card || !card.isConnected || !isVisibleElement(card)) {
                return;
            }

            var personId = String(card.getAttribute('data-id') || '').replace(/-/g, '').toLowerCase();
            if (!personId) {
                return;
            }

            var role = roleMap[personId];
            var secondary = card.querySelector('.cardText.cardText-secondary');
            if (!secondary) {
                return;
            }

            var target = secondary.querySelector('bdi') || secondary;
            var icon = createActorGenderIcon(role);
            if (!icon) {
                restoreActorGenderIconTarget(target);
                return;
            }

            if (target.getAttribute('data-jf-to-stash-gender-icon') === 'true') {
                var existingSvg = target.querySelector('svg[data-gender]');
                var spec = getActorGenderIconSpec(role);
                if (existingSvg && spec && existingSvg.getAttribute('data-gender') === spec.gender) {
                    return;
                }
                restoreActorGenderIconTarget(target);
            }

            target.__jfToStashOriginalRoleHtml = target.innerHTML;
            target.innerHTML = '';
            target.setAttribute('data-jf-to-stash-gender-icon', 'true');
            target.appendChild(icon);
        });
    }

    function requestActorRoles(itemId) {
        if (!itemId || actorRoleCache[itemId] || actorRolePending[itemId]) {
            return;
        }

        var userId = getCurrentUserId();
        if (!userId) {
            return;
        }

        actorRolePending[itemId] = true;
        apiJson(
            'Users/' + encodeURIComponent(userId) +
            '/Items/' + encodeURIComponent(itemId) +
            '?Fields=People')
            .then(function (item) {
                var roleMap = Object.create(null);
                var people = item && (item.People || item.people);
                if (Array.isArray(people)) {
                    people.forEach(function (person) {
                        if (!person) {
                            return;
                        }

                        var type = String(person.Type || person.type || '').toLowerCase();
                        if (type && type !== 'actor') {
                            return;
                        }

                        var id = String(person.Id || person.id || '').replace(/-/g, '').toLowerCase();
                        var role = person.Role !== undefined ? person.Role : person.role;
                        if (id && role) {
                            roleMap[id] = String(role);
                        }
                    });
                }
                actorRoleCache[itemId] = roleMap;
            })
            .catch(function (error) {
                console.debug('[JFToStashSync] Could not load actor roles from Jellyfin item metadata.', error);
                actorRoleCache[itemId] = Object.create(null);
            })
            .finally(function () {
                delete actorRolePending[itemId];
                scheduleEnsureButton();
            });
    }

    function ensureActorGenderIcons() {
        if (!actorGenderIconsEnabled) {
            restoreAllActorGenderIcons();
            return;
        }

        var favoriteButton = findDetailFavoriteButton();
        if (!favoriteButton) {
            return;
        }

        var itemId = String(favoriteButton.getAttribute('data-id') || '').replace(/-/g, '');
        if (!itemId) {
            return;
        }

        if (!actorRoleCache[itemId]) {
            requestActorRoles(itemId);
            return;
        }

        applyActorGenderIcons(itemId);
    }

    function normalizeOverviewText(value) {
        return String(value || '')
            .replace(/\r\n?/g, '\n')
            .replace(/\s+/g, ' ')
            .trim();
    }

    function restorePersonOverviewLinks() {
        document.querySelectorAll('a[data-jf-to-stash-overview-link="true"]').forEach(function (link) {
            if (!link || !link.parentNode) {
                return;
            }

            var parent = link.parentNode;
            parent.replaceChild(document.createTextNode(link.textContent || link.getAttribute('href') || ''), link);
            if (typeof parent.normalize === 'function') {
                parent.normalize();
            }
        });
    }

    function createPersonOverviewLink(urlText) {
        var parsed;
        try {
            parsed = new URL(urlText);
        } catch (e) {
            return null;
        }

        if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
            return null;
        }

        var link = document.createElement('a');
        link.href = parsed.href;
        link.textContent = urlText;
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.setAttribute('data-jf-to-stash-overview-link', 'true');
        link.style.textDecoration = 'underline';
        link.style.cursor = 'pointer';
        return link;
    }

    function splitUrlTrailingPunctuation(raw) {
        var url = raw || '';
        var trailing = '';
        while (url && /[.,;:!?\]\}]/.test(url.charAt(url.length - 1))) {
            trailing = url.charAt(url.length - 1) + trailing;
            url = url.slice(0, -1);
        }

        // A closing parenthesis is usually punctuation in prose. Preserve it outside the link
        // unless the URL contains a matching unmatched opening parenthesis.
        while (url.charAt(url.length - 1) === ')') {
            var opens = (url.match(/\(/g) || []).length;
            var closes = (url.match(/\)/g) || []).length;
            if (closes <= opens) {
                break;
            }
            trailing = ')' + trailing;
            url = url.slice(0, -1);
        }

        return { url: url, trailing: trailing };
    }

    function linkifyPersonOverviewTextNode(node) {
        if (!node || !node.nodeValue || !/https?:\/\//i.test(node.nodeValue)) {
            return;
        }

        var parent = node.parentElement;
        if (!parent || parent.closest('a, button, script, style, textarea, input')) {
            return;
        }

        var text = node.nodeValue;
        var regex = /https?:\/\/[^\s<>"']+/gi;
        var fragment = document.createDocumentFragment();
        var lastIndex = 0;
        var changed = false;
        var match;

        while ((match = regex.exec(text)) !== null) {
            if (match.index > lastIndex) {
                fragment.appendChild(document.createTextNode(text.slice(lastIndex, match.index)));
            }

            var parts = splitUrlTrailingPunctuation(match[0]);
            var link = createPersonOverviewLink(parts.url);
            if (link) {
                fragment.appendChild(link);
                changed = true;
            } else {
                fragment.appendChild(document.createTextNode(parts.url));
            }

            if (parts.trailing) {
                fragment.appendChild(document.createTextNode(parts.trailing));
            }
            lastIndex = match.index + match[0].length;
        }

        if (!changed) {
            return;
        }

        if (lastIndex < text.length) {
            fragment.appendChild(document.createTextNode(text.slice(lastIndex)));
        }
        node.parentNode.replaceChild(fragment, node);
    }

    function linkifyPersonOverviewElement(element) {
        if (!element || !element.isConnected) {
            return;
        }

        var walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
        var nodes = [];
        var node;
        while ((node = walker.nextNode())) {
            nodes.push(node);
        }

        nodes.forEach(linkifyPersonOverviewTextNode);
    }

    function findVisiblePersonOverview() {
        var selectors = [
            '#itemDetailPage .detailSectionContent .overview',
            '#itemDetailPage .overview',
            '.itemDetailPage .detailSectionContent .overview',
            '.itemDetailPage .overview'
        ];

        for (var i = 0; i < selectors.length; i++) {
            var candidates = document.querySelectorAll(selectors[i]);
            for (var j = 0; j < candidates.length; j++) {
                if (isVisibleElement(candidates[j])) {
                    return candidates[j];
                }
            }
        }

        return null;
    }

    function requestPersonDetails(itemId) {
        if (!itemId || personDetailsCache[itemId] || personDetailsPending[itemId]) {
            return;
        }

        var userId = getCurrentUserId();
        if (!userId) {
            return;
        }

        personDetailsPending[itemId] = true;
        apiJson(
            'Users/' + encodeURIComponent(userId) +
            '/Items/' + encodeURIComponent(itemId))
            .then(function (item) {
                var type = String(item && (item.Type || item.type) || '').toLowerCase();
                personDetailsCache[itemId] = {
                    isPerson: type === 'person',
                    overview: String(item && (item.Overview !== undefined ? item.Overview : item.overview) || '')
                };
            })
            .catch(function (error) {
                console.debug('[JFToStashSync] Could not load Jellyfin person details for overview linkification.', error);
                personDetailsCache[itemId] = { isPerson: false, overview: '' };
            })
            .finally(function () {
                delete personDetailsPending[itemId];
                scheduleEnsureButton();
            });
    }

    function ensurePersonOverviewLinks() {
        if (!personOverviewLinksEnabled) {
            restorePersonOverviewLinks();
            return;
        }

        var itemId = parseItemId(window.location.href);
        if (!itemId) {
            return;
        }

        if (!personDetailsCache[itemId]) {
            requestPersonDetails(itemId);
            return;
        }

        var details = personDetailsCache[itemId];
        if (!details || !details.isPerson || !details.overview || !/https?:\/\//i.test(details.overview)) {
            return;
        }

        var overview = findVisiblePersonOverview();
        if (!overview) {
            return;
        }

        // Verify that the visible block is the current Person's overview before editing it.
        // The Web UI may show a shortened form, so either side may be a prefix of the other.
        var expected = normalizeOverviewText(details.overview);
        var actual = normalizeOverviewText(overview.textContent || '');
        if (expected && actual && expected.indexOf(actual) !== 0 && actual.indexOf(expected) !== 0) {
            return;
        }

        linkifyPersonOverviewElement(overview);
    }

    var PERSON_SOCIAL_SPECS = [
        { key: "instagram", title: "Instagram", color: "#E4405F", hosts: ["instagram.com"], viewBox: "0 0 24 24", path: "M7.0301.084c-1.2768.0602-2.1487.264-2.911.5634-.7888.3075-1.4575.72-2.1228 1.3877-.6652.6677-1.075 1.3368-1.3802 2.127-.2954.7638-.4956 1.6365-.552 2.914-.0564 1.2775-.0689 1.6882-.0626 4.947.0062 3.2586.0206 3.6671.0825 4.9473.061 1.2765.264 2.1482.5635 2.9107.308.7889.72 1.4573 1.388 2.1228.6679.6655 1.3365 1.0743 2.1285 1.38.7632.295 1.6361.4961 2.9134.552 1.2773.056 1.6884.069 4.9462.0627 3.2578-.0062 3.668-.0207 4.9478-.0814 1.28-.0607 2.147-.2652 2.9098-.5633.7889-.3086 1.4578-.72 2.1228-1.3881.665-.6682 1.0745-1.3378 1.3795-2.1284.2957-.7632.4966-1.636.552-2.9124.056-1.2809.0692-1.6898.063-4.948-.0063-3.2583-.021-3.6668-.0817-4.9465-.0607-1.2797-.264-2.1487-.5633-2.9117-.3084-.7889-.72-1.4568-1.3876-2.1228C21.2982 1.33 20.628.9208 19.8378.6165 19.074.321 18.2017.1197 16.9244.0645 15.6471.0093 15.236-.005 11.977.0014 8.718.0076 8.31.0215 7.0301.0839m.1402 21.6932c-1.17-.0509-1.8053-.2453-2.2287-.408-.5606-.216-.96-.4771-1.3819-.895-.422-.4178-.6811-.8186-.9-1.378-.1644-.4234-.3624-1.058-.4171-2.228-.0595-1.2645-.072-1.6442-.079-4.848-.007-3.2037.0053-3.583.0607-4.848.05-1.169.2456-1.805.408-2.2282.216-.5613.4762-.96.895-1.3816.4188-.4217.8184-.6814 1.3783-.9003.423-.1651 1.0575-.3614 2.227-.4171 1.2655-.06 1.6447-.072 4.848-.079 3.2033-.007 3.5835.005 4.8495.0608 1.169.0508 1.8053.2445 2.228.408.5608.216.96.4754 1.3816.895.4217.4194.6816.8176.9005 1.3787.1653.4217.3617 1.056.4169 2.2263.0602 1.2655.0739 1.645.0796 4.848.0058 3.203-.0055 3.5834-.061 4.848-.051 1.17-.245 1.8055-.408 2.2294-.216.5604-.4763.96-.8954 1.3814-.419.4215-.8181.6811-1.3783.9-.4224.1649-1.0577.3617-2.2262.4174-1.2656.0595-1.6448.072-4.8493.079-3.2045.007-3.5825-.006-4.848-.0608M16.953 5.5864A1.44 1.44 0 1 0 18.39 4.144a1.44 1.44 0 0 0-1.437 1.4424M5.8385 12.012c.0067 3.4032 2.7706 6.1557 6.173 6.1493 3.4026-.0065 6.157-2.7701 6.1506-6.1733-.0065-3.4032-2.771-6.1565-6.174-6.1498-3.403.0067-6.156 2.771-6.1496 6.1738M8 12.0077a4 4 0 1 1 4.008 3.9921A3.9996 3.9996 0 0 1 8 12.0077" },
        { key: "onlyfans", title: "OnlyFans", color: "#00AFF0", hosts: ["onlyfans.com"], viewBox: "0 0 24 24", path: "M24 4.003h-4.015c-3.45 0-5.3.197-6.748 1.957a7.996 7.996 0 1 0 2.103 9.211c3.182-.231 5.39-2.134 6.085-5.173 0 0-2.399.585-4.43 0 4.018-.777 6.333-3.037 7.005-5.995zM5.61 11.999A2.391 2.391 0 0 1 9.28 9.97a2.966 2.966 0 0 1 2.998-2.528h.008c-.92 1.778-1.407 3.352-1.998 5.263A2.392 2.392 0 0 1 5.61 12Zm2.386-7.996a7.996 7.996 0 1 0 7.996 7.996 7.996 7.996 0 0 0-7.996-7.996Zm0 10.394A2.399 2.399 0 1 1 10.395 12a2.396 2.396 0 0 1-2.399 2.398Z" },
        { key: "patreon", title: "Patreon", color: "#FF424D", hosts: ["patreon.com"], viewBox: "0 0 24 24", path: "M22.957 7.21c-.004-3.064-2.391-5.576-5.191-6.482-3.478-1.125-8.064-.962-11.384.604C2.357 3.231 1.093 7.391 1.046 11.54c-.039 3.411.302 12.396 5.369 12.46 3.765.047 4.326-4.804 6.068-7.141 1.24-1.662 2.836-2.132 4.801-2.618 3.376-.836 5.678-3.501 5.673-7.031Z" },
        { key: "reddit", title: "Reddit", color: "#FF4500", hosts: ["reddit.com", "redd.it"], viewBox: "0 0 24 24", path: "M12 0C5.373 0 0 5.373 0 12c0 3.314 1.343 6.314 3.515 8.485l-2.286 2.286C.775 23.225 1.097 24 1.738 24H12c6.627 0 12-5.373 12-12S18.627 0 12 0Zm4.388 3.199c1.104 0 1.999.895 1.999 1.999 0 1.105-.895 2-1.999 2-.946 0-1.739-.657-1.947-1.539v.002c-1.147.162-2.032 1.15-2.032 2.341v.007c1.776.067 3.4.567 4.686 1.363.473-.363 1.064-.58 1.707-.58 1.547 0 2.802 1.254 2.802 2.802 0 1.117-.655 2.081-1.601 2.531-.088 3.256-3.637 5.876-7.997 5.876-4.361 0-7.905-2.617-7.998-5.87-.954-.447-1.614-1.415-1.614-2.538 0-1.548 1.255-2.802 2.803-2.802.645 0 1.239.218 1.712.585 1.275-.79 2.881-1.291 4.64-1.365v-.01c0-1.663 1.263-3.034 2.88-3.207.188-.911.993-1.595 1.959-1.595Zm-8.085 8.376c-.784 0-1.459.78-1.506 1.797-.047 1.016.64 1.429 1.426 1.429.786 0 1.371-.369 1.418-1.385.047-1.017-.553-1.841-1.338-1.841Zm7.406 0c-.786 0-1.385.824-1.338 1.841.047 1.017.634 1.385 1.418 1.385.785 0 1.473-.413 1.426-1.429-.046-1.017-.721-1.797-1.506-1.797Zm-3.703 4.013c-.974 0-1.907.048-2.77.135-.147.015-.241.168-.183.305.483 1.154 1.622 1.964 2.953 1.964 1.33 0 2.47-.81 2.953-1.964.057-.137-.037-.29-.184-.305-.863-.087-1.795-.135-2.769-.135Z" },
        { key: "telegram", title: "Telegram", color: "#26A5E4", hosts: ["t.me", "telegram.me", "telegram.dog"], viewBox: "0 0 24 24", path: "M11.944 0A12 12 0 0 0 0 12a12 12 0 0 0 12 12 12 12 0 0 0 12-12A12 12 0 0 0 12 0a12 12 0 0 0-.056 0zm4.962 7.224c.1-.002.321.023.465.14a.506.506 0 0 1 .171.325c.016.093.036.306.02.472-.18 1.898-.962 6.502-1.36 8.627-.168.9-.499 1.201-.82 1.23-.696.065-1.225-.46-1.9-.902-1.056-.693-1.653-1.124-2.678-1.8-1.185-.78-.417-1.21.258-1.91.177-.184 3.247-2.977 3.307-3.23.007-.032.014-.15-.056-.212s-.174-.041-.249-.024c-.106.024-1.793 1.14-5.061 3.345-.48.33-.913.49-1.302.48-.428-.008-1.252-.241-1.865-.44-.752-.245-1.349-.374-1.297-.789.027-.216.325-.437.893-.663 3.498-1.524 5.83-2.529 6.998-3.014 3.332-1.386 4.025-1.627 4.476-1.635z" },
        { key: "tiktok", title: "TikTok", color: "#FE2C55", hosts: ["tiktok.com"], viewBox: "0 0 24 24", path: "M12.525.02c1.31-.02 2.61-.01 3.91-.02.08 1.53.63 3.09 1.75 4.17 1.12 1.11 2.7 1.62 4.24 1.79v4.03c-1.44-.05-2.89-.35-4.2-.97-.57-.26-1.1-.59-1.62-.93-.01 2.92.01 5.84-.02 8.75-.08 1.4-.54 2.79-1.35 3.94-1.31 1.92-3.58 3.17-5.91 3.21-1.43.08-2.86-.31-4.08-1.03-2.02-1.19-3.44-3.37-3.65-5.71-.02-.5-.03-1-.01-1.49.18-1.9 1.12-3.72 2.58-4.96 1.66-1.44 3.98-2.13 6.15-1.72.02 1.48-.04 2.96-.04 4.44-.99-.32-2.15-.23-3.02.37-.63.41-1.11 1.04-1.36 1.75-.21.51-.15 1.07-.14 1.61.24 1.64 1.82 3.02 3.5 2.87 1.12-.01 2.19-.66 2.77-1.61.19-.33.4-.67.41-1.06.1-1.79.06-3.57.07-5.36.01-4.03-.01-8.05.02-12.07z" },
        { key: "tumblr", title: "Tumblr", color: "#35465C", hosts: ["tumblr.com"], viewBox: "0 0 24 24", path: "M14.563 24c-5.093 0-7.031-3.756-7.031-6.411V9.747H5.116V6.648c3.63-1.313 4.512-4.596 4.71-6.469C9.84.051 9.941 0 9.999 0h3.517v6.114h4.801v3.633h-4.82v7.47c.016 1.001.375 2.371 2.207 2.371h.09c.631-.02 1.486-.205 1.936-.419l1.156 3.425c-.436.636-2.4 1.374-4.156 1.404h-.178l.011.002z" },
        { key: "twitch", title: "Twitch", color: "#9146FF", hosts: ["twitch.tv"], viewBox: "0 0 24 24", path: "M11.571 4.714h1.715v5.143H11.57zm4.715 0H18v5.143h-1.714zM6 0L1.714 4.286v15.428h5.143V24l4.286-4.286h3.428L22.286 12V0zm14.571 11.143l-3.428 3.428h-3.429l-3 3v-3H6.857V1.714h13.714Z" },
        { key: "vk", title: "VK", color: "#0077FF", hosts: ["vk.com"], viewBox: "0 0 24 24", path: "m9.489.004.729-.003h3.564l.73.003.914.01.433.007.418.011.403.014.388.016.374.021.36.025.345.03.333.033c1.74.196 2.933.616 3.833 1.516.9.9 1.32 2.092 1.516 3.833l.034.333.029.346.025.36.02.373.025.588.012.41.013.644.009.915.004.98-.001 3.313-.003.73-.01.914-.007.433-.011.418-.014.403-.016.388-.021.374-.025.36-.03.345-.033.333c-.196 1.74-.616 2.933-1.516 3.833-.9.9-2.092 1.32-3.833 1.516l-.333.034-.346.029-.36.025-.373.02-.588.025-.41.012-.644.013-.915.009-.98.004-3.313-.001-.73-.003-.914-.01-.433-.007-.418-.011-.403-.014-.388-.016-.374-.021-.36-.025-.345-.03-.333-.033c-1.74-.196-2.933-.616-3.833-1.516-.9-.9-1.32-2.092-1.516-3.833l-.034-.333-.029-.346-.025-.36-.02-.373-.025-.588-.012-.41-.013-.644-.009-.915-.004-.98.001-3.313.003-.73.01-.914.007-.433.011-.418.014-.403.016-.388.021-.374.025-.36.03-.345.033-.333c.196-1.74.616-2.933 1.516-3.833.9-.9 2.092-1.32 3.833-1.516l.333-.034.346-.029.36-.025.373-.02.588-.025.41-.012.644-.013.915-.009ZM6.79 7.3H4.05c.13 6.24 3.25 9.99 8.72 9.99h.31v-3.57c2.01.2 3.53 1.67 4.14 3.57h2.84c-.78-2.84-2.83-4.41-4.11-5.01 1.28-.74 3.08-2.54 3.51-4.98h-2.58c-.56 1.98-2.22 3.78-3.8 3.95V7.3H10.5v6.92c-1.6-.4-3.62-2.34-3.71-6.92Z" },
        { key: "wordpress", title: "WordPress", color: "#21759B", hosts: ["wordpress.com", "wordpress.org"], viewBox: "0 0 24 24", path: "M21.469 6.825c.84 1.537 1.318 3.3 1.318 5.175 0 3.979-2.156 7.456-5.363 9.325l3.295-9.527c.615-1.54.82-2.771.82-3.864 0-.405-.026-.78-.07-1.11m-7.981.105c.647-.03 1.232-.105 1.232-.105.582-.075.514-.93-.067-.899 0 0-1.755.135-2.88.135-1.064 0-2.85-.15-2.85-.15-.585-.03-.661.855-.075.885 0 0 .54.061 1.125.09l1.68 4.605-2.37 7.08L5.354 6.9c.649-.03 1.234-.1 1.234-.1.585-.075.516-.93-.065-.896 0 0-1.746.138-2.874.138-.2 0-.438-.008-.69-.015C4.911 3.15 8.235 1.215 12 1.215c2.809 0 5.365 1.072 7.286 2.833-.046-.003-.091-.009-.141-.009-1.06 0-1.812.923-1.812 1.914 0 .89.513 1.643 1.06 2.531.411.72.89 1.643.89 2.977 0 .915-.354 1.994-.821 3.479l-1.075 3.585-3.9-11.61.001.014zM12 22.784c-1.059 0-2.081-.153-3.048-.437l3.237-9.406 3.315 9.087c.024.053.05.101.078.149-1.12.393-2.325.609-3.582.609M1.211 12c0-1.564.336-3.05.935-4.39L7.29 21.709C3.694 19.96 1.212 16.271 1.211 12M12 0C5.385 0 0 5.385 0 12s5.385 12 12 12 12-5.385 12-12S18.615 0 12 0" },
        { key: "x", title: "X / Twitter", color: "#FFFFFF", hosts: ["x.com", "twitter.com"], viewBox: "0 0 24 24", path: "M14.234 10.162 22.977 0h-2.072l-7.591 8.824L7.251 0H.258l9.168 13.343L.258 24H2.33l8.016-9.318L16.749 24h6.993zm-2.837 3.299-.929-1.329L3.076 1.56h3.182l5.965 8.532.929 1.329 7.754 11.09h-3.182z" },
        { key: "youtube", title: "YouTube", color: "#FF0000", hosts: ["youtube.com", "youtu.be"], viewBox: "0 0 24 24", path: "M23.498 6.186a3.016 3.016 0 0 0-2.122-2.136C19.505 3.545 12 3.545 12 3.545s-7.505 0-9.377.505A3.017 3.017 0 0 0 .502 6.186C0 8.07 0 12 0 12s0 3.93.502 5.814a3.016 3.016 0 0 0 2.122 2.136c1.871.505 9.376.505 9.376.505s7.505 0 9.377-.505a3.015 3.015 0 0 0 2.122-2.136C24 15.93 24 12 24 12s0-3.93-.502-5.814zM9.545 15.568V8.432L15.818 12l-6.273 3.568z" },
        { key: "bluesky", title: "Bluesky", color: "#1185FE", hosts: ["bsky.app", "bluesky.social", "bsky.social"], viewBox: "0 0 24 24", path: "M5.202 2.857C7.954 4.922 10.913 9.11 12 11.358c1.087-2.247 4.046-6.436 6.798-8.501C20.783 1.366 24 .213 24 3.883c0 .732-.42 6.156-.667 7.037-.856 3.061-3.978 3.842-6.755 3.37 4.854.826 6.089 3.562 3.422 6.299-5.065 5.196-7.28-1.304-7.847-2.97-.104-.305-.152-.448-.153-.327 0-.121-.05.022-.153.327-.568 1.666-2.782 8.166-7.847 2.97-2.667-2.737-1.432-5.473 3.422-6.3-2.777.473-5.899-.308-6.755-3.369C.42 10.04 0 4.615 0 3.883c0-3.67 3.217-2.517 5.202-1.026" },
        { key: "facebook", title: "Facebook", color: "#1877F2", hosts: ["facebook.com", "fb.com", "fb.me"], viewBox: "0 0 24 24", path: "M9.101 23.691v-7.98H6.627v-3.667h2.474v-1.58c0-4.085 1.848-5.978 5.858-5.978.401 0 .955.042 1.468.103a8.68 8.68 0 0 1 1.141.195v3.325a8.623 8.623 0 0 0-.653-.036 26.805 26.805 0 0 0-.733-.009c-.707 0-1.259.096-1.675.309a1.686 1.686 0 0 0-.679.622c-.258.42-.374.995-.374 1.752v1.297h3.919l-.386 2.103-.287 1.564h-3.246v8.245C19.396 23.238 24 18.179 24 12.044c0-6.627-5.373-12-12-12s-12 5.373-12 12c0 5.628 3.874 10.35 9.101 11.647Z" },
        { key: "imdb", title: "IMDb", color: "#F5C518", hosts: ["imdb.com"], viewBox: "0 0 24 24", path: "M22.3781 0H1.6218C.7411.0583.0587.7437.0018 1.5953l-.001 20.783c.0585.8761.7125 1.543 1.5559 1.6191A.337.337 0 0 0 1.6016 24h20.7971a.4579.4579 0 0 0 .0437-.002c.8727-.0768 1.5568-.8271 1.5568-1.7085V1.7098c0-.8914-.696-1.6416-1.584-1.7078A.3294.3294 0 0 0 22.3781 0zm0 .496a1.2144 1.2144 0 0 1 1.1252 1.2139v20.5797c0 .6377-.4875 1.1602-1.1045 1.2145H1.6016c-.5967-.0543-1.0645-.5297-1.1053-1.1258V1.6284C.5371 1.0185 1.0184.5364 1.6217.496h20.7564zM4.7954 8.2603v7.3636H2.8899V8.2603h1.9055zm6.5367 0v7.3636H9.6707v-4.9704l-.6711 4.9704H7.813l-.6986-4.8618-.0066 4.8618h-1.668V8.2603h2.468c.0748.4476.1492.9694.2307 1.5734l.2712 1.8713.4407-3.4447h2.4817zm2.9772 1.3289c.0742.0404.122.108.1417.2034.0279.0953.0345.3118.0345.6442v2.8548c0 .4881-.0345.7867-.0955.8954-.0609.1152-.2304.1695-.5018.1695V9.5211c.204 0 .3457.0205.4211.0681zm-.0211 6.0347c.4543 0 .8006-.0265 1.0245-.0742.2304-.0477.4204-.1357.5694-.2648.1556-.1218.2642-.298.3251-.5219.0611-.2238.1021-.6648.1021-1.3224v-2.5832c0-.6986-.0271-1.1668-.0742-1.4039-.041-.237-.1431-.4543-.3126-.6437-.1695-.1973-.4198-.3324-.7456-.421-.3191-.0808-.8542-.1285-1.7694-.1285h-1.4244v7.3636h2.3051zm5.14-1.7827c0 .3523-.0199.5762-.0544.6708-.033.0947-.1894.1424-.3046.1424-.1086 0-.19-.0477-.2238-.1351-.041-.0887-.0609-.2986-.0609-.6238v-1.9469c0-.3324.0199-.5423.0543-.6237.0338-.0808.1086-.122.2171-.122.1153 0 .2709.0412.3114.1425.041.0947.0609.2986.0609.6032v1.8926zm-2.4747-5.5809v7.3636h1.7157l.1152-.4675c.1556.1894.3251.3324.5152.4271.1828.0881.4608.1357.678.1357.3047 0 .5629-.0748.7802-.237.2165-.1562.3589-.3462.4198-.5628.0543-.2173.0887-.543.0887-.9841v-2.0675c0-.4409-.0139-.7324-.0344-.8681-.0199-.1357-.0742-.2781-.1695-.4204-.1021-.1425-.2437-.251-.4272-.3325-.1834-.0742-.3999-.1152-.6576-.1152-.2172 0-.4952.0477-.6846.1285-.1835.0887-.353.2238-.5086.4007V8.2603h-1.8309z" }
    ];

    function hostMatchesPersonSocialDomain(host, domain) {
        host = String(host || '').toLowerCase().replace(/\.$/, '');
        domain = String(domain || '').toLowerCase();
        return host === domain || host.endsWith('.' + domain);
    }

    function getPersonSocialSpec(urlText) {
        var parsed;
        try {
            parsed = new URL(urlText);
        } catch (e) {
            return null;
        }

        if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
            return null;
        }

        var host = String(parsed.hostname || '').toLowerCase();
        for (var i = 0; i < PERSON_SOCIAL_SPECS.length; i++) {
            var spec = PERSON_SOCIAL_SPECS[i];
            for (var j = 0; j < spec.hosts.length; j++) {
                if (hostMatchesPersonSocialDomain(host, spec.hosts[j])) {
                    return { spec: spec, href: parsed.href };
                }
            }
        }

        return null;
    }

    function extractPersonSocialLinks(overview) {
        var text = String(overview || '');
        var regex = /https?:\/\/[^\s<>"']+/gi;
        var byService = Object.create(null);
        var ordered = [];
        var match;

        while ((match = regex.exec(text)) !== null) {
            var parts = splitUrlTrailingPunctuation(match[0]);
            var social = getPersonSocialSpec(parts.url);
            if (!social || byService[social.spec.key]) {
                continue;
            }

            byService[social.spec.key] = true;
            ordered.push(social);
        }

        return ordered;
    }

    function restorePersonSocialIcons() {
        document.querySelectorAll('[data-jf-to-stash-person-social-icons="true"]').forEach(function (node) {
            node.remove();
        });
    }

    function findVisiblePersonNameHeading() {
        var selectors = [
            '#itemDetailPage h1.itemName.infoText',
            '.itemDetailPage h1.itemName.infoText',
            'h1.itemName.infoText'
        ];

        for (var i = 0; i < selectors.length; i++) {
            var candidates = document.querySelectorAll(selectors[i]);
            for (var j = 0; j < candidates.length; j++) {
                if (isVisibleElement(candidates[j])) {
                    return candidates[j];
                }
            }
        }

        return null;
    }

    function createPersonSocialIconLink(social) {
        var spec = social && social.spec;
        if (!spec) {
            return null;
        }

        var link = document.createElement('a');
        link.href = social.href;
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.title = spec.title;
        link.setAttribute('aria-label', spec.title);
        link.setAttribute('data-jf-to-stash-person-social-service', spec.key);
        link.style.display = 'inline-flex';
        link.style.alignItems = 'center';
        link.style.justifyContent = 'center';
        link.style.width = '1em';
        link.style.height = '1em';
        link.style.color = spec.color;
        link.style.textDecoration = 'none';
        link.style.cursor = 'pointer';
        link.style.flex = '0 0 auto';
        link.style.transition = 'transform 120ms ease, opacity 120ms ease';
        link.addEventListener('mouseenter', function () {
            link.style.transform = 'scale(1.12)';
            link.style.opacity = '0.9';
        });
        link.addEventListener('mouseleave', function () {
            link.style.transform = '';
            link.style.opacity = '';
        });

        if (spec.key === 'x') {
            link.style.filter = 'drop-shadow(0 0 1px rgba(0,0,0,.75))';
        }

        var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('viewBox', spec.viewBox);
        svg.setAttribute('width', '1em');
        svg.setAttribute('height', '1em');
        svg.setAttribute('aria-hidden', 'true');
        svg.setAttribute('focusable', 'false');
        svg.style.display = 'block';

        var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('fill', 'currentColor');
        path.setAttribute('d', spec.path);
        svg.appendChild(path);
        link.appendChild(svg);
        return link;
    }

    function ensurePersonSocialIcons() {
        if (!personSocialIconsEnabled) {
            restorePersonSocialIcons();
            return;
        }

        var itemId = parseItemId(window.location.href);
        if (!itemId) {
            restorePersonSocialIcons();
            return;
        }

        if (!personDetailsCache[itemId]) {
            requestPersonDetails(itemId);
            return;
        }

        var details = personDetailsCache[itemId];
        if (!details || !details.isPerson) {
            restorePersonSocialIcons();
            return;
        }

        var socials = extractPersonSocialLinks(details.overview);
        var heading = findVisiblePersonNameHeading();
        if (!heading) {
            return;
        }

        var signature = itemId + '|' + socials.map(function (social) {
            return social.spec.key + '=' + social.href;
        }).join('|');

        var existing = heading.querySelector('[data-jf-to-stash-person-social-icons="true"]');
        if (existing && existing.getAttribute('data-jf-to-stash-social-signature') === signature) {
            return;
        }

        restorePersonSocialIcons();

        if (!socials.length) {
            return;
        }

        var holder = document.createElement('span');
        holder.setAttribute('data-jf-to-stash-person-social-icons', 'true');
        holder.setAttribute('data-jf-to-stash-social-signature', signature);
        holder.style.display = 'inline-flex';
        holder.style.alignItems = 'center';
        holder.style.gap = '0.32em';
        holder.style.marginLeft = '0.45em';
        holder.style.verticalAlign = 'middle';
        holder.style.fontSize = '0.68em';
        holder.style.lineHeight = '1';
        holder.style.whiteSpace = 'nowrap';

        socials.forEach(function (social) {
            var icon = createPersonSocialIconLink(social);
            if (icon) {
                holder.appendChild(icon);
            }
        });

        if (holder.childNodes.length) {
            heading.appendChild(holder);
        }
    }

    function createSceneLinkIcon() {
        var holder = document.createElement('span');
        holder.className = 'detailButton-icon jfToStashSceneLinkGlyph';
        holder.setAttribute('aria-hidden', 'true');
        holder.style.display = 'inline-flex';
        holder.style.alignItems = 'center';
        holder.style.justifyContent = 'center';

        var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        // The icon supplied by the user, normalized to the path bounds so it fills a Jellyfin detail button.
        svg.setAttribute('viewBox', '180 335 202 137');
        svg.setAttribute('width', '1em');
        svg.setAttribute('height', '1em');
        svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
        svg.setAttribute('focusable', 'false');
        svg.style.display = 'block';

        var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('fill', 'currentColor');
        path.setAttribute('d', 'm 281.375 335.34375 l -84.3125 24.53125 l -16.15625 20.4375 l 73.375 25.59375 l 21.28125 -25.8125 l 5.59375 2.375 l 5.59375 -2.375 l 22.15625 26.46875 l 72.71875 -25.8125 l -17 -22.15625 l -83.25 -23.25 z m 0 6.6875 l 58.96875 17.40625 L 281.375 376 l -59.59375 -17.21875 l 59.59375 -16.75 z m 4.625 51.1875 l -0.21875 77.875 l 78.3125 -32.0625 l 0.21875 -43.65625 l -58.71875 19.34375 l -17.21875 -21.5 l -2.375 0 z M 274.71875 393.4375 L 257.5 414.9375 L 198.78125 395.59375 L 199 439.25 l 78.3125 32.0625 l -0.21875 -77.875 l -2.375 0 z');
        svg.appendChild(path);
        holder.appendChild(svg);
        return holder;
    }

    function createSceneLinkButton(itemId) {
        var button;
        try {
            button = document.createElement('button', { is: 'emby-button' });
        } catch (e) {
            button = document.createElement('button');
        }

        button.id = DETAIL_LINK_BUTTON_ID;
        button.type = 'button';
        button.setAttribute('is', 'emby-button');
        button.className = 'button-flat btnStashSceneLink detailButton emby-button';
        button.title = 'Refresh metadata and find Stash scene';
        button.setAttribute('aria-label', 'Refresh metadata and find Stash scene');
        button.setAttribute('data-item-id', itemId);

        var content = document.createElement('div');
        content.className = 'detailButton-content';
        content.appendChild(createSceneLinkIcon());
        button.appendChild(content);
        button.addEventListener('click', onSceneLinkButtonClick, false);
        return button;
    }

    function showSceneLinkError(message) {
        console.error('[JFToStashSync] Manual Stash scene link failed: ' + message);
        try {
            if (window.Dashboard && typeof window.Dashboard.alert === 'function') {
                window.Dashboard.alert(message);
                return;
            }
        } catch (e) {
            // Fall through to a native alert.
        }

        window.alert(message);
    }

    function delay(milliseconds) {
        return new Promise(function (resolve) {
            window.setTimeout(resolve, milliseconds);
        });
    }

    function setSceneLinkButtonProcessing(button, processing) {
        if (!button) {
            return;
        }

        button.disabled = !!processing;
        button.style.opacity = processing ? '0.55' : '1';
        button.title = processing
            ? 'Refreshing metadata and finding Stash scene…'
            : 'Refresh metadata and find Stash scene';
        button.setAttribute('aria-label', button.title);
    }

    async function pollManualSceneLinkJob(jobId, itemId, button, showErrors) {
        if (!jobId || !itemId || detailJobPolling[jobId]) {
            return;
        }

        detailJobPolling[jobId] = true;
        try {
            while (true) {
                await delay(1000);

                var status = await apiJson(
                    'JFToStashSync/ResolveAndLinkSceneStatus?jobId=' + encodeURIComponent(jobId));
                var isRunning = !!(status && (status.isRunning === true || status.IsRunning === true));
                if (isRunning) {
                    continue;
                }

                var success = !!(status && (status.success === true || status.Success === true));
                var sceneId = status && (status.sceneId || status.SceneId || '');
                var message = status && (status.message || status.Message || '');

                if (success) {
                    detailStatusCache[itemId] = {
                        show: false,
                        hasStashId: true,
                        sceneId: sceneId,
                        processing: false,
                        jobId: ''
                    };

                    var currentButton = document.getElementById(DETAIL_LINK_BUTTON_ID);
                    if (currentButton && currentButton.getAttribute('data-item-id') === itemId) {
                        currentButton.title = sceneId ? ('Linked to Stash scene ' + sceneId) : 'Linked to Stash';
                        currentButton.setAttribute('aria-label', currentButton.title);
                        currentButton.style.opacity = '1';
                        window.setTimeout(function () {
                            if (currentButton.isConnected) {
                                currentButton.remove();
                            }
                        }, 500);
                    }

                    console.info('[JFToStashSync] Manual Stash scene-link job completed. itemId=' + itemId + ' sceneId=' + sceneId);
                } else {
                    detailStatusCache[itemId] = {
                        show: true,
                        hasStashId: false,
                        sceneId: '',
                        processing: false,
                        jobId: ''
                    };

                    var failedButton = document.getElementById(DETAIL_LINK_BUTTON_ID);
                    if (failedButton && failedButton.getAttribute('data-item-id') === itemId) {
                        setSceneLinkButtonProcessing(failedButton, false);
                    }

                    if (showErrors && failedButton && failedButton.isConnected) {
                        showSceneLinkError(message || 'Stash scene matching failed.');
                    }
                }

                scheduleEnsureButton();
                return;
            }
        } catch (error) {
            // A Jellyfin Web page rebuild can interrupt a short polling request. The server-side
            // job continues independently; the next SceneLinkStatus call on the active page will
            // discover the running job and resume polling.
            console.debug('[JFToStashSync] Manual scene-link job polling was interrupted; server job continues.', error);
            delete detailStatusCache[itemId];
            window.setTimeout(function () {
                requestDetailLinkStatus(itemId);
            }, 1500);
        } finally {
            delete detailJobPolling[jobId];
        }
    }

    async function onSceneLinkButtonClick(event) {
        event.preventDefault();
        event.stopPropagation();

        var button = event.currentTarget;
        if (!button || button.disabled) {
            return;
        }

        var itemId = (button.getAttribute('data-item-id') || '').replace(/-/g, '');
        if (!itemId) {
            showSceneLinkError('Could not determine the Jellyfin video ID.');
            return;
        }

        setSceneLinkButtonProcessing(button, true);

        try {
            var started = await apiJson(
                'JFToStashSync/ResolveAndLinkScene?itemId=' + encodeURIComponent(itemId),
                { type: 'POST' });
            var jobId = started && (started.jobId || started.JobId || '');
            if (!jobId) {
                throw new Error((started && (started.message || started.Message)) || 'The manual Stash scene-link job could not be started.');
            }

            detailStatusCache[itemId] = {
                show: true,
                hasStashId: false,
                sceneId: '',
                processing: true,
                jobId: jobId
            };

            pollManualSceneLinkJob(jobId, itemId, button, true);
        } catch (error) {
            var message = error && error.message ? error.message : 'Stash scene matching failed.';
            setSceneLinkButtonProcessing(button, false);
            showSceneLinkError(message);
        }
    }

    function requestDetailLinkStatus(itemId) {
        if (!itemId || detailStatusPending[itemId]) {
            return;
        }

        detailStatusPending[itemId] = true;
        apiJson('JFToStashSync/SceneLinkStatus?itemId=' + encodeURIComponent(itemId))
            .then(function (result) {
                var isVideo = !!(result && (result.isVideo === true || result.IsVideo === true));
                var hasStashId = !!(result && (result.hasStashId === true || result.HasStashId === true));
                var canSearch = !!(result && (result.canSearch === true || result.CanSearch === true));
                var processing = !!(result && (result.isProcessing === true || result.IsProcessing === true));
                var jobId = result && (result.jobId || result.JobId || '');
                detailStatusCache[itemId] = {
                    show: isVideo && !hasStashId && canSearch,
                    hasStashId: hasStashId,
                    sceneId: result && (result.stashId || result.StashId || ''),
                    processing: processing,
                    jobId: jobId
                };

                if (processing && jobId) {
                    pollManualSceneLinkJob(jobId, itemId, null, false);
                }
            })
            .catch(function (error) {
                console.debug('[JFToStashSync] Could not read manual scene-link status.', error);
                detailStatusCache[itemId] = { show: false, hasStashId: false, sceneId: '', processing: false, jobId: '' };
            })
            .finally(function () {
                delete detailStatusPending[itemId];
                scheduleEnsureButton();
            });
    }

    function ensureDetailLinkButton() {
        var existing = document.getElementById(DETAIL_LINK_BUTTON_ID);
        var favoriteButton = findDetailFavoriteButton();
        if (!favoriteButton || !favoriteButton.parentElement) {
            if (existing) {
                existing.remove();
            }
            return;
        }

        var itemId = (favoriteButton.getAttribute('data-id') || '').replace(/-/g, '');
        if (!itemId) {
            if (existing) {
                existing.remove();
            }
            return;
        }

        if (existing && existing.getAttribute('data-item-id') !== itemId) {
            existing.remove();
            existing = null;
        }

        var status = detailStatusCache[itemId];
        if (!status) {
            if (existing) {
                existing.remove();
            }
            requestDetailLinkStatus(itemId);
            return;
        }

        if (!status.show) {
            if (existing) {
                existing.remove();
            }
            return;
        }

        var parent = favoriteButton.parentElement;
        if (existing) {
            if (existing.parentElement !== parent || favoriteButton.nextElementSibling !== existing) {
                favoriteButton.insertAdjacentElement('afterend', existing);
            }
            setSceneLinkButtonProcessing(existing, !!status.processing);
            return;
        }

        var button = createSceneLinkButton(itemId);
        setSceneLinkButtonProcessing(button, !!status.processing);
        favoriteButton.insertAdjacentElement('afterend', button);
        console.info('[JFToStashSync] Manual Stash scene-link button inserted after Jellyfin Favorite control. itemId=' + itemId);
    }

    function buildPersonImageUrl(person) {
        var api = getApiClient();
        if (!api || typeof api.getUrl !== 'function' || !person || !person.Id) {
            return '';
        }

        var path = 'Items/' + encodeURIComponent(person.Id) + '/Images/Primary' +
            '?fillHeight=180&fillWidth=120&quality=92';
        var tag = person.PrimaryImageTag || person.primaryImageTag || '';
        if (tag) {
            path += '&tag=' + encodeURIComponent(tag);
        }

        try {
            return api.getUrl(path);
        } catch (e) {
            return '';
        }
    }

    function createUserGlyph(sizeClass) {
        var glyph = document.createElement('span');
        glyph.className = sizeClass || '';
        glyph.setAttribute('aria-hidden', 'true');
        glyph.style.display = 'inline-flex';
        glyph.style.alignItems = 'center';
        glyph.style.justifyContent = 'center';

        var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('viewBox', '0 0 448 512');
        svg.setAttribute('width', '1em');
        svg.setAttribute('height', '1em');
        svg.setAttribute('focusable', 'false');
        svg.style.display = 'block';

        var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('fill', 'currentColor');
        path.setAttribute('d', 'M224 248a120 120 0 1 0 0-240 120 120 0 1 0 0 240zm-29.7 56C95.8 304 16 383.8 16 482.3 16 498.7 29.3 512 45.7 512l356.6 0c16.4 0 29.7-13.3 29.7-29.7 0-98.5-79.8-178.3-178.3-178.3l-59.4 0z');
        svg.appendChild(path);
        glyph.appendChild(svg);
        return glyph;
    }

    function createFavoriteGlyph(isFavorite) {
        var span = document.createElement('span');
        span.setAttribute('aria-hidden', 'true');
        span.style.display = 'inline-flex';
        span.style.alignItems = 'center';
        span.style.justifyContent = 'center';
        span.style.width = '1.7em';
        span.style.height = '1.7em';
        span.style.color = 'currentColor';

        var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('viewBox', '0 0 24 24');
        svg.setAttribute('width', '1.7em');
        svg.setAttribute('height', '1.7em');
        svg.setAttribute('focusable', 'false');
        svg.style.display = 'block';

        var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('fill', 'currentColor');
        path.setAttribute('d', isFavorite
            ? 'M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z'
            : 'M16.5 3c-1.74 0-3.41.81-4.5 2.09C10.91 3.81 9.24 3 7.5 3 4.42 3 2 5.42 2 8.5c0 3.78 3.4 6.86 8.55 11.54L12 21.35l1.45-1.32C18.6 15.36 22 12.28 22 8.5 22 5.42 19.58 3 16.5 3zm-4.4 15.55-.1.1-.1-.1C7.14 14.24 4 11.39 4 8.5 4 6.5 5.5 5 7.5 5c1.54 0 3.04.99 3.57 2.36h1.87C13.46 5.99 14.96 5 16.5 5c2 0 3.5 1.5 3.5 3.5 0 2.89-3.14 5.74-7.9 10.05z');
        svg.appendChild(path);
        span.appendChild(svg);
        return span;
    }

    function getServerId() {
        var api = getApiClient();
        if (!api) {
            return '';
        }

        try {
            if (typeof api.serverId === 'function') {
                return api.serverId() || '';
            }
            if (typeof api.serverId === 'string') {
                return api.serverId;
            }
            if (api._serverInfo && api._serverInfo.Id) {
                return api._serverInfo.Id;
            }
            if (api.serverInfo && api.serverInfo.Id) {
                return api.serverInfo.Id;
            }
        } catch (e) {
            // Fall through to URL parsing below.
        }

        try {
            var match = String(window.location.hash || '').match(/[?&]serverId=([^&]+)/i);
            return match && match[1] ? decodeURIComponent(match[1]) : '';
        } catch (e) {
            return '';
        }
    }

    function buildPersonDetailsUrl(person) {
        if (!person || !person.Id) {
            return '#';
        }

        var url = '#/details?id=' + encodeURIComponent(String(person.Id).replace(/-/g, ''));
        var serverId = getServerId();
        if (serverId) {
            url += '&serverId=' + encodeURIComponent(serverId);
        }
        return url;
    }

    function closeActorDialog() {
        var cleanup = actorDialogCleanup;
        actorDialogCleanup = null;
        if (cleanup) {
            cleanup();
        }

        var dialog = document.getElementById(ACTOR_DIALOG_ID);
        if (dialog) {
            dialog.remove();
        }
    }

    async function togglePersonFavorite(person, button) {
        if (!person || !person.Id || !button || button.disabled) {
            return;
        }

        var userId = getCurrentUserId();
        if (!userId) {
            throw new Error('Could not determine the current Jellyfin user.');
        }

        var isFavorite = button.getAttribute('data-is-favorite') === 'true';
        button.disabled = true;
        button.style.opacity = '0.55';

        try {
            var path = 'Users/' + encodeURIComponent(userId) +
                '/FavoriteItems/' + encodeURIComponent(person.Id);

            await apiJson(path, { type: isFavorite ? 'DELETE' : 'POST' });

            var newState = !isFavorite;
            button.setAttribute('data-is-favorite', newState ? 'true' : 'false');
            button.title = newState ? 'Remove from favorites' : 'Add to favorites';
            button.setAttribute('aria-label', button.title);
            button.replaceChildren(createFavoriteGlyph(newState));
            person.UserData = person.UserData || {};
            person.UserData.IsFavorite = newState;
        } catch (error) {
            console.error('[JFToStashSync] Could not change performer favorite state.', error);
            button.title = 'Favorite update failed: ' + (error && error.message ? error.message : error);
        } finally {
            button.disabled = false;
            button.style.opacity = '1';
        }
    }

    function createActorRow(person) {
        var row = document.createElement('div');
        row.style.display = 'grid';
        row.style.gridTemplateColumns = '56px minmax(0,1fr) 48px';
        row.style.alignItems = 'center';
        row.style.gap = '12px';
        row.style.padding = '8px 10px';
        row.style.borderRadius = '9px';
        row.style.background = 'rgba(255,255,255,.055)';

        var detailsUrl = buildPersonDetailsUrl(person);

        var imageLink = document.createElement('a');
        imageLink.href = detailsUrl;
        imageLink.target = '_blank';
        imageLink.rel = 'noopener noreferrer';
        imageLink.title = person.Name ? ('Open ' + person.Name + ' in a new tab') : 'Open actor in a new tab';
        imageLink.setAttribute('aria-label', imageLink.title);
        imageLink.style.display = 'block';
        imageLink.style.width = '56px';
        imageLink.style.height = '76px';
        imageLink.style.borderRadius = '8px';
        imageLink.style.overflow = 'hidden';
        imageLink.style.background = 'rgba(255,255,255,.08)';
        imageLink.style.color = 'inherit';
        imageLink.style.textDecoration = 'none';

        var image = document.createElement('div');
        image.style.width = '100%';
        image.style.height = '100%';
        image.style.display = 'flex';
        image.style.alignItems = 'center';
        image.style.justifyContent = 'center';

        var imageUrl = buildPersonImageUrl(person);
        if (imageUrl) {
            var img = document.createElement('img');
            img.src = imageUrl;
            img.alt = person.Name || 'Actor';
            img.loading = 'lazy';
            img.style.width = '100%';
            img.style.height = '100%';
            img.style.objectFit = 'cover';
            image.appendChild(img);
        } else {
            var placeholder = createUserGlyph('');
            placeholder.style.fontSize = '2em';
            placeholder.style.opacity = '.65';
            image.appendChild(placeholder);
        }
        imageLink.appendChild(image);

        var nameCell = document.createElement('div');
        nameCell.style.display = 'flex';
        nameCell.style.alignItems = 'center';
        nameCell.style.gap = '7px';
        nameCell.style.minWidth = '0';

        var nameLink = document.createElement('a');
        nameLink.href = detailsUrl;
        nameLink.target = '_blank';
        nameLink.rel = 'noopener noreferrer';
        nameLink.textContent = person.Name || 'Unknown actor';
        nameLink.title = person.Name ? ('Open ' + person.Name + ' in a new tab') : 'Open actor in a new tab';
        nameLink.style.display = 'block';
        nameLink.style.flex = '0 1 auto';
        nameLink.style.minWidth = '0';
        nameLink.style.fontSize = '1.02em';
        nameLink.style.fontWeight = '600';
        nameLink.style.overflow = 'hidden';
        nameLink.style.textOverflow = 'ellipsis';
        nameLink.style.whiteSpace = 'nowrap';
        nameLink.style.color = 'inherit';
        nameLink.style.textDecoration = 'none';

        nameCell.appendChild(nameLink);
        if (actorGenderIconsEnabled) {
            var genderIcon = createActorGenderIcon(person.__jfToStashActorRole);
            if (genderIcon) {
                genderIcon.style.flex = '0 0 auto';
                genderIcon.style.fontSize = '1.05em';
                nameCell.appendChild(genderIcon);
            }
        }

        [imageLink, nameLink].forEach(function (link) {
            link.addEventListener('click', function () {
                window.setTimeout(closeActorDialog, 0);
            });
        });

        var isFavorite = !!(person.UserData && person.UserData.IsFavorite);
        var favButton;
        try {
            favButton = document.createElement('button', { is: 'paper-icon-button-light' });
        } catch (e) {
            favButton = document.createElement('button');
        }
        favButton.type = 'button';
        favButton.setAttribute('is', 'paper-icon-button-light');
        favButton.className = 'paper-icon-button-light';
        favButton.setAttribute('data-is-favorite', isFavorite ? 'true' : 'false');
        favButton.title = isFavorite ? 'Remove from favorites' : 'Add to favorites';
        favButton.setAttribute('aria-label', favButton.title);
        favButton.style.width = '44px';
        favButton.style.height = '44px';
        favButton.style.border = '0';
        favButton.style.borderRadius = '50%';
        favButton.style.background = 'transparent';
        favButton.style.cursor = 'pointer';
        favButton.style.color = 'inherit';
        favButton.style.padding = '0';
        favButton.style.display = 'inline-flex';
        favButton.style.alignItems = 'center';
        favButton.style.justifyContent = 'center';
        favButton.replaceChildren(createFavoriteGlyph(isFavorite));
        favButton.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();
            togglePersonFavorite(person, favButton).catch(function (error) {
                console.error('[JFToStashSync] Favorite toggle failed.', error);
            });
        });

        row.appendChild(imageLink);
        row.appendChild(nameCell);
        row.appendChild(favButton);
        return row;
    }

    function positionActorDialog(dialog, anchorButton) {
        if (!dialog || !dialog.isConnected || !anchorButton || !anchorButton.isConnected) {
            return;
        }

        var margin = 10;
        var gap = 10;
        var anchorRect = anchorButton.getBoundingClientRect();
        var dialogRect = dialog.getBoundingClientRect();
        var width = dialogRect.width;
        var height = dialogRect.height;

        var left = anchorRect.left + (anchorRect.width / 2) - (width / 2);
        left = Math.max(margin, Math.min(left, window.innerWidth - width - margin));

        var top = anchorRect.top - height - gap;
        if (top < margin) {
            top = anchorRect.bottom + gap;
        }
        if (top + height > window.innerHeight - margin) {
            top = Math.max(margin, window.innerHeight - height - margin);
        }

        dialog.style.left = Math.round(left) + 'px';
        dialog.style.top = Math.round(top) + 'px';
    }

    function showActorDialog(persons, anchorButton) {
        closeActorDialog();

        if (!anchorButton || !anchorButton.isConnected) {
            return;
        }

        var panel = document.createElement('div');
        panel.id = ACTOR_DIALOG_ID;
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-modal', 'false');
        panel.setAttribute('aria-label', 'Actors');
        panel.style.position = 'fixed';
        panel.style.zIndex = '999999';
        panel.style.width = 'min(430px, calc(100vw - 20px))';
        panel.style.maxHeight = 'min(62vh, 560px)';
        panel.style.background = 'rgba(24,24,24,.98)';
        panel.style.color = '#fff';
        panel.style.border = '1px solid rgba(255,255,255,.12)';
        panel.style.borderRadius = '12px';
        panel.style.boxShadow = '0 14px 40px rgba(0,0,0,.55)';
        panel.style.display = 'flex';
        panel.style.flexDirection = 'column';
        panel.style.overflow = 'hidden';
        panel.style.boxSizing = 'border-box';

        var list = document.createElement('div');
        list.setAttribute('data-jf-to-stash-actor-list', 'true');
        list.style.display = 'grid';
        list.style.gridTemplateColumns = 'minmax(0, 1fr)';
        list.style.gap = '8px';
        list.style.padding = '10px';
        list.style.overflowY = 'auto';
        list.style.overscrollBehavior = 'contain';
        list.style.flex = '1 1 auto';
        list.style.minHeight = '0';

        if (!Array.isArray(persons) || !persons.length) {
            var empty = document.createElement('div');
            empty.textContent = 'No actors found for this video.';
            empty.style.padding = '18px';
            empty.style.opacity = '.75';
            list.appendChild(empty);
        } else {
            persons.forEach(function (person) {
                list.appendChild(createActorRow(person));
            });
        }

        panel.appendChild(list);
        document.body.appendChild(panel);
        anchorButton.setAttribute('aria-expanded', 'true');

        function reposition() {
            positionActorDialog(panel, anchorButton);
        }

        function onDocumentPointerDown(event) {
            if (!panel.contains(event.target) && !anchorButton.contains(event.target)) {
                closeActorDialog();
            }
        }

        function onDocumentKeyDown(event) {
            if (event.key === 'Escape' || event.key === 'Esc') {
                event.preventDefault();
                closeActorDialog();
            }
        }

        function onViewportChanged() {
            window.requestAnimationFrame(reposition);
        }

        actorDialogCleanup = function () {
            document.removeEventListener('pointerdown', onDocumentPointerDown, true);
            document.removeEventListener('keydown', onDocumentKeyDown, true);
            window.removeEventListener('resize', onViewportChanged);
            window.removeEventListener('scroll', onViewportChanged, true);
            if (anchorButton && anchorButton.isConnected) {
                anchorButton.setAttribute('aria-expanded', 'false');
            }
        };

        document.addEventListener('keydown', onDocumentKeyDown, true);
        window.addEventListener('resize', onViewportChanged);
        window.addEventListener('scroll', onViewportChanged, true);
        window.requestAnimationFrame(reposition);

        // Delay outside-click binding so the click that opened the popup cannot close it again.
        window.setTimeout(function () {
            if (panel.isConnected) {
                document.addEventListener('pointerdown', onDocumentPointerDown, true);
            }
        }, 0);
    }

    async function loadCurrentVideoActors(itemId) {
        var userId = getCurrentUserId();
        if (!userId) {
            throw new Error('Could not determine the current Jellyfin user.');
        }

        var personsPromise = apiJson(
            'Persons' +
            '?UserId=' + encodeURIComponent(userId) +
            '&AppearsInItemId=' + encodeURIComponent(itemId) +
            '&PersonTypes=Actor' +
            '&EnableUserData=true' +
            '&EnableImages=true' +
            '&ImageTypeLimit=1' +
            '&EnableImageTypes=Primary' +
            '&Limit=100');

        var itemPeoplePromise = actorGenderIconsEnabled
            ? apiJson(
                'Users/' + encodeURIComponent(userId) +
                '/Items/' + encodeURIComponent(itemId) +
                '?Fields=People')
            : Promise.resolve(null);

        var results = await Promise.all([personsPromise, itemPeoplePromise]);
        var result = results[0];
        var item = results[1];
        var items = result && (result.Items || result.items);
        items = Array.isArray(items) ? items : [];

        if (actorGenderIconsEnabled && item) {
            var roleMap = Object.create(null);
            var people = item.People || item.people;
            if (Array.isArray(people)) {
                people.forEach(function (person) {
                    if (!person) {
                        return;
                    }

                    var type = String(person.Type || person.type || '').toLowerCase();
                    if (type && type !== 'actor') {
                        return;
                    }

                    var id = String(person.Id || person.id || '')
                        .replace(/-/g, '')
                        .toLowerCase();
                    var role = person.Role !== undefined ? person.Role : person.role;
                    if (id && role) {
                        roleMap[id] = String(role);
                    }
                });
            }

            actorRoleCache[itemId] = roleMap;
            items.forEach(function (person) {
                var id = String((person && (person.Id || person.id)) || '')
                    .replace(/-/g, '')
                    .toLowerCase();
                person.__jfToStashActorRole = id ? (roleMap[id] || '') : '';
            });
        }

        return items;
    }

    async function onActorButtonClick(event) {
        event.preventDefault();
        event.stopPropagation();

        var button = event.currentTarget;
        if (!button || button.disabled) {
            return;
        }

        if (document.getElementById(ACTOR_DIALOG_ID)) {
            closeActorDialog();
            return;
        }

        button.disabled = true;
        button.style.opacity = '0.55';
        try {
            var itemId = (button.getAttribute('data-item-id') || '').replace(/-/g, '') || await resolveCurrentItemId();
            if (!itemId) {
                throw new Error('Could not determine the currently playing video.');
            }

            var actors = await loadCurrentVideoActors(itemId);
            showActorDialog(actors, button);
        } catch (error) {
            console.error('[JFToStashSync] Could not load actors for the current video.', error);
            button.title = 'Actors: ' + (error && error.message ? error.message : 'load failed');
            window.setTimeout(function () {
                if (button.isConnected) {
                    button.title = 'Actors';
                }
            }, 1800);
        } finally {
            button.disabled = false;
            button.style.opacity = '1';
        }
    }

    function createActorOsdButton() {
        var button;
        try {
            button = document.createElement('button', { is: 'paper-icon-button-light' });
        } catch (e) {
            button = document.createElement('button');
        }

        button.id = ACTOR_BUTTON_ID;
        button.type = 'button';
        button.setAttribute('is', 'paper-icon-button-light');
        button.className = 'btnJFToStashActors autoSize paper-icon-button-light';
        button.title = 'Actors';
        button.setAttribute('aria-label', 'Actors');
        button.setAttribute('aria-haspopup', 'dialog');
        button.setAttribute('aria-expanded', 'false');
        button.appendChild(createUserGlyph('xlargePaperIconButton jfToStashActorsGlyph'));
        button.addEventListener('click', onActorButtonClick, false);
        return button;
    }

    function ensureActorButton() {
        var existing = document.getElementById(ACTOR_BUTTON_ID);
        if (!actorListEnabled) {
            if (existing) {
                existing.remove();
            }
            closeActorDialog();
            return;
        }

        var favoriteButton = findFavoriteButton();
        if (!favoriteButton || !favoriteButton.parentElement) {
            if (existing) {
                existing.remove();
            }
            closeActorDialog();
            return;
        }

        var parent = favoriteButton.parentElement;
        var jellyfinItemId = (favoriteButton.getAttribute('data-id') || '').replace(/-/g, '');
        var oButton = document.getElementById(BUTTON_ID);
        var anchor = oButton && oButton.parentElement === parent ? oButton : favoriteButton;

        if (existing) {
            if (jellyfinItemId) {
                existing.setAttribute('data-item-id', jellyfinItemId);
            } else {
                existing.removeAttribute('data-item-id');
            }

            if (existing.parentElement !== parent || anchor.nextElementSibling !== existing) {
                anchor.insertAdjacentElement('afterend', existing);
            }
            return;
        }

        var button = createActorOsdButton();
        if (jellyfinItemId) {
            button.setAttribute('data-item-id', jellyfinItemId);
        }
        anchor.insertAdjacentElement('afterend', button);
        console.info('[JFToStashSync] Actors button inserted in Jellyfin Web player OSD.');
    }

    function createOsdButton() {
        var button;
        try {
            // Jellyfin's native OSD controls use the customized built-in button.
            button = document.createElement('button', { is: 'paper-icon-button-light' });
        } catch (e) {
            button = document.createElement('button');
        }

        button.id = BUTTON_ID;
        button.type = 'button';
        button.setAttribute('is', 'paper-icon-button-light');
        button.className = 'btnStashOCounter autoSize paper-icon-button-light';
        button.title = 'Stash: +1 to O counter';
        button.setAttribute('aria-label', 'Stash: +1 to O counter');

        var glyph = document.createElement('span');
        glyph.className = 'xlargePaperIconButton jfToStashOCounterGlyph';
        glyph.setAttribute('aria-hidden', 'true');
        glyph.style.display = 'inline-flex';
        glyph.style.alignItems = 'center';
        glyph.style.justifyContent = 'center';

        var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('viewBox', '0 0 36 36');
        svg.setAttribute('width', '1em');
        svg.setAttribute('height', '1em');
        svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
        svg.setAttribute('focusable', 'false');
        svg.style.display = 'block';

        var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('fill', 'currentColor');
        path.setAttribute('d', 'M22.855.758L7.875 7.024l12.537 9.733c2.633 2.224 6.377 2.937 9.77 1.518c4.826-2.018 7.096-7.576 5.072-12.413C33.232 1.024 27.68-1.261 22.855.758zm-9.962 17.924L2.05 10.284L.137 23.529a7.993 7.993 0 0 0 2.958 7.803a8.001 8.001 0 0 0 9.798-12.65zm15.339 7.015l-8.156-4.69l-.033 9.223c-.088 2 .904 3.98 2.75 5.041a5.462 5.462 0 0 0 7.479-2.051c1.499-2.644.589-6.013-2.04-7.523z');
        svg.appendChild(path);
        glyph.appendChild(svg);
        button.appendChild(glyph);

        button.addEventListener('click', onButtonClick, false);
        return button;
    }

    function setButtonFeedback(button, title, isError) {
        if (!button) {
            return;
        }

        button.title = title;
        button.setAttribute('aria-label', title);
        button.style.opacity = isError ? '0.65' : '1';

        window.setTimeout(function () {
            if (button.isConnected) {
                button.title = 'Stash: +1 to O counter';
                button.setAttribute('aria-label', 'Stash: +1 to O counter');
                button.style.opacity = '1';
            }
        }, 1400);
    }

    async function onButtonClick(event) {
        event.preventDefault();
        event.stopPropagation();

        var button = event.currentTarget;
        if (!button || button.disabled) {
            return;
        }

        button.disabled = true;
        button.style.opacity = '0.55';
        button.title = 'Stash: updating O counter…';
        button.setAttribute('aria-label', 'Stash: updating O counter');

        try {
            var itemId = (button.getAttribute('data-item-id') || '').replace(/-/g, '') || await resolveCurrentItemId();
            var userId = getCurrentUserId();
            if (!itemId) {
                throw new Error('Could not determine the currently playing video.');
            }
            if (!userId) {
                throw new Error('Could not determine the current Jellyfin user.');
            }

            var result = await apiJson(
                'JFToStashSync/IncrementO' +
                    '?itemId=' + encodeURIComponent(itemId) +
                    '&userId=' + encodeURIComponent(userId),
                { type: 'POST' });

            var count = result && (result.count !== undefined ? result.count : result.Count);
            var sceneId = result && (result.sceneId || result.SceneId || '');
            setButtonFeedback(
                button,
                'Stash O counter: ' + (count !== undefined ? count : '+1') + (sceneId ? ' (scene ' + sceneId + ')' : ''),
                false);
        } catch (error) {
            console.error('[JFToStashSync] Failed to increment Stash O counter.', error);
            setButtonFeedback(
                button,
                'JF To Stash Sync: ' + (error && error.message ? error.message : 'O-counter update failed'),
                true);
        } finally {
            button.disabled = false;
        }
    }

    function ensureButton() {

        var existing = document.getElementById(BUTTON_ID);
        if (!configEnabled) {
            if (existing) {
                existing.remove();
            }
            return;
        }

        var favoriteButton = findFavoriteButton();
        if (!favoriteButton || !favoriteButton.parentElement) {
            if (existing) {
                existing.remove();
            }
            return;
        }

        var parent = favoriteButton.parentElement;
        var jellyfinItemId = (favoriteButton.getAttribute('data-id') || '').replace(/-/g, '');

        // If the button already exists, keep it immediately to the right of Favorite.
        // Jellyfin rebuilds the OSD dynamically, so this check also repairs its position.
        if (existing) {
            if (jellyfinItemId) {
                existing.setAttribute('data-item-id', jellyfinItemId);
            } else {
                existing.removeAttribute('data-item-id');
            }

            if (existing.parentElement !== parent || favoriteButton.nextElementSibling !== existing) {
                favoriteButton.insertAdjacentElement('afterend', existing);
                console.info('[JFToStashSync] O+ button moved next to Jellyfin Favorite control.');
            }
            return;
        }

        var button = createOsdButton();
        if (jellyfinItemId) {
            button.setAttribute('data-item-id', jellyfinItemId);
        }

        // Exact requested placement: directly after Jellyfin's native Favorite button.
        favoriteButton.insertAdjacentElement('afterend', button);
        console.info('[JFToStashSync] O+ button inserted immediately after Jellyfin Favorite control.');
    }

    function ensureAllButtons() {
        scheduled = false;
        ensureButton();
        ensureActorButton();
        ensureDetailLinkButton();
        ensureActorGenderIcons();
        ensurePersonOverviewLinks();
        ensurePersonSocialIcons();
    }

    function scheduleEnsureButton() {
        if (scheduled) {
            return;
        }
        scheduled = true;
        window.requestAnimationFrame(ensureAllButtons);
    }

    async function loadConfig() {
        var api = getApiClient();
        if (!api || typeof api.fetch !== 'function' || typeof api.getUrl !== 'function') {
            window.setTimeout(loadConfig, 1000);
            return;
        }

        try {
            var config = await apiJson('JFToStashSync/PlayerOCounterConfig');
            configEnabled = !!(config && (config.enabled === true || config.Enabled === true));
            actorListEnabled = !!(config && (config.actorListEnabled === true || config.ActorListEnabled === true));
            actorGenderIconsEnabled = !!(config && (config.actorGenderIconsEnabled === true || config.ActorGenderIconsEnabled === true));
            personOverviewLinksEnabled = !!(config && (config.personOverviewLinksEnabled === true || config.PersonOverviewLinksEnabled === true));
            personSocialIconsEnabled = !!(config && (config.personSocialIconsEnabled === true || config.PersonSocialIconsEnabled === true));
        } catch (error) {
            configEnabled = false;
            actorListEnabled = false;
            actorGenderIconsEnabled = false;
            personOverviewLinksEnabled = false;
            personSocialIconsEnabled = false;
            console.warn('[JFToStashSync] Could not load Jellyfin Web integration configuration.', error);
        }

        scheduleEnsureButton();
    }

    function start() {
        loadConfig();
        observer = new MutationObserver(scheduleEnsureButton);
        observer.observe(document.documentElement, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['data-id']
        });

        function onSpaNavigation() {
            // Jellyfin Web is a SPA and can reuse an existing details-page DOM while only
            // changing visibility/data-id. Clear the status cache so the active item is
            // re-checked immediately instead of waiting for a full browser reload.
            restoreAllActorGenderIcons();
            restorePersonOverviewLinks();
            restorePersonSocialIcons();
            closeActorDialog();
            detailStatusCache = Object.create(null);
            actorRoleCache = Object.create(null);
            actorRolePending = Object.create(null);
            personDetailsCache = Object.create(null);
            personDetailsPending = Object.create(null);
            scheduleEnsureButton();
            window.setTimeout(scheduleEnsureButton, 100);
            window.setTimeout(scheduleEnsureButton, 500);
        }

        window.addEventListener('hashchange', onSpaNavigation);
        window.addEventListener('popstate', onSpaNavigation);
        document.addEventListener('viewshow', onSpaNavigation, true);
        document.addEventListener('pageshow', onSpaNavigation, true);
        window.setInterval(scheduleEnsureButton, 1000);
        console.info('[JFToStashSync] Jellyfin Web integrations loaded (O-counter, player actors, manual scene link, actor gender icons, person overview links, person social icons).');
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start, { once: true });
    } else {
        start();
    }
}());
