document.addEventListener('DOMContentLoaded', () => {
    const dateInput = document.getElementById('dashDate');
    const searchInput = document.getElementById('searchInput');
    const timeWindowFilter = document.getElementById('timeWindowFilter');
    const leftBody = document.getElementById('leftBody');
    const rightBody = document.getElementById('rightBody');
    
    const statTotal = document.getElementById('statTotal');
    const statReceived = document.getElementById('statReceived');
    const statLack = document.getElementById('statLack');
    const statPending = document.getElementById('statPending');

    const cbBarOnTime = document.getElementById('cbBarOnTime');
    const cbBarLate = document.getElementById('cbBarLate');
    const cbBarPending = document.getElementById('cbBarPending');

    let allDashboardData = [];
    let filteredData = []; 

    let lineChartInstance = null;
    let doughnutChartInstance = null;
    let selectedChartHour = null;

    // ==================== LAYOUT TOGGLE ====================
    let isSplitView = localStorage.getItem('dashboardSplitView') === 'true';
    const btnLayoutToggle = document.getElementById('btnLayoutToggle');

    if (isSplitView) document.body.classList.add('split-view');

    if (btnLayoutToggle) {
        btnLayoutToggle.addEventListener('click', () => {
            isSplitView = !isSplitView;
            if (isSplitView) {
                document.body.classList.add('split-view');
                localStorage.setItem('dashboardSplitView', 'true');
            } else {
                document.body.classList.remove('split-view');
                localStorage.setItem('dashboardSplitView', 'false');
            }
            renderDashboard();
            if (typeof updateCharts === 'function') updateCharts();
        });
    }

    // ==================== DATE FORMAT & STATUS CHECK ====================
    dateInput.valueAsDate = new Date();
    
    function formatDate(date) {
        const day = String(date.getDate()).padStart(2, '0');
        const month = String(date.getMonth() + 1).padStart(2, '0');
        return `${day}.${month}`;
    }

    function extractTimeWindows(data) {
        const windows = new Set();
        data.forEach(order => { if (order.timeWindow && order.timeWindow.trim() !== '') windows.add(order.timeWindow.trim()); });
        return Array.from(windows).sort();
    }

    function populateTimeWindowFilter(windows) {
        const currentSelectedValue = timeWindowFilter.value;

        const totalCount = allDashboardData.length;
        timeWindowFilter.innerHTML = `<option value="">-- Tất cả -- (${totalCount})</option>`;
        
        windows.forEach(window => {
            const count = allDashboardData.filter(order => order.timeWindow === window).length;
            const option = document.createElement('option');
            option.value = window;
            option.textContent = `${window}`.padEnd(15) + `(${count})`;
            timeWindowFilter.appendChild(option);
        });

        if (currentSelectedValue) {
            // Cố gắng gán lại giá trị cũ. Nếu giá trị đó vẫn còn tồn tại trong danh sách mới thì nó sẽ được chọn.
            timeWindowFilter.value = currentSelectedValue;
        }
    }

    function checkDeliveryStatus(deliveryDateStr, timeWindow, updateTime, status) {
        if (!timeWindow || !timeWindow.includes('-')) return { state: 'normal', text: '' };

        let targetDate = new Date(dateInput.value); 
        if (deliveryDateStr) {
            if (deliveryDateStr.includes('/') && deliveryDateStr.split('/')[2].length === 4) {
                let parts = deliveryDateStr.split('/');
                let parsed = new Date(`${parts[2]}-${parts[1]}-${parts[0]}T00:00:00`);
                if (!isNaN(parsed.getTime())) targetDate = parsed;
            } else {
                let parsed = new Date(deliveryDateStr);
                if (!isNaN(parsed.getTime())) targetDate = parsed;
            }
        }

        const [startStr, endStr] = timeWindow.split('-').map(s => s.trim());
        const [startH, startM] = startStr.split(':').map(Number);
        const [endH, endM] = endStr.split(':').map(Number);

        let startDate = new Date(targetDate); startDate.setHours(startH, startM, 0, 0);
        let endDate = new Date(targetDate); endDate.setHours(endH, endM, 0, 0);
        if (endH < startH) endDate.setDate(endDate.getDate() + 1);

        if (status === 'Received' || status === 'Lack') {
            if (!updateTime) return { state: 'normal', text: '' };
            let actualTime = new Date(updateTime.replace(' ', 'T'));
            if (actualTime > endDate) return { state: 'late', text: '⚠️ TRỄ' };
            if (actualTime < startDate) return { state: 'early', text: '🌟 SỚM' };
            return { state: 'ontime', text: '' }; 
        } else {
            let now = new Date();
            if (now > endDate) return { state: 'late', text: '⚠️ QUÁ HẠN' };
            return { state: 'normal', text: '' };
        }
    }

    // ==================== FETCH DATA ====================
    async function fetchDashboardData() {
        const dateStr = formatDate(new Date(dateInput.value));
        try {
            const response = await fetch(`/api/dashboard?date=${dateStr}&t=${new Date().getTime()}`);
            if (!response.ok) throw new Error("Lỗi tải dữ liệu");
            allDashboardData = await response.json();
            
            const windows = extractTimeWindows(allDashboardData);
            populateTimeWindowFilter(windows);
            
            applyFilters();
        } catch (error) {
            leftBody.innerHTML = `<tr><td colspan="2" style="color: #e74c3c; text-align: center; padding: 50px;">Lỗi: ${error.message}</td></tr>`;
            if (rightBody) rightBody.innerHTML = `<tr><td colspan="2"></td></tr>`;
        }
    }

    function applyFilters() {
        const searchTerm = searchInput.value.toLowerCase().trim();
        const selectedTimeWindow = timeWindowFilter.value;

        // Lấy trạng thái của 3 Checkbox trên biểu đồ
        const cbOnTime = document.getElementById('cbBarOnTime');
        const cbLate = document.getElementById('cbBarLate');
        const cbPending = document.getElementById('cbBarPending');

        const showOnTime = cbOnTime ? cbOnTime.checked : true;
        const showLate = cbLate ? cbLate.checked : true;
        const showPending = cbPending ? cbPending.checked : true;

        filteredData = allDashboardData.filter(order => {
            // 1. Lọc theo ô Tìm kiếm
            if (searchTerm) {
                const searchableText = `${order.odrno} ${order.fitem} ${order.mw}`.toLowerCase();
                if (!searchableText.includes(searchTerm)) return false;
            }
            
            // 2. Lọc theo Khung giờ (Dropdown)
            if (selectedTimeWindow && order.timeWindow !== selectedTimeWindow) return false;

            // 🚀 3. LỌC THEO CỘT BIỂU ĐỒ KHI CLICK (MỚI THÊM)
            if (selectedChartHour) {
                let orderHour = "";
                if (order.status !== 'Pending' && order.updateTime) {
                    // Lấy giờ thực tế cập nhật
                    const timePart = order.updateTime.split(' ')[1];
                    if (timePart) orderHour = timePart.split(':')[0] + ':00';
                } else if (order.status === 'Pending' && order.timeWindow) {
                    // Nếu chưa giao, lấy giờ quy định bắt đầu
                    const startStr = order.timeWindow.split('-')[0].trim();
                    if (startStr) orderHour = startStr.split(':')[0] + ':00';
                }
                
                // Nếu giờ của MX không khớp với cột đã click -> Ẩn
                if (orderHour !== selectedChartHour) return false;
            }

            // 4. LỌC THEO CHECKBOX TRẠNG THÁI
            const delStatus = checkDeliveryStatus(order.deliveryDate, order.timeWindow, order.updateTime, order.status);
            
            if (order.status === 'Pending') {
                if (!showPending) return false;
            } else {
                if (delStatus.state === 'late') {
                    if (!showLate) return false;
                } else {
                    if (!showOnTime) return false;
                }
            }

            return true;
        });

        renderDashboard();
        updateStats();
        updateCharts();
    }

    function updateStats() {
        statTotal.textContent = filteredData.length;
        let received = 0, lack = 0, pending = 0;
        filteredData.forEach(order => {
            if (order.status === 'Received') received++;
            else if (order.status === 'Lack') lack++;
            else if (order.status === 'Pending') pending++;
        });
        statReceived.textContent = received; statLack.textContent = lack; statPending.textContent = pending;
    }

    // ==================== VẼ BẢNG (CHỈ CÒN 2 CỘT) ====================
    // ==================== VẼ DANH SÁCH MX (DẠNG CARD) ====================
    function renderDashboard() {
        const dashBody = document.getElementById('dynamicDashBody');
        if (!dashBody) return;

        if (filteredData.length === 0) {
            dashBody.innerHTML = `<div style="grid-column: 1/-1; padding: 50px; color: #95a5a6; text-align: center;">Không tìm thấy dữ liệu phù hợp.</div>`;
            return;
        }

        dashBody.innerHTML = filteredData.map(order => renderCard(order)).join('');
    }

    function renderCard(order) {
        const delStatus = checkDeliveryStatus(order.deliveryDate, order.timeWindow, order.updateTime, order.status);
        
        let badgeHtml = ''; let cardClass = '';
        if (delStatus.state === 'late') { badgeHtml = `<span class="badge-late" style="margin-top:0;">${delStatus.text}</span>`; cardClass = 'card-late'; } 
        else if (delStatus.state === 'early') { badgeHtml = `<span class="badge-early" style="margin-top:0;">${delStatus.text}</span>`; cardClass = 'card-early'; }

        let statusIcon = '⏳'; let statusColor = '#bdc3c7';
        if (order.status === 'Received') { statusIcon = '✅'; statusColor = '#2ecc71'; }
        if (order.status === 'Lack') { statusIcon = '❌'; statusColor = '#e74c3c'; }

        // Thiết kế lại thành dạng Card gọn gàng
        return `
            <div class="dash-card ${cardClass}" style="background-color: ${order.status === 'NOT FOUND' ? 'rgba(243, 156, 18, 0.1)' : ''};" onclick="window.openMxDetail('${order.odrno}')">
                <!-- Cột trái: Thông tin MX -->
                <div style="flex: 1; text-align: left;">
                    <div style="display: flex; align-items: center; gap: 10px; margin-bottom: 5px;">
                        <span class="mx-ordno" style="font-size: 18px; font-weight: bold; color: #f093fb; margin: 0;">📦 ${order.odrno}</span>
                        ${badgeHtml}
                    </div>
                    ${order.fitem ? `<div class="mx-fitem" style="font-size: 13px; color: #a8edea;">FITEM: ${order.fitem}</div>` : ''}
                    ${order.mw ? `<div class="mx-mw" style="font-size: 12px; color: #bdc3c7;">MW#: ${order.mw}</div>` : ''}
                </div>
                
                <!-- Cột phải: Trạng thái & Giờ -->
                <div style="text-align: right; min-width: 100px;">
                    <div style="font-size: 16px; font-weight: bold; color: ${statusColor}; margin-bottom: 5px;">${statusIcon} ${order.status}</div>
                    <div class="time-window" style="font-size: 12px; color: #a8edea; font-weight: bold;">⏰ ${order.timeWindow || '-'}</div>
                    ${order.deliveryDate ? `<div class="time-date" style="font-size: 11px; color: #bdc3c7; margin-top: 2px;">📅 ${order.deliveryDate}</div>` : ''}
                </div>
            </div>
        `;
    }

    // ==================== MỞ MODAL CHI TIẾT MX ====================
    window.openMxDetail = async function(odrno) {
        const modal = document.getElementById('modalMxDetail');
        document.getElementById('mxDetailOdrno').textContent = odrno;
        document.getElementById('mxItemsList').innerHTML = '<div class="loading-text">Đang tải Items...</div>';
        document.getElementById('mxPartsList').innerHTML = '<div class="loading-text">Đang tải Parts...</div>';
        
        const order = allDashboardData.find(o => o.odrno.toUpperCase() === odrno.toUpperCase());
        const oldNote = document.getElementById('mxDetailNoteBox');
        if (oldNote) oldNote.remove(); 
        
        if (order && order.note) {
            const mxInfoRow = modal.querySelector('.mx-info-row');
            if (mxInfoRow) {
                mxInfoRow.insertAdjacentHTML('beforeend', `
                    <div class="mx-info-item" id="mxDetailNoteBox" style="flex: 100%; margin-top: 15px; padding-top: 15px; border-top: 1px dashed rgba(255,255,255,0.2);">
                        <label style="color: #e74c3c; font-weight: bold; font-size: 14px;">📝 GHI CHÚ THIẾU HÀNG:</label>
                        <span style="font-size: 16px; color: #ffda79; display: block; margin-top: 5px; font-weight: bold;">${order.note}</span>
                    </div>
                `);
            }
        }

        modal.classList.add('active');

        try {
            const dateStr = formatDate(new Date(dateInput.value));
            const response = await fetch(`/mx-detail?odrno=${encodeURIComponent(odrno)}&date=${dateStr}`);
            if (!response.ok) throw new Error("Lỗi tải dữ liệu");
            const data = await response.json();
            
            document.getElementById('mxDetailTotalItems').textContent = data.items.reduce((s, i) => s + i.quantity, 0);
            
            if (data.items.length > 0) {
                document.getElementById('mxItemsList').innerHTML = data.items.map(i => `<div class="mx-item-card"><div class="item-code">📦 ${i.itemCode}</div><div class="item-qty">Qty: ${i.quantity}</div></div>`).join('');
            } else document.getElementById('mxItemsList').innerHTML = '<div class="mx-parts-empty">Không có Items</div>';
            
            if (data.parts.length > 0) {
                document.getElementById('mxPartsList').innerHTML = data.parts.map(p => `<div class="mx-part-row"><div class="mx-part-name">${p.partName}</div><div class="mx-part-qty">${p.quantity}</div></div>`).join('');
            } else document.getElementById('mxPartsList').innerHTML = '<div class="mx-parts-empty">Không có Parts</div>';
            
        } catch (error) {
            document.getElementById('mxItemsList').innerHTML = `<div class="mx-parts-empty">❌ Lỗi</div>`;
            document.getElementById('mxPartsList').innerHTML = `<div class="mx-parts-empty">Không tìm thấy chi tiết</div>`;
        }
    };

    const modalMx = document.getElementById('modalMxDetail');
    if(modalMx) {
        modalMx.addEventListener('click', (e) => {
            if (e.target.id === 'modalMxDetail') e.target.classList.remove('active');
        });
    }

    // ==================== CHARTS LOGIC ====================
    function updateCharts() {
        if (!filteredData) return;

        // 1. DOUGHNUT CHART
        let onTime = 0, late = 0, lack = 0, pending = 0;

        filteredData.forEach(order => {
            if (order.status === 'Pending') pending++;
            else if (order.status === 'Lack') lack++;
            else {
                const delStatus = checkDeliveryStatus(order.deliveryDate, order.timeWindow, order.updateTime, order.status);
                if (delStatus.state === 'late') late++;
                else onTime++; 
            }
        });

        const totalCenter = document.getElementById('totalMxCenter');
        if(totalCenter) totalCenter.textContent = filteredData.length;

        const ctxDoughnut = document.getElementById('doughnutChart');
        if (ctxDoughnut) {
            if (doughnutChartInstance) doughnutChartInstance.destroy();
            doughnutChartInstance = new Chart(ctxDoughnut.getContext('2d'), {
                type: 'doughnut',
                data: {
                    labels: ['Đã giao', 'Trễ giờ', 'Thiếu hàng', 'Chờ nhận'],
                    datasets: [{ data: [onTime, late, lack, pending], backgroundColor: ['#00b894', '#e74c3c', '#f39c12', '#34495e'], borderWidth: 0, cutout: '75%', borderRadius: 10 }]
                },
                options: { responsive: true, maintainAspectRatio: false, plugins: { legend: { position: 'bottom', labels: { color: '#bdc3c7', usePointStyle: true } } } }
            });
        }

        // 2. LINE/BAR CHART
        const chartTypeSelect = document.getElementById('chartTypeSelect');
        const chartType = chartTypeSelect ? chartTypeSelect.value : 'line';
        const chartCheckboxes = document.getElementById('chartCheckboxes');
        
        if (chartCheckboxes) chartCheckboxes.style.display = chartType === 'bar' ? 'flex' : 'none';

        const hourlyStats = {};

        allDashboardData.forEach(order => {
            let hourStr = "";
            if (order.status !== 'Pending' && order.updateTime) {
                const timePart = order.updateTime.split(' ')[1];
                if (timePart) hourStr = timePart.split(':')[0] + ':00';
            } else if (order.status === 'Pending' && order.timeWindow) {
                const startStr = order.timeWindow.split('-')[0].trim();
                if (startStr) hourStr = startStr.split(':')[0] + ':00';
            }

            if (hourStr) {
                if (!hourlyStats[hourStr]) hourlyStats[hourStr] = { actualScans: 0, onTime: 0, late: 0, pending: 0 };

                if (order.status === 'Pending') {
                    hourlyStats[hourStr].pending++;
                } else {
                    hourlyStats[hourStr].actualScans++;
                    const delStatus = checkDeliveryStatus(order.deliveryDate, order.timeWindow, order.updateTime, order.status);
                    if (delStatus.state === 'late') hourlyStats[hourStr].late++;
                    else hourlyStats[hourStr].onTime++;
                }
            }
        });

        let labels = Object.keys(hourlyStats).sort();
        if (chartType === 'line') {
            labels = [];
            for (let i = 0; i < 24; i++) labels.push(i.toString().padStart(2, '0') + ':00');
        }

        const dataActualScans = labels.map(h => hourlyStats[h] ? hourlyStats[h].actualScans : 0);
        const dataOnTime = labels.map(h => hourlyStats[h] ? hourlyStats[h].onTime : 0);
        const dataLate = labels.map(h => hourlyStats[h] ? hourlyStats[h].late : 0);
        const dataPending = labels.map(h => hourlyStats[h] ? hourlyStats[h].pending : 0);

        const ctxLine = document.getElementById('lineChart');
        if (ctxLine) {
            if (lineChartInstance) lineChartInstance.destroy();
            const ctx = ctxLine.getContext('2d');

            if (chartType === 'line') {
                let gradientFill = ctx.createLinearGradient(0, 0, 0, 250);
                gradientFill.addColorStop(0, 'rgba(102, 126, 234, 0.5)');
                gradientFill.addColorStop(1, 'rgba(102, 126, 234, 0.0)');

                lineChartInstance = new Chart(ctx, {
                    type: 'line',
                    data: {
                        labels: labels,
                        datasets: [{ label: 'Số lần quét', data: dataActualScans, borderColor: '#667eea', backgroundColor: gradientFill, borderWidth: 3, tension: 0.4, fill: true, pointBackgroundColor: '#fff', pointBorderColor: '#667eea', pointRadius: 3 }]
                    },
                    options: {
                        responsive: true, maintainAspectRatio: false,
                        plugins: { legend: { display: false } },
                        // ✨ SỰ KIỆN CLICK & HOVER
                        onClick: (event, elements, chart) => {
                            if (elements.length > 0) {
                                const clickedHour = chart.data.labels[elements[0].index];
                                selectedChartHour = (selectedChartHour === clickedHour) ? null : clickedHour;
                                if (timeWindowFilter) timeWindowFilter.value = ""; 
                                applyFilters();
                            }
                        },
                        onHover: (event, elements) => {
                            event.native.target.style.cursor = elements.length ? 'pointer' : 'default';
                        },
                        scales: {
                            y: { beginAtZero: true, grid: { color: 'rgba(255,255,255,0.05)' }, ticks: { color: '#bdc3c7', stepSize: 1 } },
                            x: { grid: { display: false }, ticks: { color: '#bdc3c7', autoSkip: false, maxRotation: 45, minRotation: 45 } } 
                        }

                    }
                });
            } 
            else if (chartType === 'bar') {
                const datasets = [];
                if (cbBarOnTime && cbBarOnTime.checked) datasets.push({ label: 'Đã giao', data: dataOnTime, backgroundColor: '#00b894', borderRadius: 4 });
                if (cbBarLate && cbBarLate.checked) datasets.push({ label: 'Giao trễ', data: dataLate, backgroundColor: '#e74c3c', borderRadius: 4 });
                if (cbBarPending && cbBarPending.checked) datasets.push({ label: 'Chờ nhận', data: dataPending, backgroundColor: '#7f8c8d', borderRadius: 4 });

                lineChartInstance = new Chart(ctx, {
                    type: 'bar',
                    data: { labels: labels.length > 0 ? labels : ['Chưa có dữ liệu'], datasets: datasets },
                    options: {
                        responsive: true, maintainAspectRatio: false,
                        interaction: {
                            mode: 'index',
                            intersect: false,
                        },
                        plugins: { legend: { display: false } },
                        tooltip: { 
                                padding: 10,
                                titleFont: { size: 14 },
                                bodyFont: { size: 13 }
                        }, 
                        // ✨ SỰ KIỆN CLICK & HOVER
                        onClick: (event, elements, chart) => {
                            if (elements.length > 0) {
                                const clickedHour = chart.data.labels[elements[0].index];
                                selectedChartHour = (selectedChartHour === clickedHour) ? null : clickedHour;
                                if (timeWindowFilter) timeWindowFilter.value = ""; 
                                applyFilters();
                            }
                        },
                        onHover: (event, elements) => {
                            event.native.target.style.cursor = elements.length ? 'pointer' : 'default';
                        },
                        scales: {
                            x: { stacked: true, grid: { display: false }, ticks: { color: '#bdc3c7', autoSkip: false, maxRotation: 45, minRotation: 45 } },
                            y: { stacked: true, beginAtZero: true, grid: { color: 'rgba(255,255,255,0.05)' }, ticks: { color: '#bdc3c7', stepSize: 1 } }
                        }
                    }
                });
            }
        }
    }

    const chartTypeSelect = document.getElementById('chartTypeSelect');
    if (chartTypeSelect && !chartTypeSelect.hasAttribute('data-listener')) {
        chartTypeSelect.addEventListener('change', updateCharts);
        chartTypeSelect.setAttribute('data-listener', 'true');
    }

    ['cbBarOnTime', 'cbBarLate', 'cbBarPending'].forEach(id => {
        const cb = document.getElementById(id);
        if (cb && !cb.hasAttribute('data-listener')) {
            cb.addEventListener('change', applyFilters); 
            cb.setAttribute('data-listener', 'true');
        }
    });


    // ==================== EVENT LISTENERS ====================
    dateInput.addEventListener('change', fetchDashboardData);
    searchInput.addEventListener('input', applyFilters);
    timeWindowFilter.addEventListener('change', () => {
        selectedChartHour = null; // Hủy lọc biểu đồ khi dùng Dropdown
        applyFilters();
    });
    
    // Xử lý đổi số cột hiển thị
    const colCountSelect = document.getElementById('colCountSelect');
    const dynamicDashBody = document.getElementById('dynamicDashBody');
    
    if (colCountSelect) {
        // Khôi phục số cột đã lưu
        const savedCols = localStorage.getItem('dashColCount') || '2';
        colCountSelect.value = savedCols;
        if (dynamicDashBody) {
            dynamicDashBody.className = `dynamic-grid-container grid-cols-${savedCols}`;
        }

        colCountSelect.addEventListener('change', (e) => {
            const cols = e.target.value;
            localStorage.setItem('dashColCount', cols);
            if (dynamicDashBody) {
                dynamicDashBody.className = `dynamic-grid-container grid-cols-${cols}`;
            }
        });
    }

    // ==================== SIGNALR FOR DASHBOARD ====================
    let dashboardSignalR = null;

    async function initDashboardSignalR() {
        try {
            dashboardSignalR = new signalR.HubConnectionBuilder().withUrl("/orderHub").withAutomaticReconnect().build();
            dashboardSignalR.onreconnecting(() => { const ind = document.getElementById('realtimeIndicator'); if (ind) { ind.innerHTML = '<div class="dot" style="background: #f39c12;"></div> Reconnecting...'; ind.style.color = '#f39c12'; } });
            dashboardSignalR.onreconnected(() => { const ind = document.getElementById('realtimeIndicator'); if (ind) { ind.innerHTML = '<div class="dot"></div> Real-time ✓'; ind.style.color = '#2ecc71'; } fetchDashboardData(); });
            dashboardSignalR.onclose(() => { const ind = document.getElementById('realtimeIndicator'); if (ind) { ind.innerHTML = '<div class="dot" style="background: #e74c3c;"></div> Offline'; ind.style.color = '#e74c3c'; } setTimeout(initDashboardSignalR, 5000); });
            
            dashboardSignalR.on("OrderUpdated", () => { fetchDashboardData(); });
            await dashboardSignalR.start();
            const ind = document.getElementById('realtimeIndicator');
            if (ind) { ind.innerHTML = '<div class="dot"></div> Real-time ✓'; ind.style.color = '#2ecc71'; }
        } catch (err) { setTimeout(initDashboardSignalR, 5000); }
    }

    fetchDashboardData().then(() => { if (typeof signalR !== 'undefined') initDashboardSignalR(); });
});
