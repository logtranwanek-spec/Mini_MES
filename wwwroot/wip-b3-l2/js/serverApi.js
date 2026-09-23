/** All warehouse writes are commands; browser storage holds only an unconfirmed request ID/payload. */
const WipApi = {
    base: '/api/wip-b3-l2',
    pendingKey: 'wip-b3-l2.pending-command.v1',
    ready: false,
    busy: false,
    refreshing: false,
    queue: Promise.resolve(),
    managerToken: sessionStorage.getItem('wip-b3-l2.manager-token'),

    status(message, error = false) {
        let banner = document.getElementById('server-status');
        if (!banner) {
            banner = document.createElement('div');
            banner.id = 'server-status';
            banner.setAttribute('role', 'status');
            banner.style.cssText = 'position:fixed;bottom:12px;left:12px;z-index:10000;padding:10px 16px;border-radius:8px;max-width:650px;font:14px system-ui;box-shadow:0 2px 10px #0005';
            document.body.appendChild(banner);
        }
        banner.style.background = error ? '#7f1d1d' : '#14532d';
        banner.style.color = 'white';
        banner.replaceChildren(document.createTextNode(message));
        if (sessionStorage.getItem(this.pendingKey)) {
            const button = document.createElement('button');
            button.textContent = 'Kiểm tra / gửi lại';
            button.style.cssText = 'margin-left:12px;text-decoration:underline';
            button.onclick = () => this.retryPending();
            banner.appendChild(button);
        }
    },

    uuid() {
        const bytes = crypto.getRandomValues(new Uint8Array(16));
        bytes[6] = (bytes[6] & 15) | 64; bytes[8] = (bytes[8] & 63) | 128;
        const hex = Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
        return `${hex.slice(0,8)}-${hex.slice(8,12)}-${hex.slice(12,16)}-${hex.slice(16,20)}-${hex.slice(20)}`;
    },

    async request(path, options = {}) {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), 15000);
        try {
            const response = await fetch(this.base + path, {
                ...options, cache: 'no-store', signal: controller.signal,
                headers: { 'Content-Type': 'application/json', ...(this.managerToken ? { Authorization: 'Bearer ' + this.managerToken } : {}), ...options.headers }
            });
            if (!response.ok) {
                const data = await response.json().catch(() => ({}));
                const error = new Error(data.error || `Lỗi máy chủ (${response.status})`);
                error.definitive = [400, 401, 403, 409].includes(response.status);
                error.status = response.status;
                throw error;
            }
            // Some valid endpoints return an empty body for an absent optional value (for example /undo).
            const body = await response.text();
            return body.trim() ? JSON.parse(body) : null;
        } finally { clearTimeout(timer); }
    },

    async refresh() {
        if (this.refreshing) return;
        this.refreshing = true;
        try {
            const latest = await this.request('/revision');
            if (!latest || latest.revision !== WIPManager.revision) {
                const snapshot = await this.request('/state');
                WIPManager.applySnapshot(snapshot);
            }
            this.ready = true;
            if (!this.busy) this.status(sessionStorage.getItem(this.pendingKey)
                ? 'Có giao dịch chưa xác nhận. Kiểm tra trước khi scan tiếp.'
                : 'Kho B3 · L2 — Đã kết nối máy chủ', !!sessionStorage.getItem(this.pendingKey));
        } catch (error) {
            this.ready = false;
            this.status('Mất kết nối máy chủ. Dữ liệu trên màn hình có thể đã cũ; chưa thể xác nhận scan.', true);
            throw error;
        } finally { this.refreshing = false; }
    },

    async init() {
        // Keep reconnecting even if the first request fails.
        if (!this.timer) {
            this.timer = setInterval(() => { if (!document.hidden) this.refresh().catch(() => {}); }, 10000);
            document.addEventListener('visibilitychange', () => {
                if (!document.hidden) this.refresh().catch(() => {});
            });
        }
        await this.refresh().catch(() => {});
    },

    command(action, values = {}) {
        const work = this.queue.then(async () => {
            if (sessionStorage.getItem(this.pendingKey)) {
                this.status('Giao dịch trước chưa được xác nhận. Bấm kiểm tra / gửi lại trước khi scan tiếp.', true);
                return null;
            }
            const command = { requestId: this.uuid(), action, ...values };
            // Persist before sending. Reloading the page cannot create a new ID for an uncertain operation.
            sessionStorage.setItem(this.pendingKey, JSON.stringify(command));
            return this.send(command);
        });
        // Keep the serialized queue usable, but let the caller receive the actual server error.
        this.queue = work.catch(error => { this.status(error.message, true); });
        return work;
    },

    async send(command) {
        this.busy = true;
        this.status('Đang xác nhận giao dịch với máy chủ…');
        try {
            let result;
            for (let attempt = 0; attempt < 2; attempt++) {
                try {
                    result = await this.request('/commands', { method: 'POST', body: JSON.stringify(command) });
                    break;
                } catch (error) { if (error.definitive || attempt === 1) throw error; }
            }
            sessionStorage.removeItem(this.pendingKey);
            WIPManager.applySnapshot(result);
            this.ready = true;
            this.status('Đã lưu tồn kho và lịch sử trên máy chủ.');
            return result;
        } catch (error) {
            if (error.status === 401 || error.status === 403) {
                // Keep the same request ID: a previous attempt could already have committed.
                this.managerToken = null; sessionStorage.removeItem('wip-b3-l2.manager-token');
                this.status('Phiên manager đã hết hạn. Bấm kiểm tra / gửi lại để xác thực lại.', true);
            } else if (error.definitive) {
                sessionStorage.removeItem(this.pendingKey);
                await this.refresh().catch(() => {});
                this.status(error.message, true);
            } else {
                this.status('Chưa xác nhận được giao dịch. Không scan lại MO; bấm kiểm tra / gửi lại để tránh trùng.', true);
            }
            throw error;
        } finally { this.busy = false; }
    },

    async retryPending() {
        if (this.busy) return;
        const value = sessionStorage.getItem(this.pendingKey);
        if (value) {
            const command = JSON.parse(value);
            if (['clear-line', 'undo-line'].includes(command.action) && typeof askManagerPassword === 'function') {
                if (!await askManagerPassword('gửi lại giao dịch')) return;
            }
            await this.send(command);
        }
    }
};

