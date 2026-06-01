// ==================== STATE MANAGEMENT ====================
const AppState = {
    currentDate: null,
    currentFileType: null,
    allOrders: [],
    filteredOrders: [],
    searchTerm: '',
    filters: {
        pending: true,
        received: true,
        lack: true,
        realtime: false
    },
    scanHistory: [],
    isScannerReady: false,
    currentTime: new Date(),
    pendingScan: null,
    scanTimeout: null
};

// ==================== DOM ELEMENTS ====================
const DOM = {
    dateInput: document.getElementById('dateInput'),
    searchInput: document.getElementById('searchInput'),
    barcodeInput: document.getElementById('barcodeInput'),
    btnConsoleLid: document.getElementById('btnConsoleLid'),
    btnOtherFile: document.getElementById('btnOtherFile'),
    btnSync: document.getElementById('btnSync'),
    btnExport: document.getElementById('btnExport'),
    btnClearSearch: document.getElementById('btnClearSearch'),
    
    filterPending: document.getElementById('filterPending'),
    filterReceived: document.getElementById('filterReceived'),
    filterLack: document.getElementById('filterLack'),
    filterRealtime: document.getElementById('filterRealtime'),
    
    timeScrollContainer: document.getElementById('timeScrollContainer'),
    
    selectedFileLabel: document.getElementById('selectedFileLabel'),
    syncStatus: document.getElementById('syncStatus'),
    scannerStatus: document.getElementById('scannerStatus'),
    totalLabel: document.getElementById('totalLabel'),
    orderList: document.getElementById('orderList'),
    scanHistory: document.getElementById('scanHistory'),
    currentTimeDisplay: document.getElementById('currentTimeDisplay'),
    
    modalCheck: document.getElementById('modalCheck'),
    modalNotFound: document.getElementById('modalNotFound'),
    modalMxDetail: document.getElementById('modalMxDetail'),
    
    checkModalTitle: document.getElementById('checkModalTitle'),
    moDetails: document.getElementById('moDetails'),
    btnMarkReceived: document.getElementById('btnMarkReceived'),
    btnMarkLack: document.getElementById('btnMarkLack'),
    notFoundOdrno: document.getElementById('notFoundOdrno'),
    btnConfirmNotFound: document.getElementById('btnConfirmNotFound'),
    btnCancelNotFound: document.getElementById('btnCancelNotFound'),
    
    btnCloseMxDetail: document.getElementById('btnCloseMxDetail'),
    mxDetailOdrno: document.getElementById('mxDetailOdrno'),
    mxDetailTotalItems: document.getElementById('mxDetailTotalItems'),
    mxItemsList: document.getElementById('mxItemsList'),
    mxPartsList: document.getElementById('mxPartsList'),
    btnExportMxDetail: document.getElementById('btnExportMxDetail'),
    btnPrintMxDetail: document.getElementById('btnPrintMxDetail'),
    
    toastContainer: document.getElementById('toastContainer')
};

// ==================== INITIALIZATION ====================
function init() {
    console.log('🚀 Initializing WIP WNK3 App...');
    DOM.dateInput.valueAsDate = new Date();
    AppState.currentDate = formatDate(new Date());
    
    setupEventListeners();
    setupBarcodeScanner();
    
    setInterval(updateCurrentTime, 1000);
    updateCurrentTime();
    
    performSync(); 
}

