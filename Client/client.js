(function () {
    let watchedCache = {};
    let lastFetchTime = 0;
    let isFetching = false;

    let storageData = null;
    let lastStorageFetch = 0;
    let isFetchingStorage = false;

    // Inject CSS styles for avatars, stacked deck animation, and home storage bar
    if (!document.getElementById('jellycheck-styles')) {
        const style = document.createElement('style');
        style.id = 'jellycheck-styles';
        style.innerHTML = `
            .jellycheck-indicators {
                position: absolute;
                top: 6px;
                left: 6px;
                display: flex;
                flex-direction: row;
                z-index: 999999 !important;
                pointer-events: auto;
                background: rgba(0, 0, 0, 0.45);
                padding: 3px 5px;
                border-radius: 12px;
                backdrop-filter: blur(4px);
                border: 0.5px solid rgba(255, 255, 255, 0.2);
                box-shadow: 0 4px 12px rgba(0, 0, 0, 0.4);
                transition: gap 0.25s cubic-bezier(0.4, 0, 0.2, 1), background 0.25s;
            }
            .jellycheck-indicators:hover {
                background: rgba(0, 0, 0, 0.75);
            }
            .jellycheck-avatar {
                width: 20px;
                height: 20px;
                border-radius: 50%;
                overflow: hidden;
                display: flex;
                align-items: center;
                justify-content: center;
                border: 1.5px solid #ffffff;
                box-shadow: 0 2px 4px rgba(0, 0, 0, 0.4);
                margin-right: -6px;
                transition: all 0.25s cubic-bezier(0.4, 0, 0.2, 1);
                position: relative;
                cursor: pointer;
            }
            .jellycheck-indicators:hover .jellycheck-avatar {
                margin-right: 3px;
            }
            .jellycheck-avatar:hover {
                transform: scale(1.3);
                z-index: 100 !important;
                box-shadow: 0 4px 10px rgba(0, 0, 0, 0.6);
                border-color: #00f2fe;
            }
            .jellycheck-avatar img {
                width: 100%;
                height: 100%;
                object-fit: cover;
                display: block;
            }
            .jellycheck-avatar span {
                color: #ffffff;
                font-size: 10px;
                font-weight: bold;
                line-height: 1;
            }

            /* Jellycheck Storage Bar on Home / Start Page */
            .jellycheck-storage-wrapper {
                width: 100%;
                box-sizing: border-box;
                padding: 12px 1.5% 6px 1.5%;
                display: flex;
                justify-content: center;
                animation: jc-fade-in 0.35s ease-out;
            }
            @keyframes jc-fade-in {
                from { opacity: 0; transform: translateY(-4px); }
                to { opacity: 1; transform: translateY(0); }
            }
            .jellycheck-storage-card {
                width: 100%;
                background: linear-gradient(135deg, rgba(20, 24, 38, 0.72) 0%, rgba(12, 14, 24, 0.84) 100%);
                border: 1px solid rgba(255, 255, 255, 0.08);
                border-radius: 12px;
                padding: 11px 18px;
                backdrop-filter: blur(12px);
                -webkit-backdrop-filter: blur(12px);
                box-shadow: 0 4px 20px rgba(0, 0, 0, 0.3);
                transition: border-color 0.25s, box-shadow 0.25s;
                box-sizing: border-box;
            }
            .jellycheck-storage-card:hover {
                border-color: rgba(0, 242, 254, 0.3);
                box-shadow: 0 6px 24px rgba(0, 242, 254, 0.12);
            }
            .jellycheck-storage-header {
                display: flex;
                justify-content: space-between;
                align-items: center;
                margin-bottom: 7px;
                font-size: 0.88rem;
                font-family: inherit;
            }
            .jellycheck-storage-title-area {
                display: flex;
                align-items: center;
                gap: 8px;
                color: #e2e8f0;
                font-weight: 600;
                letter-spacing: 0.2px;
            }
            .jellycheck-storage-icon {
                width: 16px;
                height: 16px;
                color: #00f2fe;
                flex-shrink: 0;
            }
            .jellycheck-storage-stats {
                display: flex;
                align-items: center;
                gap: 6px;
                font-size: 0.85rem;
                color: #94a3b8;
            }
            .jellycheck-storage-stats strong {
                color: #00f2fe;
                font-weight: 700;
            }
            .jellycheck-storage-pct {
                color: #64748b;
                font-size: 0.8rem;
            }
            .jellycheck-storage-track {
                width: 100%;
                height: 7px;
                background: rgba(255, 255, 255, 0.08);
                border-radius: 6px;
                overflow: hidden;
            }
            .jellycheck-storage-bar {
                height: 100%;
                border-radius: 6px;
                background: linear-gradient(90deg, #00f2fe 0%, #4facfe 100%);
                box-shadow: 0 0 10px rgba(0, 242, 254, 0.35);
                transition: width 0.7s cubic-bezier(0.4, 0, 0.2, 1);
            }
            .jellycheck-storage-bar.warning {
                background: linear-gradient(90deg, #ffb300 0%, #ff7043 100%);
                box-shadow: 0 0 10px rgba(255, 179, 0, 0.35);
            }
            .jellycheck-storage-bar.critical {
                background: linear-gradient(90deg, #ff5252 0%, #f44336 100%);
                box-shadow: 0 0 10px rgba(255, 82, 82, 0.4);
            }
            @media (max-width: 600px) {
                .jellycheck-storage-header {
                    flex-direction: column;
                    align-items: flex-start;
                    gap: 4px;
                }
                .jellycheck-storage-stats {
                    font-size: 0.8rem;
                }
            }
        `;
        document.head.appendChild(style);
    }

    function getApiUrl(subPath) {
        if (window.ApiClient && typeof window.ApiClient.getUrl === 'function') {
            return window.ApiClient.getUrl(subPath);
        }
        const basePath = window.location.pathname.split('/web/')[0] || '';
        return `${basePath}/${subPath}`;
    }

    function normalizeId(id) {
        return id ? id.replace(/-/g, '').toLowerCase() : '';
    }

    /**
     * Extract the Jellyfin item ID from within a .cardBox element.
     */
    function getItemIdFromCardBox(cardBox) {
        const dataEl = cardBox.querySelector('[data-id]') || cardBox.querySelector('[data-itemid]');
        if (dataEl) {
            const id = dataEl.getAttribute('data-id') || dataEl.getAttribute('data-itemid');
            if (id) return id;
        }

        const links = cardBox.querySelectorAll('a[href]');
        for (const link of links) {
            const href = link.getAttribute('href');
            if (!href) continue;

            const hexMatch = href.match(/[?&]id=([0-9a-f]{32})/i);
            if (hexMatch) return hexMatch[1];

            const uuidMatch = href.match(/[?&]id=([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/i);
            if (uuidMatch) return uuidMatch[1];

            const pathHex = href.match(/\/([0-9a-f]{32})(?:[?&#/]|$)/i);
            if (pathHex) return pathHex[1];
        }

        return null;
    }

    async function fetchWatchedData() {
        if (isFetching) return;
        isFetching = true;
        try {
            const url = getApiUrl('jellycheck/watched');
            let data;
            if (window.ApiClient && typeof window.ApiClient.getJSON === 'function') {
                data = await window.ApiClient.getJSON(url);
            } else {
                const token = window.ApiClient && typeof window.ApiClient.accessToken === 'function' ? window.ApiClient.accessToken() : '';
                const headers = { 'Accept': 'application/json' };
                if (token) {
                    headers['Authorization'] = `MediaBrowser Client="Jellycheck", Device="Web", DeviceId="JellycheckWeb", Version="1.0.0", Token="${token}"`;
                }
                const response = await fetch(url, { headers });
                data = await response.json();
            }
            
            watchedCache = {};
            if (data) {
                for (const key in data) {
                    watchedCache[normalizeId(key)] = data[key];
                }
            }
            
            lastFetchTime = Date.now();
            updateAllVisibleCards();
        } catch (err) {
            console.error('[Jellycheck] Error fetching watched data:', err);
        } finally {
            isFetching = false;
        }
    }

    async function fetchStorageData(force = false) {
        if (isFetchingStorage) return;
        if (!force && Date.now() - lastStorageFetch < 60000 && storageData) {
            updateStorageBar();
            return;
        }
        isFetchingStorage = true;
        try {
            const url = getApiUrl('jellycheck/storage');
            let data;
            if (window.ApiClient && typeof window.ApiClient.getJSON === 'function') {
                data = await window.ApiClient.getJSON(url);
            } else {
                const token = window.ApiClient && typeof window.ApiClient.accessToken === 'function' ? window.ApiClient.accessToken() : '';
                const headers = { 'Accept': 'application/json' };
                if (token) {
                    headers['Authorization'] = `MediaBrowser Client="Jellycheck", Device="Web", DeviceId="JellycheckWeb", Version="1.0.0", Token="${token}"`;
                }
                const response = await fetch(url, { headers });
                data = await response.json();
            }

            if (data) {
                storageData = data;
                lastStorageFetch = Date.now();
                updateStorageBar();
            }
        } catch (err) {
            console.warn('[Jellycheck] Could not fetch storage data:', err);
        } finally {
            isFetchingStorage = false;
        }
    }

    function getHomeSectionsContainer() {
        // Active (visible) home container
        const activeContainer = document.querySelector('.page:not(.hide) .homeSectionsContainer');
        if (activeContainer) return activeContainer;

        const anyContainer = document.querySelector('.homeSectionsContainer');
        if (anyContainer) {
            const page = anyContainer.closest('.page');
            if (!page || !page.classList.contains('hide')) {
                return anyContainer;
            }
        }
        return null;
    }

    function updateStorageBar() {
        const homeSections = getHomeSectionsContainer();
        const existing = document.getElementById('jellycheck-storage-wrapper');

        // If not on home page or storage is disabled, remove existing bar
        if (!homeSections || (storageData && storageData.enabled === false)) {
            if (existing) existing.remove();
            return;
        }

        // If data not yet fetched, trigger fetch
        if (!storageData) {
            fetchStorageData();
            return;
        }

        // Check if existing bar is already attached inside the correct container
        if (existing && existing.parentElement === homeSections) {
            return; // Up-to-date and in place
        }

        if (existing) {
            existing.remove();
        }

        const pct = typeof storageData.percentUsed === 'number' ? storageData.percentUsed : 0;
        let barClass = '';
        if (pct >= 90) {
            barClass = 'critical';
        } else if (pct >= 80) {
            barClass = 'warning';
        }

        let tooltipDetails = `${storageData.usedSpaceFormatted || ''} used of ${storageData.totalSpaceFormatted || ''} (${pct}% used)`;
        if (storageData.disks && storageData.disks.length > 1) {
            tooltipDetails += '\n' + storageData.disks.map(d => `${d.path || d.label}: ${d.freeSpaceFormatted} free / ${d.totalSpaceFormatted}`).join('\n');
        }

        const wrapper = document.createElement('div');
        wrapper.id = 'jellycheck-storage-wrapper';
        wrapper.className = 'jellycheck-storage-wrapper';
        wrapper.innerHTML = `
            <div class="jellycheck-storage-card" title="${tooltipDetails}">
                <div class="jellycheck-storage-header">
                    <div class="jellycheck-storage-title-area">
                        <svg class="jellycheck-storage-icon" viewBox="0 0 24 24">
                            <path fill="currentColor" d="M4 3h16a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2m0 2v4h16V5H4m14 1a1 1 0 0 0-1 1a1 1 0 0 0 1 1a1 1 0 0 0 1-1a1 1 0 0 0-1-1m-3 0a1 1 0 0 0-1 1a1 1 0 0 0 1 1a1 1 0 0 0 1-1a1 1 0 0 0-1-1M4 13h16a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2v-4a2 2 0 0 1 2-2m0 2v4h16v-4H4m14 1a1 1 0 0 0-1 1a1 1 0 0 0 1 1a1 1 0 0 0 1-1a1 1 0 0 0-1-1m-3 0a1 1 0 0 0-1 1a1 1 0 0 0 1 1a1 1 0 0 0 1-1a1 1 0 0 0-1-1Z"/>
                        </svg>
                        <span>Storage</span>
                    </div>
                    <div class="jellycheck-storage-stats">
                        <span><strong>${storageData.freeSpaceFormatted || '0 B'}</strong> free</span>
                        <span>of ${storageData.totalSpaceFormatted || '0 B'}</span>
                        <span class="jellycheck-storage-pct">(${pct}% used)</span>
                    </div>
                </div>
                <div class="jellycheck-storage-track">
                    <div class="jellycheck-storage-bar ${barClass}" style="width: ${Math.min(100, Math.max(0, pct))}%;"></div>
                </div>
            </div>
        `;

        homeSections.prepend(wrapper);
    }

    function getGradientForUser(username) {
        if (!username) return '#4facfe';
        let hash = 0;
        for (let i = 0; i < username.length; i++) {
            hash = username.charCodeAt(i) + ((hash << 5) - hash);
        }
        const h1 = Math.abs(hash % 360);
        const h2 = (h1 + 60) % 360;
        return `linear-gradient(135deg, hsl(${h1}, 80%, 45%), hsl(${h2}, 80%, 35%))`;
    }

    function getUserImageUrl(userId) {
        let url = '';
        if (window.ApiClient && typeof window.ApiClient.getUrl === 'function') {
            url = window.ApiClient.getUrl(`Users/${userId}/Images/Primary`);
        } else {
            const basePath = window.location.pathname.split('/web/')[0] || '';
            url = `${basePath}/Users/${userId}/Images/Primary`;
        }

        if (window.ApiClient && typeof window.ApiClient.accessToken === 'function') {
            const token = window.ApiClient.accessToken();
            if (token) {
                const sep = url.includes('?') ? '&' : '?';
                url += `${sep}api_key=${token}`;
            }
        }
        return url;
    }

    function updateCard(cardBox) {
        const itemId = getItemIdFromCardBox(cardBox);
        if (!itemId) return;

        const normalizedItemId = normalizeId(itemId);
        const users = watchedCache[normalizedItemId] || [];

        const fingerprint = users.map(u => String(u.Id || u.id || '')).join(',');

        const existing = cardBox.querySelector('.jellycheck-indicators');
        const currentFingerprint = cardBox.getAttribute('data-jellycheck-fp');

        if (existing && currentFingerprint === fingerprint) return;

        if (existing) existing.remove();

        if (users.length === 0) {
            cardBox.removeAttribute('data-jellycheck-fp');
            return;
        }

        cardBox.setAttribute('data-jellycheck-fp', fingerprint);

        const container = document.createElement('div');
        container.className = 'jellycheck-indicators';

        users.forEach((user, idx) => {
            const userId = user.Id || user.id || '';
            const userName = user.Name || user.name || 'User';
            const hasPrimaryImage = user.HasPrimaryImage || user.hasPrimaryImage || false;

            const avatar = document.createElement('div');
            avatar.className = 'jellycheck-avatar';
            avatar.style.zIndex = 10 - idx;
            avatar.title = userName;

            if (hasPrimaryImage && userId) {
                const img = document.createElement('img');
                img.src = getUserImageUrl(userId);
                avatar.appendChild(img);
            } else {
                avatar.style.background = getGradientForUser(userName);
                const span = document.createElement('span');
                span.innerText = userName.charAt(0).toUpperCase();
                avatar.appendChild(span);
            }
            container.appendChild(avatar);
        });

        const imageContainer = cardBox.querySelector('.cardImageContainer')
            || cardBox.querySelector('.cardScalable')
            || cardBox;

        const computedStyle = window.getComputedStyle(imageContainer);
        if (computedStyle.position === 'static') {
            imageContainer.style.position = 'relative';
        }
        imageContainer.style.overflow = 'visible';

        imageContainer.appendChild(container);
    }

    function updateAllVisibleCards() {
        const cards = document.querySelectorAll('.cardBox');
        cards.forEach(updateCard);
    }

    // Monitor for DOM changes (cards and home page navigation)
    const observer = new MutationObserver((mutations) => {
        let hasNewCards = false;
        let homeChanged = false;

        for (const mutation of mutations) {
            for (const node of mutation.addedNodes) {
                if (node.nodeType === Node.ELEMENT_NODE) {
                    if (node.classList && (node.classList.contains('cardBox') || node.classList.contains('card')) ||
                        node.querySelector && node.querySelector('.cardBox')) {
                        hasNewCards = true;
                    }
                    if (node.classList && node.classList.contains('homeSectionsContainer') ||
                        node.querySelector && node.querySelector('.homeSectionsContainer')) {
                        homeChanged = true;
                    }
                }
            }
            if (hasNewCards && homeChanged) break;
        }

        if (homeChanged) {
            updateStorageBar();
        }

        if (hasNewCards) {
            if (Date.now() - lastFetchTime > 10000) {
                fetchWatchedData();
            } else {
                updateAllVisibleCards();
            }
        }
    });

    observer.observe(document.body, { childList: true, subtree: true });

    // Periodic scanner to catch delayed rendering / SPA transitions
    setInterval(() => {
        if (document.hidden) return;
        updateAllVisibleCards();
        updateStorageBar();
    }, 2000);

    // Refresh storage data every 5 minutes in background
    setInterval(() => {
        if (!document.hidden && (!storageData || storageData.enabled !== false)) {
            fetchStorageData(true);
        }
    }, 5 * 60 * 1000);

    window.addEventListener('hashchange', () => {
        setTimeout(updateStorageBar, 200);
    });

    function init() {
        if (window.ApiClient) {
            fetchWatchedData();
            fetchStorageData();
        } else {
            setTimeout(init, 200);
        }
    }
    init();
})();
