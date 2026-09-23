(() => {
    'use strict';
    const $ = id => document.getElementById(id);
    const state = { orders: [], file: '', search: '', selectedId: null, generation: 0,
        orderAbort: null, locationAbort: null, loading: false, legacyBackend: false };
    const escape = value => String(value ?? '').replace(/[&<>"']/g,
        char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]);
    const category = status => ({ pending: 'Pending', received: 'Received', lack: 'Lack' })[String(status).toLowerCase()] || 'Other';
    const label = status => ({ Pending: '⏳ Chưa nhận', Received: '✅ Đã nhận', Lack: '❌ Nhận thiếu' })[category(status)] || `⚠️ ${status || 'Chưa xác định'}`;
    const leadtimeStart = value => {
        const match = String(value ?? '').match(/(\d{1,2}):(\d{2})/);
        return match ? Number(match[1]) * 60 + Number(match[2]) : 1440;
    };
    const today = new Date();
    $('deliveryDate').value = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`;

    async function request(path, signal) {
        const response = await fetch('/api/wip-b3-l2/delivery/' + path, { cache: 'no-store', signal });
        if (response.status === 404 && path.startsWith('orders?')) {
            state.legacyBackend = true;
            const params = new URLSearchParams(path.split('?')[1]);
            const [, month, day] = params.get('date').split('-');
            const files = params.get('fileType') ? [params.get('fileType')] : ['Console Lid', 'Other'];
            const lists = await Promise.all(files.map(async fileType => {
                const query = new URLSearchParams({ date: `${day}.${month}`, fileType });
                const res = await fetch('/orders?' + query, { cache: 'no-store', signal });
                if (!res.ok) throw new Error('Không tải được danh sách bên nhận.');
                return (await res.json()).map(order => ({ ...order, fItem: order.fitem,
                    fileType, id: `${fileType}:${order.odrno}` }));
            }));
            return lists.flat();
        }
        if (!response.ok) throw new Error(`Không tải được dữ liệu (${response.status}). Hãy thử tải lại.`);
        if (path.startsWith('orders?')) state.legacyBackend = false;
        return response.json();
    }
    async function legacyLocations(id, signal) {
        const order = state.orders.find(item => String(item.id) === String(id));
        if (!order) throw new Error('Phiếu không còn trong danh sách.');
        const response = await fetch('/api/wip-b3-l2/state', { cache: 'no-store', signal });
        if (!response.ok) throw new Error('Không tải được tồn kho.');
        const snapshot = await response.json();
        const target = String(order.mw || '').trim().toUpperCase();
        const locations = snapshot.cards.flatMap(card => card.moNumbers.filter(mo => {
            const code = String(mo).trim().toUpperCase();
            return target && (code === target || code.replace(/-XE[1-9]\d*$/, '') === target);
        }).map(moNumber => ({ moNumber, shelfCode: card.shelfCode,
            area: /^[C-G]-/.test(card.shelfCode) ? 'Kit' : ({ I: 'Cushion', K: 'Cushion', A: 'Fiber', B: 'Fiber', M: 'Decking', N: 'Decking' })[card.shelfCode[0]] || 'Chưa phân khu' })));
        locations.sort((a, b) => a.shelfCode.localeCompare(b.shelfCode));
        return { ...order, locations };
    }
    function connection(message, error = false) {
        $('deliveryConnection').textContent = message;
        $('deliveryConnection').classList.toggle('delivery-error', error);
    }
    function visibleOrders() {
        const allowed = new Set([...document.querySelectorAll('.delivery-filters input:checked')].map(input => input.value));
        return state.orders.filter(order => allowed.has(category(order.status)) &&
            [order.odrno, order.mw, order.fItem, order.note].some(value => String(value ?? '').toLowerCase().includes(state.search)));
    }
    function render() {
        const orders = visibleOrders();
        $('deliveryTotal').textContent = `${orders.length} / ${state.orders.length} phiếu`;
        $('deliveryOrders').innerHTML = orders.map(order => `<article class="order-card status-${category(order.status).toLowerCase()}" data-order-id="${escape(order.id)}" tabindex="0" aria-label="Xem thông tin MX ${escape(order.odrno)}, MW ${escape(order.mw)}" aria-haspopup="dialog">
            <div class="order-horizontal-layout">
                <div class="order-odrno">📦 ${escape(order.odrno)}</div>
                <div class="order-info-inline">
                    <button type="button" class="delivery-mw" data-locations="${escape(order.id)}">MW: ${escape(order.mw || 'Chưa có mã')} · Vị trí</button>
                    <span class="info-item">FITEM: ${escape(order.fItem)}</span>
                    <span class="info-item">SL: ${escape(order.qty)}</span>
                    <span class="info-item">📅 ${escape(order.deliveryDate)}</span>
                    <span class="info-item">⏰ ${escape(order.deliveryTime || 'Chưa có khung giờ')}</span>
                    <span class="info-item">${escape(order.fileType)}</span>
                </div>
                <div><div class="order-status">${escape(label(order.status))}</div><small>${escape(order.time)}</small></div>
            </div>
            ${order.note ? `<div class="delivery-note">Ghi chú bên nhận: ${escape(order.note)}</div>` : ''}
        </article>`).join('') || (state.orders.length
            ? '<p class="delivery-empty">Không có phiếu phù hợp với bộ lọc hoặc từ khóa. Hãy kiểm tra các mục đang chọn.</p>'
            : '<p class="delivery-empty">Chưa có kế hoạch cho ngày/file đã chọn. Hãy chọn ngày đã có kế hoạch hoặc bấm Cập nhật file kế hoạch tại đây.</p>');
        const times = [...new Set(orders.map(order => order.deliveryTime).filter(Boolean))];
        $('deliveryTimes').replaceChildren(...times.map(time => {
            const button = document.createElement('button');
            button.className = 'btn-time-scroll'; button.textContent = time;
            button.onclick = () => {
                const order = orders.find(item => item.deliveryTime === time);
                document.querySelector(`[data-order-id="${CSS.escape(String(order.id))}"]`)?.scrollIntoView({ behavior: 'smooth', block: 'center' });
            };
            return button;
        }));
    }
    async function refresh(reset = false) {
        if (document.hidden && !reset) return;
        if (state.loading && !reset) return;
        if (reset) {
            state.orderAbort?.abort(); state.generation++;
            state.orders = []; render(); $('deliveryLocations').close();
        }
        if (!$('deliveryDate').value) { connection('Hãy chọn ngày bên nhận.', true); return; }
        const generation = state.generation;
        const controller = new AbortController(); state.orderAbort = controller;
        const timeout = setTimeout(() => controller.abort(), 15000);
        state.loading = true;
        try {
            const params = new URLSearchParams({ date: $('deliveryDate').value, fileType: state.file });
            const orders = await request('orders?' + params, controller.signal);
            if (generation !== state.generation) return;
            state.orders = orders.sort((a, b) => leadtimeStart(a.deliveryTime) - leadtimeStart(b.deliveryTime) || String(a.odrno).localeCompare(String(b.odrno))); render();
            connection(`Đã cập nhật ${new Date().toLocaleTimeString('vi-VN')} · Tự kiểm tra mỗi 10 giây${state.legacyBackend ? ' · Đang đọc qua API bên nhận hiện hành' : ''}`);
            if ($('deliveryLocations').open) await loadLocations();
        } catch (error) {
            if (generation !== state.generation) return;
            connection('Chưa cập nhật được. Danh sách đang hiển thị có thể đã cũ; bấm Tải lại.', true);
            if ($('deliveryLocations').open) $('deliveryLocationBody').textContent = 'Chưa cập nhật được vị trí kho. Hãy tải lại trước khi lấy hàng.';
        } finally {
            clearTimeout(timeout);
            if (state.orderAbort === controller) state.loading = false;
        }
    }
    let syncingPlan = false;
    async function syncPlan() {
        if (syncingPlan || document.hidden) return;
        syncingPlan = true;
        $('syncDeliveryPlan').disabled = true;
        $('deliveryPlanStatus').textContent = 'Đang cập nhật file kế hoạch…';
        try {
            const response = await fetch('/api/wip-b3-l2/delivery/sync', { method: 'POST' });
            if (response.status === 404) throw new Error('Cần chạy bản máy chủ mới để tự cập nhật kế hoạch tại B3–L2.');
            if (!response.ok) throw new Error('Không cập nhật được file nguồn. Đang hiển thị kế hoạch đã lưu; hãy thử lại.');
            $('deliveryPlanStatus').textContent = 'Đã cập nhật file kế hoạch lúc ' + new Date().toLocaleTimeString('vi-VN');
            $('deliveryPlanStatus').classList.remove('delivery-error');
            await refresh();
        } catch (error) {
            $('deliveryPlanStatus').textContent = error.message;
            $('deliveryPlanStatus').classList.add('delivery-error');
        } finally {
            syncingPlan = false;
            $('syncDeliveryPlan').disabled = false;
        }
    }
    async function loadLocations() {
        state.locationAbort?.abort();
        const controller = new AbortController(); state.locationAbort = controller;
        const id = state.selectedId;
        const timer = setTimeout(() => controller.abort(), 15000);
        try {
            const params = new URLSearchParams({ orderId: id, date: $('deliveryDate').value });
            const result = state.legacyBackend ? await legacyLocations(id, controller.signal)
                : await request('locations?' + params, controller.signal);
            if (state.locationAbort !== controller || !$('deliveryLocations').open) return;
            const order = state.orders.find(item => String(item.id) === String(id));
            if (order) renderOrderDetails({ ...order, mw: result.mw, odrno: result.odrno });
            $('deliveryLocationsTitle').textContent = `Vị trí MW ${result.mw || '—'} · MX ${result.odrno}`;
            $('locationReceipt').textContent = `${label(result.status)}${result.note ? ' · ' + result.note : ''}${result.time ? ' · ' + result.time : ''}`;
            const rows = result.locations;
            if (!rows.length) {
                $('deliveryLocationBody').textContent = result.mw
                    ? 'Không tìm thấy MW này trong tồn kho hiện tại. Hàng có thể đã xuất hoặc chưa được nhập; trạng thái nhận vẫn theo bên WIP WNK3.'
                    : 'Phiếu chưa có mã MW để tra vị trí.';
                return;
            }
            $('deliveryLocationBody').innerHTML = `<p>${new Set(rows.map(row => row.shelfCode)).size} vị trí · ${rows.length} mã MW/xe</p>
                <table><thead><tr><th>Khu vực</th><th>Dãy / ô</th><th>MW / xe</th></tr></thead><tbody>${rows.map(row =>
                    `<tr><td>${escape(row.area)}</td><td><strong>${escape(row.shelfCode)}</strong></td><td>${escape(row.moNumber)}</td></tr>`).join('')}</tbody></table>`;
        } catch (error) {
            if (state.locationAbort === controller && $('deliveryLocations').open)
                $('deliveryLocationBody').textContent = 'Không tải được vị trí mới nhất. Hãy đóng và mở lại hoặc bấm Tải lại.';
        } finally { clearTimeout(timer); }
    }
    function renderOrderDetails(order) {
        const fields = [['MX', order.odrno], ['MW', order.mw], ['FITEM', order.fItem],
            ['Số lượng', order.qty], ['Ngày kế hoạch bên nhận', $('deliveryDate').value],
            ['Ngày giao trên phiếu', order.deliveryDate], ['Khung giờ bên nhận', order.deliveryTime], ['File', order.fileType]];
        $('deliveryOrderDetails').innerHTML = fields.map(([name, value]) =>
            `<div><dt>${escape(name)}</dt><dd>${escape(value || '—')}</dd></div>`).join('');
    }
    function openOrder(row) {
        if (!row) return;
        const order = state.orders.find(item => String(item.id) === row.dataset.orderId);
        if (!order) return;
        state.selectedId = order.id;
        renderOrderDetails(order);
        $('deliveryLocationsTitle').textContent = `Vị trí MW ${order.mw || '—'} · MX ${order.odrno}`;
        $('locationReceipt').textContent = 'Đang cập nhật trạng thái bên nhận…';
        $('deliveryLocationBody').textContent = 'Đang tìm vị trí trong kho B3–L2…';
        $('deliveryLocations').showModal(); loadLocations();
    }
    $('deliveryOrders').addEventListener('click', event => {
        openOrder(event.target.closest('[data-order-id]'));
    });
    $('deliveryOrders').addEventListener('keydown', event => {
        if (event.target.matches('[data-order-id]') && (event.key === 'Enter' || event.key === ' ')) {
            event.preventDefault(); openOrder(event.target);
        }
    });
    $('closeDeliveryLocations').onclick = () => $('deliveryLocations').close();
    $('deliveryLocations').addEventListener('close', () => { state.locationAbort?.abort(); state.locationAbort = null; state.selectedId = null; });
    $('deliveryDate').addEventListener('change', () => refresh(true));
    $('refreshDelivery').onclick = () => refresh();
    $('syncDeliveryPlan').onclick = syncPlan;
    document.querySelectorAll('[data-file]').forEach(button => {
        button.setAttribute('aria-pressed', String(button.dataset.file === state.file));
        button.onclick = () => {
            state.file = button.dataset.file;
            document.querySelectorAll('[data-file]').forEach(item => {
                item.classList.toggle('btn-primary', item === button); item.classList.toggle('btn-secondary', item !== button);
                item.setAttribute('aria-pressed', String(item === button));
            });
            refresh(true);
        };
    });
    $('deliverySearch').addEventListener('input', event => { state.search = event.target.value.trim().toLowerCase(); render(); });
    $('clearDeliverySearch').onclick = () => { $('deliverySearch').value = ''; state.search = ''; render(); };
    document.querySelectorAll('.delivery-filters input').forEach(input => input.addEventListener('change', render));
    document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });
    setInterval(refresh, 10000);
    setInterval(syncPlan, 300000);
    syncPlan();
    refresh();
    if (window.signalR) {
        const hub = new signalR.HubConnectionBuilder().withUrl('/orderHub').withAutomaticReconnect().build();
        for (const event of ['OrderUpdated', 'MasterFileSynced', 'NewOrdersAdded']) hub.on(event, () => refresh());
        hub.onreconnected(() => refresh());
        const connect = () => hub.start().catch(() => setTimeout(connect, 10000));
        connect();
    }
})();
