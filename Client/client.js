(function () {
    let watchedCache = {};
    let lastFetchTime = 0;
    let isFetching = false;

    // Inject CSS styles for avatars and stacked deck animation
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
     * Looks at child elements for data-id / data-itemid attributes,
     * then falls back to parsing href links.
     */
    function getItemIdFromCardBox(cardBox) {
        // Try data attributes on any descendant
        const dataEl = cardBox.querySelector('[data-id]') || cardBox.querySelector('[data-itemid]');
        if (dataEl) {
            const id = dataEl.getAttribute('data-id') || dataEl.getAttribute('data-itemid');
            if (id) return id;
        }

        // Fallback: parse href from any link inside the card
        const links = cardBox.querySelectorAll('a[href]');
        for (const link of links) {
            const href = link.getAttribute('href');
            if (!href) continue;

            // Try 32-char hex (dashless GUID)
            const hexMatch = href.match(/[?&]id=([0-9a-f]{32})/i);
            if (hexMatch) return hexMatch[1];

            // Try 36-char UUID (dashed GUID)
            const uuidMatch = href.match(/[?&]id=([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/i);
            if (uuidMatch) return uuidMatch[1];

            // Try any 32-char hex in path
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
            
            // Normalize cache keys (strip dashes, lowercase)
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

        // Authenticate the image request by appending the active session token
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

        // Fingerprint: comma-joined user IDs to detect changes
        const fingerprint = users.map(u => String(u.Id || u.id || '')).join(',');

        const existing = cardBox.querySelector('.jellycheck-indicators');
        const currentFingerprint = cardBox.getAttribute('data-jellycheck-fp');

        // Skip if already up-to-date
        if (existing && currentFingerprint === fingerprint) return;

        // Clean up old indicators
        if (existing) existing.remove();

        if (users.length === 0) {
            cardBox.removeAttribute('data-jellycheck-fp');
            return;
        }

        cardBox.setAttribute('data-jellycheck-fp', fingerprint);

        // Create indicators container
        const container = document.createElement('div');
        container.className = 'jellycheck-indicators';

        // Add each user avatar
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

        // Find the poster image area within the card box
        const imageContainer = cardBox.querySelector('.cardImageContainer')
            || cardBox.querySelector('.cardScalable')
            || cardBox;

        // Ensure the target has relative positioning for absolute children
        const computedStyle = window.getComputedStyle(imageContainer);
        if (computedStyle.position === 'static') {
            imageContainer.style.position = 'relative';
        }
        // Prevent clipping of the absolutely-positioned indicators
        imageContainer.style.overflow = 'visible';

        imageContainer.appendChild(container);
    }

    function updateAllVisibleCards() {
        // Select actual card wrapper elements — NOT the <a> text links
        const cards = document.querySelectorAll('.cardBox');
        cards.forEach(updateCard);
    }

    // Monitor for DOM changes (new cards added via SPA navigation or virtual scrolling)
    const observer = new MutationObserver((mutations) => {
        let hasNewCards = false;
        for (const mutation of mutations) {
            for (const node of mutation.addedNodes) {
                if (node.nodeType === Node.ELEMENT_NODE) {
                    if (node.classList && (node.classList.contains('cardBox') || node.classList.contains('card')) ||
                        node.querySelector && node.querySelector('.cardBox')) {
                        hasNewCards = true;
                        break;
                    }
                }
            }
            if (hasNewCards) break;
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

    // Periodic scanner to catch delayed rendering / SPA navigation changes
    // 2 second interval — MutationObserver handles immediate additions
    setInterval(() => {
        if (document.hidden) return;
        updateAllVisibleCards();
    }, 2000);

    // Wait for ApiClient to become available, then fetch data
    function init() {
        if (window.ApiClient) {
            fetchWatchedData();
        } else {
            setTimeout(init, 200);
        }
    }
    init();
})();
