let dashboardData = {};
let activeShelfCode = null;
let dashboardRefreshing = false;
let dashboardRefreshAgain = false;
let undoInfo = null;

async function refreshDashboard() {
    if (dashboardRefreshing) { dashboardRefreshAgain = true; return; }
    dashboardRefreshing = true;
    try {
        do {
            dashboardRefreshAgain = false;
            dashboardData = await LogManager.getDashboardData();
            await loadHourlyStats(document.getElementById('hourly-date')?.value);
            renderHourlyChart(); renderDashboardGrid();
            await updateUndoUI();
            if (activeShelfCode) openShelfModal(activeShelfCode);
        } while (dashboardRefreshAgain);
    } catch (error) { WipApi.status('Không tải được dashboard: ' + error.message, true); }
    finally { dashboardRefreshing = false; }
}

async function updateUndoUI() {
    undoInfo = await WipApi.request('/undo');
    const box = document.getElementById('undo-info'), button = document.getElementById('undo-clear-btn');
    if (!box || !button) return;
    box.textContent = undoInfo
        ? `Có thể hoàn tác line ${undoInfo.line}: ${undoInfo.totalMOs} MO tại ${undoInfo.totalCards} vị trí — ${new Date(undoInfo.savedAt).toLocaleString('vi-VN')}`
        : 'Chưa có giao dịch xuất line cần hoàn tác.';
    button.disabled = !undoInfo;
}

async function clearEntireLine(line) {
    if (!await askManagerPassword(line)) return;
    try {
        await WipApi.refresh();
        const cards = WIPManager.getByLine(line);
        const items = cards.flatMap(card => card.moNumbers.map(moNumber => ({ cardId: card.id, moNumber, entryId: card.moEntryIds[moNumber] })));
        if (!items.length) { alert(`Line ${line} không có MO.`); return; }
        if (!confirm(`Xuất toàn bộ ${items.length} MO tại ${cards.length} vị trí của line ${line}?\nMỗi MO sẽ có log OUT. Có thể hoàn tác nếu không xung đột với giao dịch mới.`)) return;
        const result = await WipApi.command('clear-line', { line, items });
        if (!result) return;
        await refreshDashboard();
        alert(`Đã xuất ${items.length} MO khỏi line ${line}. Có thể hoàn tác ở cuối trang.`);
    } catch (error) { WipApi.status(error.message, true); }
}

async function undoClearLine() {
    try {
        await updateUndoUI();
        const snapshot = undoInfo;
        if (!snapshot) return;
        if (!await askManagerPassword(`hoàn tác line ${snapshot.line}`)) return;
        if (!confirm(`Khôi phục ${snapshot.totalMOs} MO về vị trí trước khi xuất line ${snapshot.line}?`)) return;
        if (!await WipApi.command('undo-line', { undoRequestId: snapshot.requestId })) return;
        await refreshDashboard();
        alert('Đã hoàn tác. Lịch sử xuất và khôi phục vẫn được giữ trên server.');
    } catch (error) { WipApi.status(error.message, true); }
}

function setupManagerPanel() {
    document.getElementById('undo-clear-btn')?.addEventListener('click', undoClearLine);
    document.getElementById('change-pw-btn')?.addEventListener('click', async () => {
        if (!await askManagerPassword('đổi mật khẩu')) return;
        const password = prompt('Nhập mật khẩu mới (2–256 ký tự):');
        if (!password) return;
        if (prompt('Nhập lại mật khẩu mới:') !== password) { alert('Hai lần nhập không khớp.'); return; }
        try {
            await WipApi.request('/manager/password', { method: 'POST', body: JSON.stringify({ password }) });
            WipApi.managerToken = null; sessionStorage.removeItem('wip-b3-l2.manager-token');
            alert('Đã đổi mật khẩu chung trên server. Các phiên manager cũ đã hết hiệu lực.');
        } catch (error) { WipApi.status(error.message, true); }
    });
}
let hourlyStats = { in: Array(24).fill(0), out: Array(24).fill(0) };

document.addEventListener('DOMContentLoaded', async () => {
    await WIPManager.init();
    await LogManager.init();

    await refreshDashboard();

    // Mặc định chọn hôm nay
    const dateInput = document.getElementById('hourly-date');
    if (dateInput) {
        const today = new Date();
        const yyyy = today.getFullYear();
        const mm = String(today.getMonth() + 1).padStart(2, '0');
        const dd = String(today.getDate()).padStart(2, '0');
        dateInput.value = `${yyyy}-${mm}-${dd}`;
        dateInput.addEventListener('change', async () => {
            await loadHourlyStats(dateInput.value);
            renderHourlyChart();
            updateHourlyTitle(dateInput.value);
        });
    }

    await loadHourlyStats(dateInput ? dateInput.value : null);
    renderHourlyChart();
    updateHourlyTitle(dateInput ? dateInput.value : null);
    renderDashboardGrid();
    setupSearch();
    setupManagerPanel();
    document.addEventListener('wip-state-changed', refreshDashboard);
});