function setupEventListeners() {
    DOM.btnConsoleLid.addEventListener('click', () => loadOrders('Console Lid'));
    DOM.btnOtherFile.addEventListener('click', () => loadOrders('Other'));
    
    DOM.btnSync.addEventListener('click', performSync);
    DOM.btnExport.addEventListener('click', exportReport);
    
    DOM.searchInput.addEventListener('input', () => { AppState.searchTerm = DOM.searchInput.value.toLowerCase().trim(); applyFilters(); });
    DOM.btnClearSearch.addEventListener('click', () => { DOM.searchInput.value = ''; AppState.searchTerm = ''; applyFilters(); });
    
    const updateFilters = () => {
        AppState.filters.pending = DOM.filterPending.checked;
        AppState.filters.received = DOM.filterReceived.checked;
        AppState.filters.lack = DOM.filterLack.checked;
        applyFilters();
    };
    DOM.filterPending.addEventListener('change', updateFilters);
    DOM.filterReceived.addEventListener('change', updateFilters);
    DOM.filterLack.addEventListener('change', updateFilters);
    
    if (DOM.filterRealtime) {
        DOM.filterRealtime.addEventListener('change', (e) => {
            AppState.filters.realtime = e.target.checked;
            if (e.target.checked) scrollToCurrentTimeOrder();
        });
    }
    
    DOM.dateInput.addEventListener('change', () => {
        AppState.currentDate = formatDate(new Date(DOM.dateInput.value));
        if (AppState.currentFileType) loadOrders(AppState.currentFileType);
    });
    
    DOM.barcodeInput.addEventListener('keypress', (e) => {
        if (e.key === 'Enter') {
            const barcode = DOM.barcodeInput.value.trim().toUpperCase();
            if (barcode) { processScan(barcode); DOM.barcodeInput.value = ''; }
        }
    });
    
    DOM.btnMarkReceived.addEventListener('click', () => { clearPendingScan(); markStatus('Received'); });
    DOM.btnMarkLack.addEventListener('click', () => { clearPendingScan(); markStatus('Lack'); });
    DOM.btnConfirmNotFound.addEventListener('click', confirmNotFound);
    DOM.btnCancelNotFound.addEventListener('click', () => { closeModal(DOM.modalNotFound); refocusScanner(); });
    
    DOM.btnCloseMxDetail.addEventListener('click', () => closeModal(DOM.modalMxDetail));
    DOM.btnExportMxDetail.addEventListener('click', exportMxDetail);
    DOM.btnPrintMxDetail.addEventListener('click', printMxDetail);
    
    [DOM.modalCheck, DOM.modalNotFound, DOM.modalMxDetail].forEach(modal => {
        modal.addEventListener('click', (e) => {
            if (e.target === modal) {
                if (modal === DOM.modalCheck) clearPendingScan();
                closeModal(modal); refocusScanner();
            }
        });
    });
    
    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
            clearPendingScan();
            closeModal(DOM.modalCheck); closeModal(DOM.modalNotFound); closeModal(DOM.modalMxDetail);
            refocusScanner();
        }
    });
}

function updateCurrentTime() {
    const now = new Date();
    AppState.currentTime = now;
    if (DOM.currentTimeDisplay) {
        DOM.currentTimeDisplay.textContent = `Giờ: ${now.toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit', second: '2-digit' })}`;
    }
}

function scrollToCurrentTimeOrder() {
    if (!AppState.filters.realtime || AppState.filteredOrders.length === 0) return;
    const targetOrder = AppState.filteredOrders.find(order => order.deliveryTime && isInTimeRange(order.deliveryTime, AppState.currentTime));
    if (!targetOrder) return;
    
    const orderCard = document.querySelector(`.order-card[data-odrno="${targetOrder.odrno}"]`);
    if (orderCard) {
        orderCard.scrollIntoView({ behavior: 'smooth', block: 'center' });
        orderCard.classList.add('flash-highlight');
        setTimeout(() => orderCard.classList.remove('flash-highlight'), 2000);
    }
}

setInterval(() => {
    if (AppState.filters.realtime && AppState.allOrders.length > 0) {
        updateCurrentTime(); scrollToCurrentTimeOrder();
    }
}, 5 * 60 * 1000);

