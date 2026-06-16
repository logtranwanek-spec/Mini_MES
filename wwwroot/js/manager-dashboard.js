document.addEventListener('DOMContentLoaded', () => {
    // ==================== DOM ELEMENTS ====================
    const horizontalControls = document.getElementById('horizontal-controls');
    const floatingToolbar = document.getElementById('floating-toolbar');
    const filterGroupWrapper = document.getElementById('filter-group-wrapper');
    const dashHeader = document.querySelector('.dash-header');
    
    // ✅ SỬA LẠI TÊN BIẾN CHO ĐÚNG ID TRONG HTML
    const btnSyncHistorical = document.getElementById('btnSyncHistorical');
    
    const dateInput = document.getElementById('dashDate');
    const tableBody = document.getElementById('dashboard-body');
    const loadingState = document.getElementById('loading-state');
    
    const statTotal = document.getElementById('statTotal');
    const statDone = document.getElementById('statDone');
    const statAlert = document.getElementById('statAlert');
    const statInProgress = document.getElementById('statInProgress');

    const filterDone = document.getElementById('filterDone');
    const filterInProgress = document.getElementById('filterInProgress');
    const filterAlert = document.getElementById('filterAlert');
    const filterPending = document.getElementById('filterPending');

    const modal = document.getElementById('modalDrillDown');
    const closeBtn = document.getElementById('closeDrillDownModal');
    const drillDownGroupName = document.getElementById('drillDownGroupName');
    const drillDownMxName = document.getElementById('drillDownMxName');
    const drillDownList = document.getElementById('drillDownList');
    
    const chartTypeSelect = document.getElementById('chartTypeSelect');
    const chartCheckboxes = document.getElementById('chartCheckboxes');
    const cbBarOnTime = document.getElementById('cbBarOnTime');
    const cbBarLate = document.getElementById('cbBarLate');
    const cbBarPending = document.getElementById('cbBarPending');

    let topChartInstance = null;
    let bottomDoughnutInstance = null;
    
    let allData = [];

    // ==================== MORPHING TOOLBAR LOGIC ====================
    let isFloating = false;
    const headerHeight = dashHeader ? dashHeader.offsetHeight : 80;

    window.addEventListener('scroll', () => {
        if (!horizontalControls || !floatingToolbar || !filterGroupWrapper) return;
        const rect = horizontalControls.getBoundingClientRect();
        if (rect.top <= headerHeight && !isFloating) {
            isFloating = true;
            document.body.classList.add('toolbar-is-floating');
            floatingToolbar.appendChild(filterGroupWrapper);
        } else if (rect.top > headerHeight && isFloating) {
            isFloating = false;
            document.body.classList.remove('toolbar-is-floating');
            horizontalControls.prepend(filterGroupWrapper);
        }
    });

    // ==================== LOGIC CHÍNH ====================

    async function fetchDashboardData() {
        const selectedDate = dateInput.value;
        if (!selectedDate) return;
        loadingState.style.display = 'block';
        tableBody.innerHTML = '';
        try {
            const response = await fetch(`/api/manager-dashboard?date=${selectedDate}`);
            if (!response.ok) {
                try {
                    const errorJson = await response.json();
                    throw new Error(errorJson.detail || errorJson.title || 'Lỗi không xác định từ server');
                } catch {
                    throw new Error(await response.text());
                }
            }
            allData = await response.json();
            applyFiltersAndRender();
        } catch (error) {
            console.error("Lỗi chi tiết khi fetch:", error);
            tableBody.innerHTML = `<tr><td colspan="9" style="text-align: center; padding: 40px; color: #e74c3c;">Lỗi tải dữ liệu: ${error.message}</td></tr>`;
        } finally {
            loadingState.style.display = 'none';
        }
    }

    function applyFiltersAndRender() {
        if (!allData) return;
        const filters = {
            Done: filterDone.checked,
            'In Progress': filterInProgress.checked,
            Alert: filterAlert.checked,
            Pending: filterPending.checked
        };
        const filteredData = allData.filter(item => filters[item.status]);
        
        renderTable(filteredData);
        updateStats(allData); 
        updateCharts(filteredData);
    }

    function renderTable(data) {
        if (!data || data.length === 0) {
            tableBody.innerHTML = `<tr><td colspan="9" style="text-align: center; padding: 40px;">${allData.length > 0 ? 'Không có MX nào khớp' : 'Không có dữ liệu'}</td></tr>`;
            return;
        }
        tableBody.innerHTML = data.map(item => `
            <tr data-status="${item.status}">
                <td class="mx-cell"><div>${item.mx}</div><div class="fg-item">${item.fgItem}</div></td>
                <td>${item.ex}:00</td>
                <td>${item.ltUphSp}:00</td>
                <td class="status-col">${renderStatusCell(item.mx, 'Blow Fill', item.groups['Blow Fill'])}</td>
                <td class="status-col">${renderStatusCell(item.mx, 'Glueline', item.groups['Glueline'])}</td>
                <td class="status-col">${renderStatusCell(item.mx, 'HandGlue', item.groups['HandGlue'])}</td>
                <td class="status-col">${renderStatusCell(item.mx, 'Handfill', item.groups['Handfill'])}</td>
                <td class="status-col">${renderStatusCell(item.mx, 'Cushion', item.groups['Cushion'])}</td>
                <td class="status-overall status-overall-${item.status.toLowerCase().replace(' ', '')}">${item.status}</td>
            </tr>
        `).join('');
    }

    function renderStatusCell(mx, groupName, group) {
        if (!group) return '<div class="status-cell status-cell-na" title="Không có dữ liệu"></div>';
        const status = group.status;
        const tooltip = group.tooltip;
        let className = 'status-cell';
        if (status === 'green') className += ' status-cell-green';
        else if (status === 'red') className += ' status-cell-red';
        else if (status === 'na') className += ' status-cell-na';
        return `<div class="${className}" title="${tooltip}" onclick="showDrillDown('${mx}', '${groupName}')"></div>`;
    }

    function updateStats(data) {
        if (!data) return;
        statTotal.textContent = data.length;
        statDone.textContent = data.filter(item => item.status === 'Done').length;
        statAlert.textContent = data.filter(item => item.status === 'Alert').length;
        statInProgress.textContent = data.filter(item => item.status === 'In Progress').length;
    }

    function updateCharts(data) {
        if (!data) return;
        const doughnutCanvas = document.getElementById('bottomDoughnut');
        const topChartCanvas = document.getElementById('topChart');
        if (!doughnutCanvas || !topChartCanvas) return;
        const stats = {
            total: data.length,
            done: data.filter(item => item.status === 'Done').length,
            alert: data.filter(item => item.status === 'Alert').length,
            inProgress: data.filter(item => item.status === 'In Progress').length,
            pending: data.filter(item => item.status === 'Pending').length
        };
        const totalCenter = document.getElementById('totalMxCenter');
        if (totalCenter) totalCenter.textContent = stats.total;
        
        if (bottomDoughnutInstance) bottomDoughnutInstance.destroy();
        bottomDoughnutInstance = new Chart(doughnutCanvas.getContext('2d'), {
            type: 'doughnut',
            data: {
                labels: ['Hoàn thành', 'Cảnh báo', 'Đang làm', 'Chờ'],
                datasets: [{
                    data: [stats.done, stats.alert, stats.inProgress, stats.pending],
                    backgroundColor: ['#27ae60', '#e74c3c', '#3498db', '#7f8c8d'],
                    borderWidth: 0, cutout: '75%', borderRadius: 10
                }]
            },
            options: {
                responsive: true, maintainAspectRatio: false,
                plugins: { legend: { position: 'bottom', labels: { color: '#bdc3c7', usePointStyle: true, font: { size: 12 } } } }
            }
        });

        const hourlyStats = {};
        for (let i = 0; i < 24; i++) {
            hourlyStats[i] = { onTime: 0, late: 0, pending: 0, total: 0 };
        }
        data.forEach(item => {
            const hour = item.ex;
            if (typeof hour !== 'number' || isNaN(hour) || hour < 0 || hour > 23) {
                console.error("Dữ liệu MX bị lỗi, giờ 'ex' không hợp lệ:", item);
                return;
            }
            hourlyStats[hour].total++;
            if (item.status === 'Done') hourlyStats[hour].onTime++;
            else if (item.status === 'Alert') hourlyStats[hour].late++;
            else hourlyStats[hour].pending++;
        });

        const labels = Object.keys(hourlyStats).map(h => `${h.toString().padStart(2, '0')}:00`);
        const chartType = chartTypeSelect.value;
        if (chartCheckboxes) chartCheckboxes.style.display = chartType === 'bar' ? 'flex' : 'none';
        
        if (topChartInstance) topChartInstance.destroy();
        const ctx = topChartCanvas.getContext('2d');
        if (chartType === 'line') {
            const gradientFill = ctx.createLinearGradient(0, 0, 0, topChartCanvas.height);
            gradientFill.addColorStop(0, 'rgba(102, 126, 234, 0.6)');
            gradientFill.addColorStop(1, 'rgba(102, 126, 234, 0)');
            topChartInstance = new Chart(ctx, {
                type: 'line',
                data: {
                    labels: labels,
                    datasets: [{
                        label: 'Số MX',
                        data: labels.map(l => hourlyStats[parseInt(l)].total),
                        borderColor: '#667eea', backgroundColor: gradientFill,
                        borderWidth: 3, tension: 0.4, fill: true
                    }]
                },
                options: getChartOptions()
            });
        } else {
            const datasets = [];
            if (cbBarOnTime.checked) datasets.push({ label: 'Hoàn thành', data: labels.map(l => hourlyStats[parseInt(l)].onTime), backgroundColor: '#27ae60' });
            if (cbBarLate.checked) datasets.push({ label: 'Cảnh báo', data: labels.map(l => hourlyStats[parseInt(l)].late), backgroundColor: '#e74c3c' });
            if (cbBarPending.checked) datasets.push({ label: 'Đang làm/Chờ', data: labels.map(l => hourlyStats[parseInt(l)].pending), backgroundColor: '#7f8c8d' });
            topChartInstance = new Chart(ctx, {
                type: 'bar',
                data: { labels: labels, datasets: datasets },
                options: getChartOptions(true)
            });
        }
    }

    function getChartOptions(isBar = false) {
        return {
            responsive: true, maintainAspectRatio: false,
            plugins: { legend: { display: false } },
            scales: {
                y: {
                    beginAtZero: true, stacked: isBar,
                    grid: { color: 'rgba(255,255,255,0.05)' },
                    ticks: { color: '#bdc3c7', stepSize: 2 }
                },
                x: {
                    stacked: isBar, grid: { display: false },
                    ticks: { color: '#bdc3c7', autoSkip: true, maxRotation: 45, minRotation: 45 }
                }
            }
        };
    }
    
    // ==================== UI & EVENT LISTENERS ====================
    
    window.showDrillDown = (mx, groupName) => {
        const mxData = allData.find(item => item.mx === mx);
        if (!mxData || !mxData.groups) return;
        const groupData = mxData.groups[groupName];
        if (!groupData || !groupData.details) return;
        drillDownGroupName.textContent = groupName;
        drillDownMxName.textContent = mx;
        if (groupData.details.length === 0) {
            drillDownList.innerHTML = '<div class="drill-down-empty">Không có MO nào trong nhóm này.</div>';
        } else {
            drillDownList.innerHTML = groupData.details.map(mo => `
                <div class="drill-down-item">
                    <div class="drill-down-mo">${mo.mo}</div>
                    <div class="drill-down-wc">${mo.wc}</div>
                    <div class="drill-down-progress">${mo.progress}</div>
                    <div class="drill-down-status status-text-${mo.status}">${mo.status}</div>
                </div>
            `).join('');
        }
        modal.classList.add('active');
    };
    if(closeBtn) closeBtn.onclick = () => modal.classList.remove('active');
    if(modal) modal.onclick = (e) => {
        if (e.target === modal) modal.classList.remove('active');
    };

    dateInput.addEventListener('change', fetchDashboardData);
    [filterDone, filterInProgress, filterAlert, filterPending].forEach(cb => {
        if (cb) cb.addEventListener('change', applyFiltersAndRender);
    });
    [chartTypeSelect, cbBarOnTime, cbBarLate, cbBarPending].forEach(el => {
        if (el) el.addEventListener('change', applyFiltersAndRender);
    });

    // ✅ SỬA LẠI TÊN BIẾN VÀ LOGIC NÚT
    if (btnSyncHistorical) {
        btnSyncHistorical.addEventListener('click', async () => {
            const selectedDate = dateInput.value;
            if (!selectedDate) {
                alert("Vui lòng chọn ngày.");
                return;
            }
            if (!confirm(`Đồng bộ lại tất cả lịch sử quét cho các MO có kế hoạch ngày ${selectedDate} nhưng chưa có dữ liệu?`)) return;

            btnSyncHistorical.disabled = true;
            btnSyncHistorical.textContent = 'Đang đồng bộ...';

            try {
                const res = await fetch(`/api/debug/sync-historical?date=${selectedDate}`, { method: 'POST' });
                if (!res.ok) throw new Error(await res.text() || 'Đồng bộ thất bại');
                
                const data = await res.json();
                alert(`Đồng bộ thành công!\nCập nhật: ${data.updatedPairs} cặp MO/WC\nLog mới: ${data.newLogs}`);
                await fetchDashboardData();
            } catch (err) {
                alert('Lỗi đồng bộ: ' + err.message);
            } finally {
                btnSyncHistorical.disabled = false;
                btnSyncHistorical.textContent = '🔍 Đồng bộ Quét Cũ';
            }
        });
    }

    if (floatingToolbar) {
        let isDragging = false, offsetX = 0, offsetY = 0;
        const rect = floatingToolbar.getBoundingClientRect();
        floatingToolbar.style.left = rect.left + 'px';
        floatingToolbar.style.top = rect.top + 'px';
        floatingToolbar.style.transform = 'none';
        floatingToolbar.style.position = 'fixed';
        floatingToolbar.style.cursor = 'grab';
        floatingToolbar.style.userSelect = 'none';
        function onMouseDown(e) {
            if (e.target.tagName === 'INPUT' || e.target.tagName === 'LABEL' || e.target.classList.contains('filter-text')) return;
            isDragging = true;
            floatingToolbar.style.cursor = 'grabbing';
            const rect = floatingToolbar.getBoundingClientRect();
            offsetX = e.clientX - rect.left;
            offsetY = e.clientY - rect.top;
            document.addEventListener('mousemove', onMouseMove);
            document.addEventListener('mouseup', onMouseUp);
            e.preventDefault();
        }
        function onMouseMove(e) {
            if (!isDragging) return;
            floatingToolbar.style.left = (e.clientX - offsetX) + 'px';
            floatingToolbar.style.top = (e.clientY - offsetY) + 'px';
        }
        function onMouseUp() {
            if (!isDragging) return;
            isDragging = false;
            floatingToolbar.style.cursor = 'grab';
            document.removeEventListener('mousemove', onMouseMove);
            document.removeEventListener('mouseup', onMouseUp);
        }
        floatingToolbar.addEventListener('mousedown', onMouseDown);
    }
    
    // ==================== INITIAL LOAD ====================
    dateInput.valueAsDate = new Date();
    fetchDashboardData();
    setInterval(fetchDashboardData, 5 * 60 * 1000);

    // ==================== SCROLL TO TOP BUTTON LOGIC ====================
    const scrollToTopBtn = document.getElementById('scrollToTopBtn');
    if (scrollToTopBtn) {
        // Hiện/ẩn nút khi cuộn
        window.addEventListener('scroll', () => {
            if (document.body.scrollTop > 100 || document.documentElement.scrollTop > 100) {
                scrollToTopBtn.style.display = "block";
            } else {
                scrollToTopBtn.style.display = "none";
            }
        });

        // Xử lý sự kiện click
        scrollToTopBtn.addEventListener('click', () => {
            window.scrollTo({
                top: 0,
                behavior: 'smooth'
            });
        });
    }
});