function updateHourlyTitle(dateStr) {
    const el = document.getElementById('hourly-title-text');
    if (!el) return;
    if (!dateStr) {
        el.textContent = 'Tần suất vào / ra theo giờ';
        return;
    }
    const d = new Date(dateStr + 'T00:00:00');
    const today = new Date();
    const isToday = d.toDateString() === today.toDateString();
    if (isToday) {
        el.textContent = 'Tần suất vào / ra theo giờ (hôm nay)';
    } else {
        el.textContent = 'Tần suất vào / ra theo giờ (' + d.toLocaleDateString('vi-VN') + ')';
    }
}

async function loadHourlyStats(dateStr) {
    hourlyStats = { in: Array(24).fill(0), out: Array(24).fill(0) };
    try {
        const history = await LogManager.getCompleteHistory();

        // dateStr dạng YYYY-MM-DD; nếu không có thì dùng hôm nay
        let targetKey;
        if (dateStr) {
            targetKey = new Date(dateStr + 'T00:00:00').toDateString();
        } else {
            targetKey = new Date().toDateString();
        }

        history.forEach(item => {
            if (item.scanIn) {
                const d = new Date(item.scanIn);
                if (d.toDateString() === targetKey) {
                    hourlyStats.in[d.getHours()]++;
                }
            }
            if (item.scanOut) {
                const d = new Date(item.scanOut);
                if (d.toDateString() === targetKey) {
                    hourlyStats.out[d.getHours()]++;
                }
            }
        });
    } catch (e) {
        console.warn('Không tải được thống kê theo giờ:', e);
    }
}

function renderHourlyChart() {
    const container = document.getElementById('hourly-chart');
    if (!container) return;

    const maxVal = Math.max(1, ...hourlyStats.in, ...hourlyStats.out);
    let html = '';

    for (let h = 0; h < 24; h++) {
        const inH = Math.round((hourlyStats.in[h] / maxVal) * 100);
        const outH = Math.round((hourlyStats.out[h] / maxVal) * 100);
        const label = String(h).padStart(2, '0');
        const title = `${label}:00 — Vào: ${hourlyStats.in[h]}, Ra: ${hourlyStats.out[h]}`;

        html += `
            <div class="hour-col" title="${title}">
                <div class="hour-bars">
                    <div class="bar bar-in" style="height: ${Math.max(inH, hourlyStats.in[h] > 0 ? 4 : 0)}%"></div>
                    <div class="bar bar-out" style="height: ${Math.max(outH, hourlyStats.out[h] > 0 ? 4 : 0)}%"></div>
                </div>
                <div class="hour-label">${h % 2 === 0 ? label : ''}</div>
            </div>
        `;
    }
    container.innerHTML = html;
}

function renderDashboardGrid() {
    const grid = document.getElementById('dashboard-grid');
    if (!grid) return;

    const allShelves = ShelfLocations.getAllGrouped();
    const usableLines = (typeof ShelfLocations !== 'undefined' && ShelfLocations.usableLines)
        ? ShelfLocations.usableLines
        : 'ABCDEFGHIJKLMNOP'.split('');
    const visibleLines = [...new Set([...usableLines, ...WIPManager.cards.map(c => c.shelfCode[0])])];

    let html = '';

    for (const line of visibleLines) {
        if (!allShelves[line] || allShelves[line].length === 0) continue;

        html += '<div class="line-group" id="line-group-' + line + '">';
        html += '<div class="line-section-title" style="display:flex;align-items:center;justify-content:space-between;gap:0.75rem;flex-wrap:wrap;">';
        html += '<span>Line ' + line + '</span>';
        if (usableLines.includes(line)) {
        html += '<button type="button" class="line-clear-btn" onclick="event.stopPropagation();clearEntireLine(\'' + line + '\')" ';
        html += 'title="Xuất toàn bộ MO trên line ' + line + '">Xuất hàng loạt line ' + line + '</button>';
        }
        html += '</div>';
        html += '<div class="line-cards">';

        allShelves[line].forEach(shelf => {
            const shelfCode = shelf.code;
            const data = dashboardData[shelfCode] || { currentCards: [], history: [] };
            const currentCards = data.currentCards || [];
            let isOccupied = currentCards.length > 0;

            let moList = [];
            currentCards.forEach(c => {
                if (c.moNumbers && Array.isArray(c.moNumbers)) {
                    moList = moList.concat(c.moNumbers);
                } else if (c.moNumber) {
                    moList.push(c.moNumber);
                }
            });
            if (moList.length === 0 && typeof WIPManager !== 'undefined') {
                const live = WIPManager.getByShelf(shelfCode) || [];
                live.forEach(c => {
                    if (c.moNumbers) moList = moList.concat(c.moNumbers);
                    else if (c.moNumber) moList.push(c.moNumber);
                });
                if (live.length > 0) isOccupied = true;
            }
            moList = [...new Set(moList)];

            const moText = moList.length > 0
                ? moList.slice(0, 3).join(', ') + (moList.length > 3 ? ' +' + (moList.length - 3) : '')
                : '—';

            const statusText = isOccupied ? (moList.length + ' MO') : 'Trống';

            html += '<div class="dash-card ' + (isOccupied ? 'occupied' : 'empty') + '"';
            html += ' id="card-' + shelfCode + '"';
            html += ' data-shelf="' + shelfCode + '"';
            html += ' onclick="openShelfModal(\'' + shelfCode + '\')"';
            html += ' title="' + shelfCode + (moList.length ? ' — ' + moList.join(', ') : '') + '">';
            if (isOccupied) {
                html += '<span class="dash-card-count">' + moList.length + '</span>';
            }
            html += '<div>';
            html += '<div class="dash-card-code">' + shelfCode + '</div>';
            html += '<div class="dash-card-sub">' + (isOccupied ? moText : 'Chưa có MO') + '</div>';
            html += '</div>';
            html += '<div class="dash-card-status">';
            html += '<span class="dot"></span>';
            html += '<span>' + statusText + '</span>';
            html += '</div>';
            html += '</div>';
        });

        html += '</div></div>';
    }

    grid.innerHTML = html || '<p class="text-center text-sm py-12" style="color: var(--olive-400)">Không có vị trí nào</p>';
}