// ==================== CHỨC NĂNG CUỘN ĐẾN GIỜ ====================
function updateTimeScrollDropdown() {
    if (!DOM.timeScrollContainer) return;

    const times = [...new Set(AppState.filteredOrders.map(o => o.deliveryTime).filter(t => t && t.trim() !== ''))].sort();
    
    if (times.length === 0) {
        DOM.timeScrollContainer.innerHTML = '<span style="color: #95a5a6; font-size: 13px; font-style: italic;">Không có khung giờ nào</span>';
        return;
    }

    DOM.timeScrollContainer.innerHTML = ''; 

    times.forEach(t => {
        const startHour = t.split('-')[0].trim();
        const btn = document.createElement('button');
        btn.className = 'btn-time-scroll';
        btn.textContent = startHour;
        btn.title = `Cuộn đến khung giờ ${t}`; 
        
        btn.addEventListener('click', () => {
            scrollToSpecificTime(t);
        });

        DOM.timeScrollContainer.appendChild(btn);
    });
}

function scrollToSpecificTime(timeWindow) {
    const targetOrder = AppState.filteredOrders.find(order => order.deliveryTime === timeWindow);
    if (targetOrder) {
        const orderCard = document.querySelector(`.order-card[data-odrno="${targetOrder.odrno}"]`);
        if (orderCard) {
            orderCard.scrollIntoView({ behavior: 'smooth', block: 'center' });
            orderCard.classList.add('flash-highlight');
            setTimeout(() => orderCard.classList.remove('flash-highlight'), 2000);
        }
    }
}

// ==================== BARCODE SCANNER & SCAN LOGIC ====================
function setupBarcodeScanner() { DOM.barcodeInput.disabled = true; AppState.isScannerReady = false; updateScannerStatus(); }
function enableScanner() { DOM.barcodeInput.disabled = false; AppState.isScannerReady = true; updateScannerStatus(); refocusScanner(); }
function disableScanner() { DOM.barcodeInput.disabled = true; AppState.isScannerReady = false; updateScannerStatus(); }

function updateScannerStatus() {
    if (AppState.isScannerReady) {
        DOM.scannerStatus.textContent = '✅ Sẵn sàng quét';
        DOM.scannerStatus.classList.add('ready'); DOM.scannerStatus.classList.remove('pending');
        DOM.scannerStatus.style.background = ''; DOM.scannerStatus.style.animation = '';
    } else {
        DOM.scannerStatus.textContent = '⏸️ Chưa sẵn sàng';
        DOM.scannerStatus.classList.remove('ready', 'pending');
        DOM.scannerStatus.style.background = ''; DOM.scannerStatus.style.animation = '';
    }
}

function refocusScanner() {
    if (AppState.isScannerReady) setTimeout(() => { DOM.barcodeInput.value = ''; DOM.barcodeInput.focus(); }, 100);
}

function setPendingScan(order) {
    if (AppState.scanTimeout) clearTimeout(AppState.scanTimeout);
    AppState.pendingScan = order;
    AppState.scanTimeout = setTimeout(() => {
        clearPendingScan();
        showToast(`⏰ Hết thời gian chờ quét lần 2 cho ${order.odrno}`, 'warning');
    }, 30000); // Đợi 30 giây
    
    if (AppState.isScannerReady) {
        DOM.scannerStatus.textContent = `⏳ Quét lại "${order.odrno}" để xác nhận RECEIVED`;
        DOM.scannerStatus.classList.add('ready', 'pending');
        DOM.scannerStatus.style.background = 'linear-gradient(135deg, #f39c12 0%, #e67e22 100%)';
        DOM.scannerStatus.style.animation = 'pulse 1s infinite';
    }
}

function clearPendingScan() {
    if (AppState.scanTimeout) { clearTimeout(AppState.scanTimeout); AppState.scanTimeout = null; }
    AppState.pendingScan = null;
    updateScannerStatus();
}

