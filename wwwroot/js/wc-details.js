document.addEventListener('DOMContentLoaded', () => {
    const wcFilterContainer = document.getElementById('wcFilterContainer');
    const timeFilterContainer = document.getElementById('timeFilterContainer');
    const timelineView = document.getElementById('timelineView');
    
    let allMoData = [];
    let progressData = {};
    let selectedWc = 'all';

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
            const journeyData = await journeyRes.json();
            const progressRawData = await progressRes.json();
            
            progressData = {};
            progressRawData.forEach(p => { progressData[p.mo] = p; });

            allMoData = [];
            journeyData.forEach(mx => {
                mx.steps.forEach(step => {
                    allMoData.push({
                        mx: mx.mx, mo: step.mo, wc: step.workCenter, qty: step.qty,
                        leadtime: step.leadtime, status: progressData[step.mo]?.status || 'pending'
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
                selectedWc = btn.dataset.wc;
                render(); // Vẽ lại toàn bộ
            });
        });
    }

    function renderTimeFilters() {
        let mosForTimeFilter = (selectedWc === 'all') ? allMoData : allMoData.filter(mo => mo.wc === selectedWc);
        const leadtimes = [...new Set(mosForTimeFilter.map(mo => mo.leadtime))].sort();
        timeFilterContainer.innerHTML = '';

        leadtimes.forEach(lt => {
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

        // Nhóm các MO theo Leadtime
        const mosByLeadtime = {};
        filteredMos.forEach(mo => {
            const lt = mo.leadtime || 'Chưa xác định';
            if (!mosByLeadtime[lt]) mosByLeadtime[lt] = [];
            mosByLeadtime[lt].push(mo);
        });

        const sortedLeadtimes = Object.keys(mosByLeadtime).sort();
        timelineView.innerHTML = sortedLeadtimes.map(lt => {
            const moCardsHtml = mosByLeadtime[lt].map(mo => {
                const progress = progressData[mo.mo] || { status: 'pending', progress: `0/${mo.qty}` };
                return `
                    <div class="mo-detail-card status-${progress.status}">
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
        btn.dataset.wc = value; // Dùng chung data-wc
        btn.textContent = text;
        return btn;
    }

    function scrollToLeadtime(leadtime) {
        const elementId = `lt-${leadtime.replace(/[^a-zA-Z0-9]/g, '')}`;
        const targetElement = document.getElementById(elementId);
        if (targetElement) {
            targetElement.scrollIntoView({ behavior: 'smooth', block: 'center' });
            // Thêm hiệu ứng highlight
            targetElement.style.transition = 'background-color 0.5s';
            targetElement.style.backgroundColor = 'rgba(52, 152, 219, 0.1)';
            setTimeout(() => {
                targetElement.style.backgroundColor = '';
            }, 2000);
        }
    }

    initialize();
});
