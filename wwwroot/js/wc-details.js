document.addEventListener('DOMContentLoaded', () => {
    const wcFilterContainer = document.getElementById('wcFilterContainer');
    const timeFilterContainer = document.getElementById('timeFilterContainer');
    const timelineView = document.getElementById('timelineView');
    
    let allTrackingData = [];
    let allMoData = [];
    let progressData = {};
    let selectedWc = 'all';

    // Hàm chuẩn hóa WC (giống tracking.js)
    function normalizeWcForAs400(wc) {
        if (!wc) return wc;
        wc = wc.trim().toUpperCase();
        const underscoreIndex = wc.indexOf('_');
        if (underscoreIndex > 0) {
            return wc.substring(0, underscoreIndex);
        }
        return wc;
    }

    // Hàm hiển thị modal chi tiết (copy từ tracking.js)
    window.showMoScanDetail = async function (mo, plannedQty, leadtime, wcDetail) {
        const modal = document.getElementById('modalMoScanDetail');
        if (!modal) {
            alert('Lỗi: Không tìm thấy HTML của modal chi tiết.');
            return;
        }
        
        const titleEl = document.getElementById('moDetailTitle');
        const plannedQtyEl = document.getElementById('moPlannedQty');
        const scannedQtyEl = document.getElementById('moScannedQty');
        const leadtimeEl = document.getElementById('moLeadtime');
        const moMxEl = document.getElementById('moMx');
        const historyListEl = document.getElementById('moScanHistoryList');

        // Tìm MX chứa MO này từ allTrackingData
        let foundMx = '-';
        for (const mxData of allTrackingData) {
            if (mxData.steps && mxData.steps.some(step => step.mo && step.mo.toUpperCase() === mo.toUpperCase())) {
                foundMx = mxData.mx || '-';
                break;
            }
        }

        if(titleEl) titleEl.textContent = mo;
        if(moMxEl) moMxEl.textContent = foundMx;
        if(plannedQtyEl) plannedQtyEl.textContent = `${plannedQty} kits`;
        if(leadtimeEl) leadtimeEl.textContent = leadtime || '-';

        if(historyListEl) historyListEl.innerHTML = '<p style="text-align: center; color: #a8edea;">⏳ Đang tải lịch sử quét...</p>';
        modal.classList.add('active');

        try {
            const response = await fetch(`/api/tracking/mo-scan-detail?mo=${encodeURIComponent(mo)}&workCenter=${encodeURIComponent(wcDetail)}`);
            if (!response.ok) throw new Error("Không thể tải dữ liệu scan");
            const data = await response.json();

            if(scannedQtyEl) scannedQtyEl.textContent = `${data.totalScannedQty} kits`;

            if (!data.scans || data.scans.length === 0) {
                if(historyListEl) historyListEl.innerHTML = `<div class="no-scan-data"><strong>Chưa có Kit nào được quét cho Work Center này.</strong></div>`;
                return;
            }

            if(historyListEl) {
                historyListEl.innerHTML = data.scans.map((scan, index) => {
                    const scanDate = new Date(scan.scanTime);
                    const formattedTime = scanDate.toLocaleString('vi-VN', {
                        hour: '2-digit', minute: '2-digit', second: '2-digit',
                        day: '2-digit', month: '2-digit', year: 'numeric'
                    });
                    return `
                        <div class="scan-history-item">
                            <span class="scan-kit-number">${index + 1}️⃣ Lần quét #${index + 1}</span>
                            <span class="scan-wc">📋 ${scan.workCenter}</span>
                            <span class="scan-time">⏱️ ${formattedTime}</span>
                            <span class="scan-by">👤 ${scan.scannedBy || 'N/A'}</span>
                        </div>
                    `;
                }).join('');
            }
        } catch (error) {
            console.error("Lỗi tải chi tiết:", error);
            if(historyListEl) historyListEl.innerHTML = `<div class="no-scan-data"><strong>❌ Lỗi tải dữ liệu</strong></div>`;
        }
    };

    async function initialize() {
        const date = new Date().toISOString().split('T')[0];
        await loadData(date);
        render();
    }

    async function loadData(date) {
        try {
            const [journeyRes, progressRes] = await Promise.all([
                fetch(`/api/tracking/journey?date=${date}`),
                fetch(`/api/tracking/kit-progress?date=${date}`)
            ]);
            
            allTrackingData = await journeyRes.json();
            const progressRawData = await progressRes.json();
            
            progressData = {};
            progressRawData.forEach(p => { 
                const key = `${p.mo.toUpperCase()}|${p.workCenter.toUpperCase()}`;
                progressData[key] = p; 
            });

            // Tạo danh sách phẳng allMoData từ allTrackingData
            allMoData = [];
            allTrackingData.forEach(mx => {
                mx.steps.forEach(step => {
                    allMoData.push({
                        mx: mx.mx,
                        mo: step.mo,
                        wc: step.workCenter,
                        qty: step.qty,
                        leadtime: step.leadtime,
                        fgItem: step.fgItem
                    });
                });
            });

        } catch (error) {
            console.error("Lỗi tải dữ liệu:", error);
            timelineView.innerHTML = '<p style="color: red; text-align: center;">Lỗi tải dữ liệu.</p>';
        }
    }

    function render() {
        renderWcFilters();
        renderTimeFilters();
        renderTimeline();
    }

    function renderWcFilters() {
        const workCenters = [...new Set(allMoData.map(mo => mo.wc))].sort();
        wcFilterContainer.innerHTML = '';
        
        const allBtn = createFilterButton('Tất cả WC', 'all', selectedWc === 'all');
        wcFilterContainer.appendChild(allBtn);
        workCenters.forEach(wc => wcFilterContainer.appendChild(createFilterButton(wc, wc, selectedWc === wc)));

        wcFilterContainer.querySelectorAll('.filter-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                selectedWc = btn.dataset.value;
                render();
            });
        });
    }

    function renderTimeFilters() {
        let mosForTimeFilter = (selectedWc === 'all') ? allMoData : allMoData.filter(mo => mo.wc === selectedWc);
        const leadtimes = [...new Set(mosForTimeFilter.map(mo => mo.leadtime))].sort();
        timeFilterContainer.innerHTML = '';

        leadtimes.forEach(lt => {
            if (!lt) return;
            const btn = createFilterButton(lt, lt, false);
            btn.addEventListener('click', () => {
                scrollToLeadtime(lt);
            });
            timeFilterContainer.appendChild(btn);
        });
    }

    function renderTimeline() {
        let filteredMos = (selectedWc === 'all') ? allMoData : allMoData.filter(mo => mo.wc === selectedWc);

        if (filteredMos.length === 0) {
            timelineView.innerHTML = '<p style="text-align: center; color: #95a5a6;">Không có MO nào phù hợp.</p>';
            return;
        }

        const mosByLeadtime = {};
        filteredMos.forEach(mo => {
            const lt = mo.leadtime || 'Chưa xác định';
            if (!mosByLeadtime[lt]) mosByLeadtime[lt] = [];
            mosByLeadtime[lt].push(mo);
        });

        const sortedLeadtimes = Object.keys(mosByLeadtime).sort();
        timelineView.innerHTML = sortedLeadtimes.map(lt => {
            const moCardsHtml = mosByLeadtime[lt].map(mo => {
                const baseWc = normalizeWcForAs400(mo.wc);
                const progressKey = `${mo.mo.toUpperCase()}|${baseWc.toUpperCase()}`;
                const progress = progressData[progressKey] || { status: 'pending', progress: `0/${mo.qty}` };
                const plannedQty = parseInt(mo.qty) || 0;
                
                return `
                    <div class="mo-detail-card status-${progress.status}" onclick="showMoScanDetail('${mo.mo}', ${plannedQty}, '${mo.leadtime}', '${mo.wc}')">
                        <div class="card-header">${mo.mx}</div>
                        <div class="card-body">${mo.mo}, ${progress.progress}</div>
                        <div class="card-footer">${mo.leadtime}</div>
                    </div>
                `;
            }).join('');

            return `
                <div class="timeline-row" id="lt-${lt.replace(/[^a-zA-Z0-9]/g, '')}">
                    <div class="timeline-label">${lt}</div>
                    <div class="timeline-mo-container">${moCardsHtml}</div>
                </div>
            `;
        }).join('');
    }

    function createFilterButton(text, value, isActive) {
        const btn = document.createElement('button');
        btn.className = 'filter-btn' + (isActive ? ' active' : '');
        btn.dataset.value = value;
        btn.textContent = text;
        return btn;
    }

    function scrollToLeadtime(leadtime) {
        const elementId = `lt-${leadtime.replace(/[^a-zA-Z0-9]/g, '')}`;
        const targetElement = document.getElementById(elementId);
        if (targetElement) {
            targetElement.scrollIntoView({ behavior: 'smooth', block: 'center' });
            targetElement.style.transition = 'background-color 0.5s';
            targetElement.style.backgroundColor = 'rgba(52, 152, 219, 0.1)';
            setTimeout(() => {
                targetElement.style.backgroundColor = '';
            }, 2000);
        }
    }

    initialize();
});
