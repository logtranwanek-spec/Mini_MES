/** History is read from the dedicated server database, never IndexedDB. */
const LogManager = {
    async init() {},
    async getLogsByMonth(month = null) {
        return WipApi.request('/logs' + (month ? '?month=' + encodeURIComponent(month) : ''));
    },
    async getMoHistory(moNumber) {
        const logs = await WipApi.request('/logs?mo=' + encodeURIComponent(moNumber));
        const last = logs.at(-1);
        if (!last) return { scanIn: null, scanOut: null, duration: null };
        const visit = logs.filter(log => log.entryId === last.entryId);
        const add = visit.find(log => log.action === 'ADD' || log.action === 'RESTORE');
        const out = visit.find(log => log.action === 'REMOVE');
        return { scanIn: add?.timestamp || null, scanOut: out?.timestamp || null, duration: out?.duration || null };
    },
    async getMoFamilyHistory(baseMO) {
        const logs = await WipApi.request('/logs?mo=' + encodeURIComponent(baseMO) + '&family=true');
        const latestEntryByMO = new Map();
        for (const log of logs) latestEntryByMO.set(log.moNumber, log.entryId);
        return [...latestEntryByMO.entries()].map(([moNumber, entryId]) => {
            const visit = logs.filter(log => log.entryId === entryId);
            const add = visit.find(log => log.action === 'ADD' || log.action === 'RESTORE' || log.action === 'IMPORT');
            const out = visit.find(log => log.action === 'REMOVE');
            return { moNumber, shelfCode: add?.shelfCode || out?.shelfCode || '', scanIn: add?.timestamp || null,
                scanOut: out?.timestamp || null, duration: out?.duration || null };
        }).sort((a, b) => a.moNumber.localeCompare(b.moNumber, undefined, { numeric: true }));
    },
    async getCompleteHistory() {
        const logs = await this.getLogsByMonth();
        const visits = new Map();
        for (const log of logs) {
            const item = visits.get(log.entryId) || { moNumber: log.moNumber, scanIn: null, scanOut: null };
            item.shelfCode = log.shelfCode;
            if (log.action === 'ADD' || log.action === 'RESTORE') item.scanIn = log.timestamp;
            if (log.action === 'IMPORT') item.importedAt = log.timestamp;
            if (log.action === 'REMOVE') { item.scanOut = log.timestamp; item.duration = log.duration; }
            visits.set(log.entryId, item);
        }
        return [...visits.values()].reverse();
    },
    async showExportDialog() {
        const month = prompt('Nhập tháng YYYY-MM để xuất log, hoặc để trống để xuất TOÀN BỘ lịch sử:', '');
        if (month === null) return;
        if (month.trim() && !/^\d{4}-(0[1-9]|1[0-2])$/.test(month.trim())) {
            WipApi.status('Tháng không hợp lệ. Nhập dạng YYYY-MM.', true); return;
        }
        try {
            const response = await fetch(WipApi.base + '/export' + (month.trim() ? '?month=' + encodeURIComponent(month.trim()) : ''), { cache: 'no-store' });
            if (!response.ok) throw new Error('Không xuất được log từ máy chủ. Vui lòng thử lại.');
            const url = URL.createObjectURL(await response.blob());
            const link = document.createElement('a');
            link.href = url; link.download = `WipB3L2-${month.trim() || 'all'}.xlsx`; link.click();
            setTimeout(() => URL.revokeObjectURL(url), 60000);
        } catch (error) { WipApi.status(error.message, true); }
    },
async getDashboardData() {
        const dashboardData = {};

        // 1. Lấy tất cả các vị trí kệ
        const allShelves = ShelfLocations.getAllGrouped();
        for (const line in allShelves) {
            allShelves[line].forEach(shelf => {
                dashboardData[shelf.code] = { currentCards: [], history: [] };
            });
        }

        // 2. Lấy thông tin các MO đang trong kho
        const currentCards = WIPManager.getAll();
        currentCards.forEach(card => {
            if (dashboardData[card.shelfCode]) {
                card.moNumbers.forEach(mo => {
                    dashboardData[card.shelfCode].currentCards.push({
                        moNumber: mo,
                        scanIn: card.moCreatedAt?.[mo] || card.createdAt
                    });
                 });
            }
        });

        // 3. Lấy toàn bộ lịch sử từ log
        const completeHistory = await this.getCompleteHistory();
        completeHistory.forEach(item => {
            if (dashboardData[item.shelfCode]) {
                dashboardData[item.shelfCode].history.push(item);
            }
        });

        return dashboardData;
    }
};