async function processScan(odrno) {
    // KỊCH BẢN QUÉT LẦN 2
    if (AppState.pendingScan && AppState.pendingScan.odrno === odrno) {
        let isLack = false; let notesArr = [];
        
        document.querySelectorAll('.part-lack-cb:checked').forEach(cb => {
            isLack = true;
            let partName = cb.dataset.part;
            let inputEl = document.querySelector(`.part-note-input[data-part="${partName}"]`);
            let missingQty = inputEl ? inputEl.value.trim() : "";
            notesArr.push(missingQty ? `${partName} (Thiếu ${missingQty})` : `${partName} (Thiếu)`);
        });
        
        let finalStatus = isLack ? 'Lack' : 'Received';
        let finalNote = notesArr.join(' | ');

        clearPendingScan();
        closeModal(DOM.modalMxDetail); 
        
        showToast(`🎯 Quét lần 2 → Xác nhận ${finalStatus}: ${odrno}`, finalStatus === 'Lack' ? 'warning' : 'success');
        await updateOrderStatus(odrno, finalStatus, finalNote);
        addToScanHistory(odrno, finalStatus);
        
        const orderIndex = AppState.allOrders.findIndex(o => o.odrno === odrno);
        if (orderIndex !== -1) {
            AppState.allOrders[orderIndex].status = finalStatus;
            AppState.allOrders[orderIndex].note = finalNote;
            AppState.allOrders[orderIndex].time = new Date().toLocaleString('vi-VN');
        }
        
        applyFilters();
        refocusScanner();
        return;
    }
    
    // KỊCH BẢN QUÉT LẦN 1
    showToast(`📷 Đã quét: ${odrno}`, 'info');
    const order = AppState.allOrders.find(o => o.odrno.toUpperCase() === odrno);
    
    if (order) {
        setPendingScan(order);
        showMxDetail(odrno, true); 
    } else {
        clearPendingScan();
        showNotFoundModal(odrno);
    }
}

// ==================== DATE HANDLING & SYNC & LOAD ====================
function formatDate(date) {
    const day = String(date.getDate()).padStart(2, '0');
    const month = String(date.getMonth() + 1).padStart(2, '0');
    return `${day}.${month}`;
}

async function performSync() {
    DOM.syncStatus.textContent = '🔄 Đang đồng bộ...';
    DOM.syncStatus.style.color = '#f39c12';
    DOM.btnSync.disabled = true;
    disableScanner();
    
    try {
        const response = await fetch('/sync');
        if (!response.ok) throw new Error('Sync failed');
        
        DOM.syncStatus.textContent = '✅ Đồng bộ thành công';
        DOM.syncStatus.style.color = '#27ae60';
        showToast('✅ Đồng bộ Master File thành công', 'success');
        
        if (AppState.currentFileType) await loadOrders(AppState.currentFileType);
        
        setTimeout(() => { DOM.syncStatus.textContent = '✅ Sẵn sàng'; DOM.syncStatus.style.color = '#27ae60'; }, 3000);
    } catch (error) {
        DOM.syncStatus.textContent = '❌ Lỗi đồng bộ'; DOM.syncStatus.style.color = '#e74c3c';
        showToast('❌ Lỗi đồng bộ Master File', 'error');
    } finally { DOM.btnSync.disabled = false; }
}

async function loadOrders(fileType = AppState.currentFileType) {
    if (!AppState.currentDate) return;
    if (!fileType) {
        showToast('Vui lòng chọn loại file (Console Lid / File Khác)', 'warning');
        return;
    }

    AppState.currentFileType = fileType;
    disableScanner();
    
    try {
        const response = await fetch(`/orders?date=${AppState.currentDate}&fileType=${encodeURIComponent(fileType)}`);
        
        if (!response.ok) {
            const errText = await response.text();
            throw new Error(`Lỗi Server: ${errText}`);
        }
        
        AppState.allOrders = await response.json();
        AppState.scanHistory = [];
        
        DOM.selectedFileLabel.textContent = `✅ Đang xem: ${fileType} (${AppState.currentDate})`;
        DOM.selectedFileLabel.style.color = '#27ae60';
        
        applyFilters();
        updateScanHistory();
        enableScanner();
        showToast(`✅ Đã tải ${AppState.allOrders.length} MX`, 'success');
        
    } catch (error) {
        console.error("🔥 LỖI CHI TIẾT TẢI DỮ LIỆU:", error);
        showToast(`❌ ${error.message}`, 'error');
        DOM.selectedFileLabel.textContent = 'Lỗi tải dữ liệu - Xem F12';
        DOM.selectedFileLabel.style.color = '#e74c3c';
    }
}