Object.assign(WIPManager, {
    applySnapshot(snapshot) {
        if (snapshot.revision < this.revision) return;
        const changed = snapshot.revision !== this.revision;
        const previousRevision = this.revision;
        const previousByShelf = new Map();
        this.cards.forEach(card => {
            const value = previousByShelf.get(card.shelfCode) || [];
            value.push(`${card.id}:${(card.moNumbers || []).join(',')}:${card.createdAt}`);
            previousByShelf.set(card.shelfCode, value);
        });
        this.revision = snapshot.revision;
        this.cards = snapshot.cards.map(card => ({ ...card,
            shelfName: ShelfLocations.getName(card.shelfCode),
            areaColor: ShelfLocations.getAreaColor(card.shelfCode),
            timestamp: new Date(card.createdAt).toLocaleString('vi-VN')
        }));
        this.cardsByShelf = new Map(); this.cardByMO = new Map(); this.cardById = new Map();
        const currentByShelf = new Map();
        this.cards.forEach(card => {
            this.cardById.set(card.id, card);
            if (!this.cardsByShelf.has(card.shelfCode)) this.cardsByShelf.set(card.shelfCode, []);
            this.cardsByShelf.get(card.shelfCode).push(card);
            (card.moNumbers || []).forEach(mo => this.cardByMO.set(String(mo).toUpperCase().trim(), card));
            const value = currentByShelf.get(card.shelfCode) || [];
            value.push(`${card.id}:${(card.moNumbers || []).join(',')}:${card.createdAt}`);
            currentByShelf.set(card.shelfCode, value);
        });
        if (changed) {
            const changedShelves = new Set([...previousByShelf.keys(), ...currentByShelf.keys()]);
            for (const shelf of [...changedShelves]) {
                const before = (previousByShelf.get(shelf) || []).sort().join('|');
                const after = (currentByShelf.get(shelf) || []).sort().join('|');
                if (before === after) changedShelves.delete(shelf);
            }
            document.dispatchEvent(new CustomEvent('wip-state-changed', {
                detail: { changedShelves: [...changedShelves], fullRender: previousRevision < 0 }
            }));
        }
    },
    async init() { await WipApi.init(); },
    async createCard(shelfCode, moNumber, productType = UIController.selectedProductType || 'MO') {
        await this.ensureProductZone(productType);
        const result = await WipApi.command('add', { shelfCode, moNumber, productType });
        return result ? this.cards.find(c => result.affectedCardIds.includes(c.id)) || null : null;
    },
    async ensureProductZone(productType) {
        if (productType !== 'MO') {
            const capability = await fetch('/api/wip-b3-l2/product-zones', { cache: 'no-store' });
            if (!capability.ok) throw new Error('Cần cập nhật máy chủ để nhập hàng theo khu. Chưa nhập hàng.');
            const zones = await capability.json();
            if (!(zones[productType] || zones[productType.toLowerCase()])) throw new Error('Máy chủ chưa hỗ trợ khu đã chọn.');
        }
    },
    async createMultiVehicleCards(moNumber, vehicleCount = 1, productType = 'MO') {
        await this.ensureProductZone(productType);
        const result = await WipApi.command('add-multiple', { moNumber, vehicleCount, productType });
        return result ? this.cards.filter(c => result.affectedCardIds.includes(c.id)) : [];
    },
    async addMOToCard(cardId, moNumber) {
        const card = this.cards.find(c => c.id === cardId);
        return card ? !!await this.createCard(card.shelfCode, moNumber) : false;
    },
    async removeMOFromCard(cardId, moNumber, entryId) {
        return !!await WipApi.command('remove', { cardId, moNumber, entryId });
    },
    async removeMOs(items) { return !!await WipApi.command('remove-batch', { items }); },
    async removeCard(cardId) {
        const card = this.cards.find(c => c.id === cardId);
        return card ? this.removeMOs(card.moNumbers.map(moNumber => ({ cardId, moNumber, entryId: card.moEntryIds[moNumber] }))) : false;
    },
    async moveCard(cardId, shelfCode) { return !!await WipApi.command('move', { cardId, shelfCode }); },
    async clearAll() {
        if (!this.cards.length || !confirm('Xuất toàn bộ MO đang hiển thị khỏi kho? Mỗi MO sẽ có log OUT.')) return false;
        return this.removeMOs(this.cards.flatMap(card => card.moNumbers.map(moNumber => ({ cardId: card.id, moNumber, entryId: card.moEntryIds[moNumber] }))));
    },
    async importFromJSON(text) {
        const data = JSON.parse(text);
        const cards = Array.isArray(data) ? data : data.cards;
        if (!Array.isArray(cards) || !cards.length) throw new Error('File does not contain inventory cards.');
        return !!await WipApi.command('import', { cards: cards.map(card => ({
            shelfCode: card.shelfCode, moNumbers: card.moNumbers || [card.moNumber], createdAt: card.createdAt
        })) });
    }
});