function setupSearch() {
    const input = document.getElementById('dashboard-search');
    const hint = document.getElementById('search-result-hint');
    if (!input) return;

    const normalize = (s) => {
        s = (s || '').toUpperCase().trim().replace(/\s+/g, '');
        // G66 -> G-66, C12 -> C-12
        const m = s.match(/^([A-P])-?(\d{1,2})$/i);
        if (m) {
            return m[1] + '-' + m[2].padStart(2, '0');
        }
        return s;
    };

    input.addEventListener('input', () => {
        const raw = input.value.trim();
        document.querySelectorAll('.dash-card.highlight').forEach(el => el.classList.remove('highlight'));
        if (!raw) {
            if (hint) hint.textContent = '';
            return;
        }

        const code = normalize(raw);
        const card = document.getElementById('card-' + code);

        if (card) {
            card.classList.add('highlight');
            card.scrollIntoView({ behavior: 'smooth', block: 'center' });
            if (hint) hint.textContent = '→ ' + code;
        } else {
            // simpler: match by starts with line or full
            const allCards = document.querySelectorAll('.dash-card');
            let hit = null;
            allCards.forEach(el => {
                const sc = el.dataset.shelf || '';
                if (sc === code || sc.replace('-', '') === code.replace('-', '')) hit = el;
            });
            if (hit) {
                hit.classList.add('highlight');
                hit.scrollIntoView({ behavior: 'smooth', block: 'center' });
                if (hint) hint.textContent = '→ ' + hit.dataset.shelf;
            } else if (hint) {
                hint.textContent = 'Không tìm thấy "' + raw + '"';
            }
        }
    });

    input.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
            const highlighted = document.querySelector('.dash-card.highlight');
            if (highlighted && highlighted.dataset.shelf) {
                openShelfModal(highlighted.dataset.shelf);
            }
        }
    });
}