// ==================== FILTER & RENDER ====================
function applyFilters() {
    try {
        AppState.filteredOrders = AppState.allOrders.filter(order => {
            if (AppState.searchTerm) {
                const searchableText = [
                    order.odrno || '', 
                    order.fitem || '', 
                    order.mw || '', 
                    order.qty || '', 
                    order.deliveryTime || '', 
                    order.status || ''
                ].join(' ').toLowerCase();
                
                if (!searchableText.includes(AppState.searchTerm)) return false;
            }
            
            const status = (order.status || 'pending').toLowerCase();
            if (status === 'pending' && !AppState.filters.pending) return false;
            if (status === 'received' && !AppState.filters.received) return false;
            if (status === 'lack' && !AppState.filters.lack) return false;
            
            return true;
        });
        
        AppState.filteredOrders.sort((a, b) => {
            const timeA = a.deliveryTime ? a.deliveryTime.trim() : "24:00";
            const timeB = b.deliveryTime ? b.deliveryTime.trim() : "24:00";
            return timeA.localeCompare(timeB);
        });
        
        renderOrderList();
        updateTimeScrollDropdown();
        
    } catch (err) {
        console.error("🔥 Lỗi sập Javascript khi lọc dữ liệu:", err);
        showToast("Lỗi xử lý dữ liệu trên trình duyệt!", "error");
    }
}

function renderOrderList() {
    if (AppState.filteredOrders.length === 0) {
        DOM.orderList.innerHTML = `<div style="text-align: center; padding: 50px; color: #95a5a6; font-size: 18px;">📭 Không có dữ liệu</div>`;
        if (DOM.totalLabel) DOM.totalLabel.textContent = 'Tổng: 0 MX';
        return;
    }
    
    DOM.orderList.innerHTML = AppState.filteredOrders.map(order => createOrderCard(order)).join('');
    
    if (DOM.totalLabel) {
        DOM.totalLabel.textContent = `Hiển thị: ${AppState.filteredOrders.length}/${AppState.allOrders.length} MX`;
    }
    
    document.querySelectorAll('.order-card').forEach(card => {
        card.addEventListener('click', () => showMxDetail(card.dataset.odrno, false));
    });
}

// ==================== TẠO THẺ MX ====================
function createOrderCard(order) {
    const statusClass = (order.status || 'pending').toLowerCase().replace(' ', '');
    const statusIcons = { 'pending': '⏳', 'received': '✅', 'lack': '❌', 'notfound': '⚠️' };
    const icon = statusIcons[statusClass] || '⏳';
    
    let infoParts = [];
    if (order.fitem) infoParts.push(`<strong>FITEM:</strong> ${order.fitem}`);
    if (order.mw) infoParts.push(`<strong>MW#:</strong> ${order.mw}`);
    if (order.qty) infoParts.push(`<strong>Qty:</strong> ${order.qty}`);
    if (order.deliveryTime) infoParts.push(`⏰ ${order.deliveryTime}`);
    
    const noteHtml = order.note ? `<div style="color: #ffda79; font-size: 13px; font-weight: bold;">📝 Ghi chú: ${order.note}</div>` : '';
    
    // 🚀 LỌC CHỈ LẤY GIỜ (Bỏ ngày)
    let justTime = '';
    if (order.time) {
        // order.time thường có dạng "14:30:00 22/05/2026" hoặc ngược lại. Ta tách bằng khoảng trắng và lấy phần có dấu ":"
        const timeParts = order.time.split(' ');
        justTime = timeParts.find(p => p.includes(':')) || order.time;
    }

    return `
        <div class="order-card status-${statusClass}" data-odrno="${order.odrno}">
            <div class="order-horizontal-layout">
                <div class="order-odrno">📦 ${order.odrno}</div>
                <div class="order-info-inline">
                    ${infoParts.map(p => `<span class="info-item">${p}</span>`).join('')}
                </div>
                
                <!-- 🚀 KHỐI BÊN PHẢI: TRẠNG THÁI + GIỜ CẬP NHẬT -->
                <div style="display: flex; flex-direction: column; align-items: flex-end; gap: 5px; min-width: 100px;">
                    <div class="order-status">${icon} ${order.status || 'Pending'}</div>
                    ${justTime ? `<div class="order-time" style="margin: 0; font-size: 16px; font-weight: bold; color: #ffffff;">🕐 ${justTime}</div>` : ''}
                </div>
            </div>
            
            <!-- GHI CHÚ THIẾU HÀNG (NẾU CÓ) -->
            ${noteHtml ? `
                <div style="border-top: 1px dashed rgba(255,255,255,0.1); margin-top: 12px; padding-top: 8px;">
                    ${noteHtml}
                </div>
            ` : ''}
        </div>
    `;
}

