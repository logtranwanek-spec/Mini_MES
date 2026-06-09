document.addEventListener('DOMContentLoaded', () => {
    const dateInput = document.getElementById('trackDate');
    const searchInput = document.getElementById('trackSearch');
    const viewToggleBtn = document.getElementById('viewToggleBtn');
    const resultsContainer = document.getElementById('resultsContainer');

    const btnFilterInProgress = document.getElementById('btnFilterInProgress');
    const btnSelectWorkCenter = document.getElementById('btnSelectWorkCenter');
    const btnFilterLate = document.getElementById('btnFilterLate');

    const wcSearchInput = document.getElementById('wcSearchInput');
    const btnSelectAllWC = document.getElementById('btnSelectAllWC');
    const btnDeselectAllWC = document.getElementById('btnDeselectAllWC');
    const btnApplyWcFilter = document.getElementById('btnApplyWcFilter');

    const btnShowProgressSummary = document.getElementById('btnShowProgressSummary');
    const btnDetails = document.getElementById('btnWcDetails');

    let allTrackingData = [];
    let isWcView = false;
    let progressData = {};
    let selectedWorkCenters = new Set();
    let allWorkCentersData = []; // lưu đầy đủ danh sách WC để filter

    const today = new Date();
    dateInput.value = today.toISOString().split('T')[0];

    // Ẩn/hiện 2 nút theo chế độ
    function updateTopButtonsVisibility() {
        if (isWcView) { // HIỆN NÚT
            if (btnFilterInProgress) btnFilterInProgress.style.display = 'flex';
            if (btnFilterLate) btnFilterLate.style.display = 'flex';
            if (btnSelectWorkCenter) btnSelectWorkCenter.style.display = 'flex';
            if (btnShowProgressSummary) btnShowProgressSummary.style.display = 'block';
            if (btnDetails) btnDetails.style.display = 'inline-block';
        } else { // ẨN NÚT
            if (btnFilterInProgress) btnFilterInProgress.style.display = 'none';
            if (btnFilterLate) btnFilterLate.style.display = 'none';
            if (btnSelectWorkCenter) btnSelectWorkCenter.style.display = 'none';
            if (btnShowProgressSummary) btnShowProgressSummary.style.display = 'none';
            if (btnDetails) btnDetails.style.display = 'none';
        }
    }

    function normalizeWcForAs400(wc) {
        if (!wc) return wc;
        wc = wc.trim().toUpperCase();
        const underscoreIndex = wc.indexOf('_');
        if (underscoreIndex > 0) {
            return wc.substring(0, underscoreIndex);
        }
        return wc;
    }

    async function loadTrackingData() {
        const selectedDate = dateInput.value;
        if (!selectedDate) return;
        resultsContainer.innerHTML = '<p style="text-align: center; color: #a8edea; padding: 50px;">⏳ Đang tải dữ liệu...</p>';
        try {
            const response = await fetch(`/api/tracking/journey?date=${selectedDate}`);
            if (!response.ok) throw new Error(await response.text());
            allTrackingData = await response.json();
            renderTrackingData();
        } catch (error) {
            resultsContainer.innerHTML = `<p style="text-align: center; color: #e74c3c; padding: 50px;">❌ Lỗi: ${error.message}</p>`;
        }
    }

    // ==================== HÀM TẢI TIẾN ĐỘ QUÉT KIT (FAKE / REAL) ====================
    async function loadKitProgress() {
        const selectedDate = dateInput.value;
        if (!selectedDate) return;

        try {
            console.log("🔄 Đang tải tiến độ quét Kit...");
            const response = await fetch(`/api/tracking/kit-progress?date=${selectedDate}`); // ← ĐÃ SỬA
            if (!response.ok) throw new Error("Không thể tải tiến độ");
            const data = await response.json();

            progressData = {};
            data.forEach(item => {
                const moKey  = item.mo ? item.mo.toUpperCase() : "";
                const wcBase = item.baseWorkCenter ? item.baseWorkCenter.toUpperCase() : ""; // ✅ DÙNG TRƯỜNG MỚI
                const key    = `${moKey}|${wcBase}`;
                progressData[key] = {
                    status: item.status,
                    progress: item.progress,
                    currentQty: item.currentQty,
                    plannedQty: item.plannedQty,
                    workCenter: item.workCenter // Vẫn lưu WC chi tiết nếu cần
                };
            });

            console.log("✅ Đã tải tiến độ:", progressData);
            renderTrackingData();
        } catch (error) {
            console.error("❌ Lỗi tải tiến độ:", error);
        }
    }

    function renderTrackingData() {
        if (isWcView) renderByWorkCenter();
        else renderByMx();
    }

    // ==================== CHẾ ĐỘ XEM THEO MX ====================
    function renderByMx() {
        resultsContainer.className = 'tracking-results';
        if (allTrackingData.length === 0) {
            resultsContainer.innerHTML = '<p style="text-align: center; color: #f39c12; padding: 50px;">⚠️ Không có dữ liệu cho ngày này.</p>';
            return;
        }
        const groupedBySteps = {};
        allTrackingData.forEach(mxData => {
            if (!mxData.steps || !Array.isArray(mxData.steps)) return;
            const signature = mxData.steps.map(s => `${s.workCenter}-${s.mo}`).join('|');
            if (!groupedBySteps[signature]) groupedBySteps[signature] = { mxList: [], steps: mxData.steps };
            groupedBySteps[signature].mxList.push(mxData.mx);
        });

        resultsContainer.innerHTML = Object.values(groupedBySteps).map(group => {
            const fgItem = group.steps.length > 0 ? group.steps[0].fgItem : '';

            const mxHtml = group.mxList
                .map(mx => `
                    <div class="mx-card" data-mx="${mx.toLowerCase()}">
                        <div class="mx-card-mx">${mx}</div>
                        ${fgItem ? `<div class="mx-card-fgitem">${fgItem}</div>` : ''}
                    </div>
                `)
                .join('');

            group.steps.sort((a, b) => {
                const timeA = a.leadtime ? a.leadtime.split('-')[0].trim() : "--:--";
                const timeB = b.leadtime ? b.leadtime.split('-')[0].trim() : "--:--";
                return timeA.localeCompare(timeB);
            });

            const stepsHtml = group.steps.map(step => {
                const moKey  = (step.mo || "").toUpperCase();
                // ✅ SỬA LẠI DÒNG NÀY: Bỏ biến 'workCenterName' không tồn tại
                const baseWc = normalizeWcForAs400(step.workCenter);
                const key    = `${moKey}|${baseWc}`;

                const planned = parseInt(step.qty) || 0;
                const moProgress = progressData[key] || {
                    status: 'pending',
                    progress: `0/${planned}`,
                    currentQty: 0,
                    plannedQty: planned
                };

                const statusClass = `status-${moProgress.status}`;

                return `
                <div class="wc-card wc-card-clickable" data-mo="${step.mo.toLowerCase()}" onclick="showMoScanDetail('${step.mo}', ${planned}, '${step.leadtime}', '${step.workCenter}')">
                    <div class="wc-name">${step.workCenter}</div>
                    <div class="wc-mo-with-progress ${statusClass}">${step.mo}, ${moProgress.progress}</div>
                    <div class="wc-leadtime ${statusClass}">${step.leadtime}</div>
                </div>`;
            }).join('');

            return `
                <div class="tracking-row">
                    ${mxHtml ? `<div class="mx-identifier">${mxHtml}</div>` : ''}
                    <div class="work-center-timeline">${stepsHtml}</div>
                </div>`;
        }).join('');
    }

    // ==================== CHẾ ĐỘ XEM THEO WORK CENTER ====================
    function renderByWorkCenter() {
        resultsContainer.className = 'wc-view-container';
        const dataByWc = {};

        allTrackingData.forEach(mxData => {
            if (!mxData.steps) return;
            mxData.steps.forEach(step => {
                if (!dataByWc[step.workCenter]) dataByWc[step.workCenter] = [];
                dataByWc[step.workCenter].push({
                    mo: step.mo,
                    qty: step.qty,
                    leadtime: step.leadtime
                });
            });
        });

        let sortedWcNames = Object.keys(dataByWc).sort();

        // LƯU danh sách đầy đủ WC (để dùng cho modal chọn WC)
        allWorkCentersData = sortedWcNames.map(wcName => {
        const moList = dataByWc[wcName];
        let inProgressCount = 0;

        moList.forEach(mo => {
            const moKey  = (mo.mo || "").toUpperCase();
            const baseWc = normalizeWcForAs400(wcName);   // wcName là tên sheet / WC group
            const key    = `${moKey}|${baseWc}`;

            const moProgress = progressData[key];
            if (moProgress && moProgress.status === 'in-progress') {
                inProgressCount++;
            }
        });

        return {
            name: wcName,
            totalMOs: moList.length,
            inProgressMOs: inProgressCount
        };
    });

        // Nếu đã chọn filter WC, chỉ hiển thị những WC được chọn
        if (selectedWorkCenters.size > 0) {
            sortedWcNames = sortedWcNames.filter(wc => selectedWorkCenters.has(wc));
        }

        if (sortedWcNames.length === 0) {
            resultsContainer.innerHTML =
                '<p style="text-align: center; color: #f39c12; padding: 50px;">📋 Chưa chọn Work Center nào. Bấm nút "📋 Chọn WC" để chọn.</p>';
            return;
        }

        resultsContainer.innerHTML = sortedWcNames.map(wcName => {
            const moList = dataByWc[wcName];
            moList.sort((a, b) => (a.leadtime || "00:00").localeCompare(b.leadtime || "00:00"));

            return `
                <div class="wc-group-card" data-wc="${wcName.toLowerCase()}">
                    <div class="wc-group-header">${wcName} (${moList.length} MOs)</div>
                    <div class="wc-mo-list">
                        ${moList.map(mo => {
                            const moKey  = (mo.mo || "").toUpperCase();
                            const baseWc = normalizeWcForAs400(wcName);
                            const key    = `${moKey}|${baseWc}`;

                            const planned = parseInt(mo.qty) || 0;
                            const moProgress = progressData[key] || {
                                status: 'pending',
                                progress: `0/${planned}`,
                                currentQty: 0,
                                plannedQty: planned
                            };

                            const statusClass = `status-${moProgress.status}`;

                            return `
                            <div class="mo-item ${statusClass}" data-mo-item="${mo.mo.toLowerCase()}" onclick="showMoScanDetail('${mo.mo}', ${planned}, '${mo.leadtime}', '${wcName}')" style="cursor: pointer;">
                                <span class="mo-info">${mo.mo} (${mo.qty})</span>
                                <span class="mo-progress">${moProgress.progress}</span>
                                <span class="mo-leadtime ${statusClass}">${mo.leadtime}</span>
                            </div>`;
                        }).join('')}
                    </div>
                </div>`;
        }).join('');
    }

    // ==================== HÀM TÌM KIẾM & HIGHLIGHT ====================
    function findAndHighlight() {
        const searchTerm = searchInput.value.toLowerCase().trim();
        if (!searchTerm) return;

        document.querySelectorAll('.highlight-search').forEach(el => el.classList.remove('highlight-search'));

        let foundElements;

        if (isWcView) {
            foundElements = document.querySelectorAll(`.mo-item[data-mo-item="${searchTerm}"]`);
        } else {
            foundElements = document.querySelectorAll(`.mx-card[data-mx="${searchTerm}"]`);
            if (foundElements.length === 0) {
                foundElements = document.querySelectorAll(`.wc-card[data-mo="${searchTerm}"]`);
            }
        }

        if (foundElements.length > 0) {
            showTempMessage(`Tìm thấy ${foundElements.length} vị trí!`, 'success');

            foundElements.forEach((element, index) => {
                if (isWcView) {
                    const parentCard = element.closest('.wc-group-card');
                    if (parentCard) parentCard.classList.add('highlight-search');
                }
                element.classList.add('highlight-search');
                if (index === 0) {
                    element.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'center' });
                }
            });

            setTimeout(() => {
                document.querySelectorAll('.highlight-search').forEach(el => el.classList.remove('highlight-search'));
            }, 3000);
        } else {
            showTempMessage('Không tìm thấy mã nào khớp!', 'error');
        }
    }

    function showTempMessage(message, type = 'error') {
        const tempMsg = document.createElement('div');
        tempMsg.textContent = message;
        let bgColor = (type === 'success') ? '#22c55e' :
                      (type === 'warning') ? '#f59e0b' : '#ef4444';
        tempMsg.style.cssText =
            `position: fixed; top: 50%; left: 50%; transform: translate(-50%, -50%);
             background: ${bgColor}; color: white; padding: 20px;
             border-radius: 8px; z-index: 9999; font-weight: bold;`;
        document.body.appendChild(tempMsg);
        setTimeout(() => tempMsg.remove(), 2000);
    }

    // ==================== HÀM HIỂN THỊ CHI TIẾT QUÉT MO ====================
    window.showMoScanDetail = async function (mo, plannedQty, leadtime, wcDetail) {
        const modal = document.getElementById('modalMoScanDetail');
        const titleEl = document.getElementById('moDetailTitle');
        const plannedQtyEl = document.getElementById('moPlannedQty');
        const scannedQtyEl = document.getElementById('moScannedQty');
        const leadtimeEl = document.getElementById('moLeadtime');
        const moMxEl = document.getElementById('moMx');      // Ô hiển thị MX
        const moMxBox = document.getElementById('moMxBox');  // Cả block MX
        const historyListEl = document.getElementById('moScanHistoryList');

        // 🔎 Tìm MX chứa MO này trong allTrackingData
        let foundMx = '-';
        for (const mxData of allTrackingData) {
            if (!mxData.steps) continue;
            const hasMo = mxData.steps.some(
                step => step.mo && step.mo.toUpperCase() === mo.toUpperCase()
            );
            if (hasMo) {
                // mxData.mx là tên MX (tùy bạn đặt trong API)
                foundMx = mxData.mx || mxData.Mx || '-';
                break;
            }
        }

        // Gán thông tin lên header modal
        titleEl.textContent = mo;
        if (moMxEl) moMxEl.textContent = foundMx;
        plannedQtyEl.textContent = `${plannedQty} kits`;
        leadtimeEl.textContent = leadtime || '-';

        historyListEl.innerHTML = '<p style="text-align: center; color: #a8edea;">⏳ Đang tải...</p>';
        modal.classList.add('active');

        // 👆 Cho phép click vào ô MX để chuyển sang chế độ Xem theo MX và cuộn đến MX đó
        if (moMxBox && foundMx && foundMx !== '-') {
            moMxBox.style.cursor = 'pointer';
            moMxBox.title = 'Click để xem chi tiết MX này';

            moMxBox.onclick = () => {
                // Đóng modal chi tiết MO
                modal.classList.remove('active');

                // Chuyển sang chế độ Xem theo MX
                isWcView = false;
                viewToggleBtn.textContent = 'Xem theo Work Center';
                searchInput.placeholder = '🔍 Tìm mã MX hoặc MO...';
                updateTopButtonsVisibility();
                renderTrackingData();

                // Cuộn đến MX tương ứng sau khi đã render
                setTimeout(() => {
                    const mxCard = document.querySelector(
                        `.mx-card[data-mx="${foundMx.toLowerCase()}"]`
                    );
                    if (mxCard) {
                        mxCard.scrollIntoView({ behavior: 'smooth', block: 'center' });
                        mxCard.classList.add('highlight-search');
                        setTimeout(() => mxCard.classList.remove('highlight-search'), 3000);
                    } else {
                        showTempMessage(
                            `Không tìm thấy MX ${foundMx} trên màn hình`,
                            'warning'
                        );
                    }
                }, 100);
            };
        } else if (moMxBox) {
            // Nếu không tìm được MX thì bỏ click
            moMxBox.style.cursor = 'default';
            moMxBox.onclick = null;
            moMxBox.title = '';
        }

        try {
            const response = await fetch(`/api/tracking/mo-scan-detail?mo=${encodeURIComponent(mo)}&workCenter=${encodeURIComponent(wcDetail)}`); 
            if (!response.ok) throw new Error("Không thể tải dữ liệu");

            const data = await response.json();
            scannedQtyEl.textContent = `${data.totalScannedQty} kits`;

            if (data.scans.length === 0) {
                historyListEl.innerHTML = `
                    <div class="no-scan-data">
                        <strong>Chưa có Kit nào được quét</strong>
                        <p style="margin-top: 10px;">MO này chưa bắt đầu chạy.</p>
                    </div>
                `;
                return;
            }

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


        } catch (error) {
            console.error("❌ Lỗi tải chi tiết quét:", error);
            historyListEl.innerHTML = `
                <div class="no-scan-data">
                    <strong>❌ Lỗi tải dữ liệu</strong>
                    <p style="margin-top: 10px;">${error.message}</p>
                </div>
            `;
        }
    };

    // ==================== NÚT CUỘN TỚI CÁC MO ĐANG LÀM (TẤT CẢ WORKCENTER) ====================
    if (btnFilterInProgress) {
        btnFilterInProgress.addEventListener('click', () => {
            if (!isWcView) {
                alert('⚠️ Chức năng này chỉ hoạt động ở chế độ "Xem theo Work Center"');
                return;
            }

            let totalScrolled = 0;
            let totalInProgress = 0;

            document.querySelectorAll('.wc-group-card').forEach(wcCard => {
                const wcName = wcCard.dataset.wc.toUpperCase();
                const inProgressMOs = [];

                wcCard.querySelectorAll('.mo-item').forEach(moItem => {
                    const moName = moItem.dataset.moItem.toUpperCase();
                    const wcName = wcCard.dataset.wc.toUpperCase();
                    const baseWc = normalizeWcForAs400(wcName);
                    const key    = `${moName}|${baseWc}`;

                    const moData = progressData[key];

                    if (moData && moData.status === 'in-progress') {
                        const leadtimeEl = moItem.querySelector('.mo-leadtime');
                        const leadtime = leadtimeEl ? leadtimeEl.textContent.trim() : '99:99';
                        const startTime = leadtime.split('-')[0].trim();
                        inProgressMOs.push({ moName, element: moItem, startTime });
                    }
                });


                if (inProgressMOs.length > 0) {
                    totalInProgress += inProgressMOs.length;
                    inProgressMOs.sort((a, b) => a.startTime.localeCompare(b.startTime));
                    const earliestMO = inProgressMOs[0];

                    setTimeout(() => {
                        earliestMO.element.scrollIntoView({
                            behavior: 'smooth',
                            block: 'center'
                        });
                        earliestMO.element.classList.add('highlight-search');
                        setTimeout(() => {
                            earliestMO.element.classList.remove('highlight-search');
                        }, 3000);
                    }, totalScrolled * 100);

                    totalScrolled++;
                    console.log(`✅ ${wcName}: Cuộn đến ${earliestMO.moName} (${earliestMO.startTime})`);
                }
            });

            if (totalInProgress === 0) {
                alert('📭 Không có MO nào đang làm (trạng thái VÀNG)');
            } else {
                showTempMessage(`🎯 Tìm thấy ${totalInProgress} MO đang làm trong ${totalScrolled} Work Center`, 'success');
            }
        });
    }

    // ==================== NÚT CUỘN TỚI CÁC MO TRỄ ====================
    if (btnFilterLate) {
        btnFilterLate.addEventListener('click', () => {
            if (!isWcView) {
                alert('⚠️ Chức năng này chỉ hoạt động ở chế độ "Xem theo Work Center"');
                return;
            }

            let totalScrolled = 0;
            let totalLate = 0;

            document.querySelectorAll('.wc-group-card').forEach(wcCard => {
                const wcName = wcCard.dataset.wc.toUpperCase();
                const lateMOs = [];

                wcCard.querySelectorAll('.mo-item').forEach(moItem => {
                    const moName = moItem.dataset.moItem.toUpperCase();
                    const wcName = wcCard.dataset.wc.toUpperCase();
                    const baseWc = normalizeWcForAs400(wcName);
                    const key    = `${moName}|${baseWc}`;

                    const moData = progressData[key];

                    if (moData && moData.status === 'late') {
                        const leadtimeEl = moItem.querySelector('.mo-leadtime');
                        const leadtime = leadtimeEl ? leadtimeEl.textContent.trim() : '99:99';
                        const startTime = leadtime.split('-')[0].trim();
                        lateMOs.push({ moName, element: moItem, startTime });
                    }
                });


                if (lateMOs.length > 0) {
                    totalLate += lateMOs.length;
                    lateMOs.sort((a, b) => a.startTime.localeCompare(b.startTime));
                    const earliestLate = lateMOs[0];

                    setTimeout(() => {
                        earliestLate.element.scrollIntoView({
                            behavior: 'smooth',
                            block: 'center'
                        });
                        earliestLate.element.classList.add('highlight-search');
                        setTimeout(() => {
                            earliestLate.element.classList.remove('highlight-search');
                        }, 3000);
                    }, totalScrolled * 100);

                    totalScrolled++;
                    console.log(`⚠️ ${wcName}: Cuộn đến MO trễ ${earliestLate.moName} (${earliestLate.startTime})`);
                }
            });

            if (totalLate === 0) {
                alert('✅ Không có MO nào bị trễ (status = late)');
            } else {
                showTempMessage(`⚠️ Có ${totalLate} MO trễ trong ${totalScrolled} Work Center`, 'warning');
            }
        });
    }

    // ==================== XỬ LÝ NÚT "CHỌN WORK CENTER" ====================
    if (btnSelectWorkCenter) {
        btnSelectWorkCenter.addEventListener('click', () => {
            if (!isWcView) {
                alert('⚠️ Chức năng này chỉ hoạt động ở chế độ "Xem theo Work Center"');
                return;
            }
            showWorkCenterGridModal();
        });
    }

    function showWorkCenterGridModal() {
        const modal = document.getElementById('modalSelectWorkCenter');
        const searchInput = document.getElementById('wcSearchInput');
        const totalCountEl = document.getElementById('totalWcCount');

        searchInput.value = '';

        if (allWorkCentersData.length === 0) {
            const containerEl = document.getElementById('wcGridContainer');
            containerEl.innerHTML =
                '<p style="text-align: center; color: #f39c12; padding: 50px;">⚠️ Chưa có dữ liệu Work Center. Vui lòng đợi dữ liệu được tải.</p>';
            modal.classList.add('active');
            return;
        }

        totalCountEl.textContent = allWorkCentersData.length;
        updateSelectedCount();
        renderWorkCenterGrid(allWorkCentersData);
        modal.classList.add('active');
    }

    function renderWorkCenterGrid(wcList) {
        const containerEl = document.getElementById('wcGridContainer');

        if (wcList.length === 0) {
            containerEl.innerHTML = '<p style="text-align: center; color: #f39c12; padding: 50px;">Không tìm thấy Work Center nào.</p>';
            return;
        }

        wcList.sort((a, b) => a.name.localeCompare(b.name));

        containerEl.innerHTML = wcList.map(wc => {
            const isSelected = selectedWorkCenters.has(wc.name);
            return `
                <div class="wc-grid-item ${isSelected ? 'selected' : ''}" data-wc-name="${wc.name}" onclick="toggleWorkCenter('${wc.name}')">
                    <div class="wc-grid-name">${wc.name}</div>
                    <div class="wc-grid-info">${wc.totalMOs} MO (${wc.inProgressMOs} đang làm)</div>
                </div>`;
        }).join('');
    }

    window.toggleWorkCenter = function (wcName) {
        if (selectedWorkCenters.has(wcName)) {
            selectedWorkCenters.delete(wcName);
        } else {
            selectedWorkCenters.add(wcName);
        }

        const wcItem = document.querySelector(`.wc-grid-item[data-wc-name="${wcName}"]`);
        if (wcItem) {
            wcItem.classList.toggle('selected');
        }

        updateSelectedCount();
    };

    function updateSelectedCount() {
        const selectedCountEl = document.getElementById('selectedWcCount');
        if (selectedCountEl) {
            selectedCountEl.textContent = selectedWorkCenters.size;
        }
    }

    if (btnSelectAllWC) {
        btnSelectAllWC.addEventListener('click', () => {
            document.querySelectorAll('.wc-grid-item').forEach(item => {
                const wcName = item.dataset.wcName;
                selectedWorkCenters.add(wcName);
                item.classList.add('selected');
            });
            updateSelectedCount();
        });
    }

    if (btnDeselectAllWC) {
        btnDeselectAllWC.addEventListener('click', () => {
            selectedWorkCenters.clear();
            document.querySelectorAll('.wc-grid-item').forEach(item => {
                item.classList.remove('selected');
            });
            updateSelectedCount();
        });
    }

    if (btnApplyWcFilter) {
        btnApplyWcFilter.addEventListener('click', () => {
            document.getElementById('modalSelectWorkCenter').classList.remove('active');
            renderTrackingData();
            if (selectedWorkCenters.size === 0) {
                showTempMessage('⚠️ Chưa chọn Work Center nào', 'warning');
            } else {
                showTempMessage(`✓ Đang hiển thị ${selectedWorkCenters.size} Work Center`, 'success');
            }
        });
    }

    if (wcSearchInput) {
        wcSearchInput.addEventListener('input', () => {
            const searchTerm = wcSearchInput.value.toLowerCase().trim();
            const allItems = document.querySelectorAll('.wc-grid-item');

            allItems.forEach(item => {
                const wcName = item.dataset.wcName.toLowerCase();
                if (searchTerm === '' || wcName.includes(searchTerm)) {
                    item.style.display = 'block';
                } else {
                    item.style.display = 'none';
                }
            });
        });
    }

    // ==================== SỰ KIỆN CHUNG ====================
    dateInput.addEventListener('change', () => {
        loadTrackingData();
        loadKitProgress();
    });

    searchInput.addEventListener('keypress', (e) => {
        if (e.key === 'Enter') findAndHighlight();
    });

    viewToggleBtn.addEventListener('click', () => {
        isWcView = !isWcView;
        searchInput.disabled = false;
        if (isWcView) {
            viewToggleBtn.textContent = 'Xem theo MX';
            searchInput.placeholder = '🔍 Tìm mã MO...';
        } else {
            viewToggleBtn.textContent = 'Xem theo Work Center';
            searchInput.placeholder = '🔍 Tìm mã MX hoặc MO...';
        }
        updateTopButtonsVisibility();
        renderTrackingData();
    });

    // ==================== THIẾT LẬP KẾT NỐI SIGNALR REAL-TIME ====================
    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/orderHub") // Kết nối đến OrderHub đã có
        .withAutomaticReconnect()
        .build();

    // Lắng nghe sự kiện "MoProgressUpdated" từ server
    connection.on("MoProgressUpdated", (data) => {
        console.log("📡 SignalR: Received MoProgressUpdated", data);
        const moKey    = (data.mo || "").toUpperCase();
        const wcDetail = (data.workCenter || "").toUpperCase(); // ✅ backend gửi WC chi tiết
        const wcBase   = normalizeWcForAs400(wcDetail);
        const key      = `${moKey}|${wcBase}`;

        if (!progressData) progressData = {};
        progressData[key] = {
            status     : data.status,
            progress   : `${data.actual}/${data.planned}`,
            currentQty : data.actual,
            plannedQty : data.planned,
            workCenter : data.workCenter
        };
        showTempMessage(`MO: ${data.mo} -> ${data.actual}/${data.planned}`, 'success');
        renderTrackingData();
    });

    // Bắt đầu kết nối
    async function startSignalR() {
        try {
            await connection.start();
            console.log("✅ SignalR Connected.");
        } catch (err) {
            console.error("❌ SignalR Connection Failed: ", err);
            setTimeout(startSignalR, 5000); // Thử lại sau 5 giây
        }
    }

    if (btnShowProgressSummary) {
        btnShowProgressSummary.addEventListener('click', showProgressSummaryModal);
    }

    function showProgressSummaryModal() {
        const modal = document.getElementById('modalProgressSummary');
        const wcContainer = document.getElementById('wcProgressContainer');
        const factoryContainer = document.getElementById('factoryProgressContainer');

        // 1. TÍNH LẠI TIẾN ĐỘ TỪ allTrackingData
        const progressByWc = {};
        
        // Khởi tạo progressByWc với WC CHI TIẾT
        allWorkCentersData.forEach(wc => {
            progressByWc[wc.name] = { totalMOs: 0, completedMOs: 0 };
        });

        // Lặp qua allTrackingData để đếm
        allTrackingData.forEach(mxData => {
            mxData.steps.forEach(step => {
                const wcDetail = step.workCenter;
                if (progressByWc[wcDetail]) {
                    const moKey  = (step.mo || "").toUpperCase();
                    const baseWc = normalizeWcForAs400(wcDetail);
                    const key    = `${moKey}|${baseWc}`;

                    const moProgress = progressData[key];

                    progressByWc[wcDetail].totalMOs++;
                    if (moProgress && (moProgress.status === 'done' || moProgress.status === 'late')) {
                        progressByWc[wcDetail].completedMOs++;
                    }
                }
            });
        });

        // 2. Vẽ HTML cho từng Work Center
        let wcHtml = '';
        let factoryTotalMOs = 0;
        let factoryCompletedMOs = 0;

        // Sắp xếp theo tên WC chi tiết
        Object.keys(progressByWc).sort().forEach(wcName => {
            const data = progressByWc[wcName];
            factoryTotalMOs += data.totalMOs;
            factoryCompletedMOs += data.completedMOs;
            
            const percentage = data.totalMOs > 0 ? ((data.completedMOs / data.totalMOs) * 100).toFixed(1) : 0;
            
            wcHtml += `
                <div class="progress-item">
                    <div class="progress-label">
                        <strong>${wcName}</strong>
                        <span>${data.completedMOs} / ${data.totalMOs} MOs</span>
                    </div>
                    <div class="progress-bar-container">
                        <div class="progress-bar-fill" style="width: ${percentage}%;">${percentage}%</div>
                    </div>
                </div>
            `;
        });
        wcContainer.innerHTML = wcHtml;

        // 3. Vẽ HTML cho toàn xưởng
        const factoryPercentage = factoryTotalMOs > 0 ? ((factoryCompletedMOs / factoryTotalMOs) * 100).toFixed(1) : 0;
        factoryContainer.innerHTML = `
            <div class="progress-item">
                <div class="progress-label" style="font-size: 18px;">
                    <strong>TOÀN XƯỞNG</strong>
                    <span>${factoryCompletedMOs} / ${factoryTotalMOs} MOs</span>
                </div>
                <div class="progress-bar-container" style="height: 30px;">
                    <div class="progress-bar-fill" style="width: ${factoryPercentage}%; height: 30px; line-height: 30px; font-size: 16px;">
                        ${factoryPercentage}%
                    </div>
                </div>
            </div>
        `;

        // 4. Mở Modal
        modal.classList.add('active');
    }

    function autoSearchFromUrl() {
        const urlParams = new URLSearchParams(window.location.search);
        const searchTermFromUrl = urlParams.get('search');

        if (searchTermFromUrl) {
            // Điền vào ô tìm kiếm
            searchInput.value = searchTermFromUrl;
            // Cập nhật trạng thái và áp dụng bộ lọc
            // (Chờ một chút để đảm bảo dữ liệu đã tải xong)
            setTimeout(() => {
                findAndHighlight();
            }, 500);
        }
    }

    // Khởi động
    startSignalR();
    Promise.all([loadTrackingData(), loadKitProgress()]).then(() => {
        autoSearchFromUrl(); // Gọi hàm tự động tìm kiếm sau khi dữ liệu đã tải xong
    });
    loadTrackingData();
    loadKitProgress();
    updateTopButtonsVisibility();
});