// Updating a view never writes a snapshot back to the server.
document.addEventListener('wip-state-changed', (event) => {
    if (typeof UIController !== 'undefined' && UIController.serverReady) {
        const changedShelves = event.detail?.changedShelves || [];
        if (event.detail?.fullRender || !document.querySelector('.warehouse-slot')) UIController.render();
        else UIController.updateSpecificSlots(changedShelves);
        if (typeof ExcelParser !== 'undefined' && ExcelParser.scheduledMOs) UIController.updateMODashboard();
        UIController.heroPanelCards = UIController.heroPanelCards.flatMap(previous => {
            const current = WIPManager.cards.find(card => card.id === (previous.originalCardId || previous.id));
            if (!current) return [];
            const moNumbers = previous.moNumbers.filter(mo =>
                current.moEntryIds[mo] && current.moEntryIds[mo] === previous.moEntryIds[mo]);
            return moNumbers.length ? [{ ...current, id: previous.id, moNumbers,
                isVirtual: previous.isVirtual, originalCardId: previous.originalCardId }] : [];
        });
        UIController.selectedCardIds = UIController.selectedCardIds.filter(id => UIController.heroPanelCards.some(card => card.id === id));
        if (UIController.heroPanelCards.length) UIController.updateHeroPanelDisplay();
        else UIController.clearHeroPanel();
        if (UIController.currentShelfCode) {
            UIController.renderShelfCardMOList(WIPManager.getByShelf(UIController.currentShelfCode));
        }
    }
});

async function importLegacyWip(event) {
    const file = event.target.files[0];
    if (!file) return;
    try {
        if (file.size > 5 * 1024 * 1024) throw new Error('File quá lớn (tối đa 5 MB).');
        if (!confirm('Chuyển tồn kho từ file vào kho B3 L2 mới? Chỉ áp dụng khi chưa có giao dịch. Lịch sử scan cũ không nằm trong file tồn kho sẽ không được tạo lại.')) return;
        if (await WIPManager.importFromJSON(await file.text())) WipApi.status('Đã chuyển tồn kho cũ lên máy chủ; đã ghi log IMPORT.');
    } catch (error) { WipApi.status(error.message, true); }
    finally { event.target.value = ''; }
}