// ==================== SCAN HISTORY ====================
function addToScanHistory(odrno, status) {
    AppState.scanHistory.unshift({ odrno, status, time: new Date().toLocaleTimeString('vi-VN') });
    if (AppState.scanHistory.length > 10) AppState.scanHistory = AppState.scanHistory.slice(0, 10);
    updateScanHistory();
}

function updateScanHistory() {
    if (AppState.scanHistory.length === 0) {
        DOM.scanHistory.innerHTML = '<div class="history-empty">Chưa có MO nào</div>';
        return;
    }
    DOM.scanHistory.innerHTML = AppState.scanHistory.map(item => {
        const statusClass = item.status.toLowerCase().replace(' ', '');
        const icon = { 'received': '✅', 'lack': '❌', 'notfound': '⚠️' }[statusClass] || '📦';
        return `<div class="history-item status-${statusClass}"><span>${icon} ${item.odrno}</span><span class="history-time">${item.time}</span></div>`;
    }).join('');
}

// ==================== API ACTIONS & MODALS ====================
async function updateOrderStatus(odrno, status, note = "") {
    try {
        const response = await fetch('/update', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ odrno, status, note })
        });
        if (!response.ok) throw new Error('Update failed');
        const icon = { 'Received': '✅', 'Lack': '❌', 'NOT FOUND': '⚠️' }[status] || '✅';
        showToast(`${icon} ${odrno} → ${status}`, 'success');
    } catch (error) { showToast('❌ Lỗi cập nhật trạng thái', 'error'); }
}

async function markStatus(status) {
    const odrno = DOM.modalCheck.dataset.currentOdrno;
    if (!odrno) return;
    await updateOrderStatus(odrno, status);
    closeModal(DOM.modalCheck);
    addToScanHistory(odrno, status);
    
    const orderIndex = AppState.allOrders.findIndex(o => o.odrno === odrno);
    if (orderIndex !== -1) {
        AppState.allOrders[orderIndex].status = status;
        AppState.allOrders[orderIndex].time = new Date().toLocaleString('vi-VN');
    }
    applyFilters(); refocusScanner();
}

function showNotFoundModal(odrno) {
    DOM.notFoundOdrno.textContent = odrno;
    DOM.modalNotFound.dataset.notFoundOdrno = odrno;
    openModal(DOM.modalNotFound);
}

async function confirmNotFound() {
    const odrno = DOM.modalNotFound.dataset.notFoundOdrno;
    if (!odrno) return;
    await updateOrderStatus(odrno, 'NOT FOUND');
    closeModal(DOM.modalNotFound);
    addToScanHistory(odrno, 'NOT FOUND');
    
    const existingOrder = AppState.allOrders.find(o => o.odrno === odrno);
    if (existingOrder) {
        existingOrder.status = 'NOT FOUND';
        existingOrder.time = new Date().toLocaleString('vi-VN');
    } else {
        AppState.allOrders.push({ odrno, fitem: '', mw: '', qty: '', deliveryDate: '', deliveryTime: '', status: 'NOT FOUND', time: new Date().toLocaleString('vi-VN') });
    }
    applyFilters(); refocusScanner();
}