function askManagerPassword(line) {
    return new Promise((resolve) => {
        // Xóa modal cũ nếu còn
        const old = document.getElementById('manager-pw-modal');
        if (old) old.remove();

        const overlay = document.createElement('div');
        overlay.id = 'manager-pw-modal';
        overlay.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,0.45);z-index:99999;display:flex;align-items:center;justify-content:center;';

        overlay.innerHTML = `
            <div style="background:white;border-radius:1rem;padding:1.5rem 1.75rem;width:90%;max-width:360px;box-shadow:0 20px 50px rgba(0,0,0,0.25);">
                <h3 style="margin:0 0 0.35rem;font-size:1.05rem;font-weight:600;color:#0c0c09;">Xác thực manager</h3>
                <p style="margin:0 0 1rem;font-size:0.85rem;color:#6b6b5c;">Nhập mật khẩu manager <strong>(${line})</strong></p>
                <input id="manager-pw-input" type="password" autocomplete="current-password"
                    placeholder="Mật khẩu manager"
                    style="width:100%;padding:0.6rem 0.75rem;border:1px solid rgba(12,12,9,0.15);border-radius:0.6rem;font-size:0.9rem;outline:none;box-sizing:border-box;" />
                <p id="manager-pw-error" style="display:none;margin:0.5rem 0 0;font-size:0.8rem;color:#b85a5a;"></p>
                <div style="display:flex;gap:0.5rem;margin-top:1.1rem;justify-content:flex-end;">
                    <button type="button" id="manager-pw-cancel"
                        style="padding:0.5rem 0.9rem;border-radius:0.5rem;border:1px solid rgba(12,12,9,0.12);background:#f5f5f0;cursor:pointer;font-size:0.85rem;">Hủy</button>
                    <button type="button" id="manager-pw-ok"
                        style="padding:0.5rem 0.9rem;border-radius:0.5rem;border:none;background:linear-gradient(135deg,#b85a5a,#d47878);color:white;cursor:pointer;font-size:0.85rem;font-weight:600;">Xác nhận</button>
                </div>
            </div>
        `;

        document.body.appendChild(overlay);

        const input = document.getElementById('manager-pw-input');
        const err = document.getElementById('manager-pw-error');
        const close = (value) => {
            overlay.remove();
            resolve(value);
        };

        document.getElementById('manager-pw-cancel').onclick = () => close(null);
        overlay.addEventListener('click', (e) => {
            if (e.target === overlay) close(null);
        });

        const submit = async () => {
            const button = document.getElementById('manager-pw-ok');
            if (button.disabled) return;
            button.disabled = true;
            try {
                const session = await WipApi.request('/manager/login', { method: 'POST', body: JSON.stringify({ password: input.value }) });
                WipApi.managerToken = session.token;
                sessionStorage.setItem('wip-b3-l2.manager-token', session.token);
                close(true);
            } catch (error) {
                err.style.display = 'block'; err.textContent = error.message; input.value = ''; input.focus();
            } finally { button.disabled = false; }
        };

        document.getElementById('manager-pw-ok').onclick = submit;
        input.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') submit();
            if (e.key === 'Escape') close(null);
        });

        setTimeout(() => input.focus(), 50);
    });
}

function openShelfModal(shelfCode) {
    activeShelfCode = shelfCode;
    const modal = document.getElementById('shelf-detail-modal');
    const data = dashboardData[shelfCode] || { currentCards: [], history: [] };

    document.getElementById('modal-shelf-code').textContent = shelfCode;

    const currentStatusEl = document.getElementById('modal-current-status');
    let liveCards = [];
    if (typeof WIPManager !== 'undefined') {
        liveCards = WIPManager.getByShelf(shelfCode) || [];
    }
    const cardsToShow = liveCards.length > 0 ? liveCards : (data.currentCards || []);

    if (cardsToShow.length > 0) {
        currentStatusEl.innerHTML = cardsToShow.map(card => {
            const mos = card.moNumbers || (card.moNumber ? [card.moNumber] : []);
            const time = card.createdAt
                ? new Date(card.createdAt).toLocaleString('vi-VN')
                : (card.scanIn ? new Date(card.scanIn).toLocaleString('vi-VN') : '—');
            return '<div class="mb-2 last:mb-0" style="color: var(--olive-800)">' +
                '<span class="font-semibold">' + mos.join(', ') + '</span>' +
                '<span class="text-xs ml-2" style="color: var(--olive-500)">In: ' + time + '</span>' +
                '</div>';
        }).join('');
    } else {
        currentStatusEl.innerHTML = '<div class="text-center text-sm" style="color: var(--olive-400)">Vị trí này đang trống</div>';
    }

    const historyBody = document.getElementById('modal-history-body');
    const history = (data.history || []).slice(0, 10);
    if (history.length > 0) {
        historyBody.innerHTML = history.map(h =>
            '<tr class="border-b" style="border-color: rgba(12,12,9,0.06)">' +
            '<td class="px-4 py-2 font-medium">' + (h.moNumber || '—') + '</td>' +
            '<td class="px-4 py-2">' + (h.scanIn ? new Date(h.scanIn).toLocaleString('vi-VN') : '—') + '</td>' +
            '<td class="px-4 py-2">' + (h.scanOut ? new Date(h.scanOut).toLocaleString('vi-VN') : '—') + '</td>' +
            '<td class="px-4 py-2">' + (h.duration || '—') + '</td>' +
            '</tr>'
        ).join('');
    } else {
        historyBody.innerHTML = '<tr><td colspan="4" class="px-4 py-6 text-center text-sm" style="color: var(--olive-400)">Chưa có lịch sử</td></tr>';
    }

    modal.classList.remove('hidden');
}

function closeShelfModal() {
    activeShelfCode = null;
    const modal = document.getElementById('shelf-detail-modal');
    if (modal) modal.classList.add('hidden');
}

document.addEventListener('click', (e) => {
    const modal = document.getElementById('shelf-detail-modal');
    if (modal && e.target === modal) {
        closeShelfModal();
    }
});