async function showMxDetail(odrno, isFromScan = false) {
    if (!AppState.currentDate) { showToast('⚠️ Không xác định được ngày', 'warning'); return; }
    try {
        DOM.mxDetailOdrno.textContent = odrno;
        DOM.mxDetailTotalItems.textContent = '-';
        DOM.mxItemsList.innerHTML = '<div class="loading-text">⏳ Đang tải danh sách Items...</div>';
        DOM.mxPartsList.innerHTML = '<div class="loading-text">⏳ Đang tải chi tiết Parts...</div>';
        
        const order = AppState.allOrders.find(o => o.odrno.toUpperCase() === odrno.toUpperCase());
        const oldNote = document.getElementById('mxDetailNoteBox');
        if (oldNote) oldNote.remove(); 
        
        if (order && order.note) {
            document.querySelector('.mx-info-row').insertAdjacentHTML('beforeend', `
                <div class="mx-info-item" id="mxDetailNoteBox" style="flex: 100%; margin-top: 15px; padding-top: 15px; border-top: 1px dashed rgba(255,255,255,0.2);">
                    <label style="color: #e74c3c; font-weight: bold; font-size: 14px;">📝 GHI CHÚ THIẾU HÀNG:</label>
                    <span style="font-size: 16px; color: #ffda79; display: block; margin-top: 5px; font-weight: bold;">${order.note}</span>
                </div>
            `);
        }

        const existingInst = document.getElementById('scanInstructionBox');
        if (existingInst) existingInst.remove();
        if (isFromScan) {
            DOM.modalMxDetail.querySelector('.modal-body').insertAdjacentHTML('afterbegin', `
                <div class="scan-instruction-box" id="scanInstructionBox">
                    <h3>🎯 HÃY KIỂM TRA HÀNG VÀ QUÉT LẠI MÃ "${odrno}" ĐỂ XÁC NHẬN</h3>
                    <p style="margin-top:5px; font-size:13px; color:white;">Nếu thiếu hàng, hãy tick vào ô "Thiếu" và ghi chú số lượng.</p>
                </div>`);
        }

        openModal(DOM.modalMxDetail);
        
        const response = await fetch(`/mx-detail?odrno=${encodeURIComponent(odrno)}&date=${AppState.currentDate}`);
        if (!response.ok) throw new Error(await response.text());
        const data = await response.json();
        
        DOM.mxDetailTotalItems.textContent = data.items.reduce((sum, item) => sum + item.quantity, 0);
        
        if (data.items && data.items.length > 0) {
            DOM.mxItemsList.innerHTML = data.items.map(item => `
                <div class="mx-item-card"><div class="item-code">📦 ${item.itemCode}</div><div class="item-qty">Số lượng: ${item.quantity}</div></div>
            `).join('');
        } else DOM.mxItemsList.innerHTML = '<div class="mx-parts-empty">Không có Items</div>';
        
        if (data.parts && data.parts.length > 0) {
            DOM.mxPartsList.innerHTML = data.parts.map(part => `
                <div class="mx-part-row">
                    <div class="mx-part-name">${part.partName}</div>
                    <div class="mx-part-qty" style="margin-right: 20px;">${part.quantity}</div>
                    ${isFromScan ? `
                    <div class="mx-part-action">
                        <label class="lack-checkbox-label"><input type="checkbox" class="part-lack-cb" data-part="${part.partName}"> Thiếu</label>
                        <input type="text" class="part-note-input" data-part="${part.partName}" placeholder="Thiếu bao nhiêu?" style="display: none;">
                    </div>` : ''}
                </div>
            `).join('');

            if (isFromScan) {
                document.querySelectorAll('.part-lack-cb').forEach(cb => {
                    cb.addEventListener('change', function() {
                        const inputEl = document.querySelector(`.part-note-input[data-part="${this.dataset.part}"]`);
                        inputEl.style.display = this.checked ? 'block' : 'none';
                        if (this.checked) inputEl.focus(); else inputEl.value = '';
                    });
                });
            }
        } else DOM.mxPartsList.innerHTML = '<div class="mx-parts-empty">Không có Parts</div>';
    } catch (error) {
        DOM.mxItemsList.innerHTML = `<div class="mx-parts-empty">❌ Lỗi tải dữ liệu</div>`;
        DOM.mxPartsList.innerHTML = `<div class="mx-parts-empty">${error.message}</div>`;
    }
}

async function exportMxDetail() { showToast('🚧 Chức năng xuất Excel đang phát triển', 'info'); }
function printMxDetail() { window.print(); }
// ==================== EXPORT REPORT (CÓ BẮT LỖI CHI TIẾT) ====================
async function exportReport() {
    if (AppState.allOrders.length === 0) { 
        showToast('Chưa có dữ liệu để xuất', 'warning'); 
        return; 
    }
    
    // Hiển thị thông báo đang xử lý và khóa nút để tránh bấm 2 lần
    showToast('⏳ Đang tạo báo cáo, vui lòng đợi...', 'info');
    DOM.btnExport.disabled = true;

    try {
        const response = await fetch('/export', {
            method: 'POST', 
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ 
                date: AppState.currentDate, 
                fileType: AppState.currentFileType || "", 
                orders: AppState.allOrders 
            })
        });
        
        // 🚀 ĐÃ SỬA: Lấy chính xác thông báo lỗi từ Server C#
        if (!response.ok) {
            const errorText = await response.text();
            
            // Nếu lỗi trả về là JSON (từ hệ thống .NET), ta cố gắng bóc tách nó ra
            try {
                const errorJson = JSON.parse(errorText);
                throw new Error(errorJson.detail || errorJson.title || errorText);
            } catch {
                throw new Error(errorText); // Nếu là text bình thường
            }
        }
        
        const blob = await response.blob();
        const a = document.createElement('a');
        a.href = window.URL.createObjectURL(blob);
        a.download = `BaoCao_MO_${AppState.currentDate}_${Date.now()}.xlsb`; 
        document.body.appendChild(a); 
        a.click(); 
        document.body.removeChild(a);
        
        showToast('✅ Đã xuất báo cáo thành công!', 'success');
        
    } catch (error) { 
        console.error("🔥 LỖI CHI TIẾT XUẤT BÁO CÁO:", error);
        
        // In thẳng lỗi từ Server lên màn hình
        showToast(`❌ Lỗi: ${error.message}`, 'error'); 
    } finally {
        DOM.btnExport.disabled = false;
    }
}

function openModal(modal) { modal.classList.add('active'); }
function closeModal(modal) { modal.classList.remove('active'); }
function showToast(message, type = 'info') {
    const toast = document.createElement('div');
    toast.className = `toast toast-${type}`;
    const icons = { 'success': '✅', 'error': '❌', 'warning': '⚠️', 'info': 'ℹ️' };
    toast.innerHTML = `<div class="toast-icon">${icons[type]}</div><div class="toast-message">${message}</div><button class="toast-close">&times;</button>`;
    DOM.toastContainer.appendChild(toast);
    toast.querySelector('.toast-close').addEventListener('click', () => toast.remove());
    setTimeout(() => { toast.style.opacity = '0'; setTimeout(() => toast.remove(), 300); }, 4000);
}

// ==================== SIGNALR CONNECTION ====================
let signalRConnection = null;
async function initSignalR() {
    try {
        signalRConnection = new signalR.HubConnectionBuilder().withUrl("/orderHub").withAutomaticReconnect().build();
        signalRConnection.on("OrderUpdated", (data) => {
            if (!data || !data.odrno) return; 
            const orderIndex = AppState.allOrders.findIndex(o => o.odrno.toUpperCase() === data.odrno.toUpperCase());
            if (orderIndex !== -1) {
                AppState.allOrders[orderIndex].status = data.status;
                AppState.allOrders[orderIndex].note = data.note;
                AppState.allOrders[orderIndex].time = new Date().toLocaleString('vi-VN');
                applyFilters();
            }
        });
        await signalRConnection.start();
    } catch (err) { setTimeout(initSignalR, 5000); }
}

document.addEventListener('DOMContentLoaded', () => { init(); initSignalR(); });
