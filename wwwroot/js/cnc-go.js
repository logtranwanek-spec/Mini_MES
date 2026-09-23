// ==================== CNC GO - TOOL MANAGEMENT (4 DAO CỐ ĐỊNH) ====================

const MACHINES = [];
for (let i = 4; i <= 21; i++) {
    MACHINES.push(`Heian ${i}`);
}

const SUPPLIERS = ["An Bình", "Trang Tuyển"];

let tool1OldValue = null; // Dùng để lưu giá trị cũ của ô input của dao 1

// Hàm helper để đồng bộ giá trị từ dao 1 sang các dao còn lại
function syncTool1Values(sourceElement, fieldPrefix) {
    const newValue = sourceElement.value;
    
    // Chỉ cập nhật cho các dao 2, 3, 4 nếu giá trị của chúng
    // giống với giá trị CŨ của dao 1 (tức là chúng chưa bị sửa thủ công)
    for (let i = 2; i <= 4; i++) {
        const targetElement = document.getElementById(`${fieldPrefix}${i}`);
        if (targetElement && targetElement.value === tool1OldValue) {
            targetElement.value = newValue;
        }
    }
}

let currentToolIndexForSupplier = null; // Lưu lại index của dao đang cần nhập supplier

// Supervisor mapping theo máy
const SUPERVISORS = {
    group_4_14: [
        "Hoàng Văn Mạnh Hùng",
        "Ngô Định Kỳ"
    ],
    group_15_21: [
        "Bùi Văn Đặng",
        "Nguyễn Minh Vấn"
    ]
};

// ==================== KHỞI TẠO ====================
document.addEventListener('DOMContentLoaded', () => {
    initMachineList();
    initShiftAuto();
    initMssPersist();
    initSupervisorAuto();
    setDefaultDateTime();
    initSignalR();

    const modalSupplier = document.getElementById('modalSupplier');
    const btnConfirmSupplier = document.getElementById('btnConfirmSupplier');
    
    if (btnConfirmSupplier) btnConfirmSupplier.addEventListener('click', confirmSupplier);
    if (modalSupplier) {
        modalSupplier.addEventListener('click', (e) => {
            if (e.target === modalSupplier) cancelSupplier();
        });
    }

    const supplierInput = document.getElementById('supplierInput');
    if (supplierInput) {
        supplierInput.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                confirmSupplier();
            }
        });
    }

    for (let i = 1; i <= 4; i++) {
        const toolTypeSelect = document.getElementById(`toolType${i}`);
        if (toolTypeSelect) {
            toolTypeSelect.addEventListener('change', () => handleToolTypeChange(i));
        }
    }

    for (let i = 1; i <= 4; i++) {
        const hoursSelect  = document.getElementById(`actualHours${i}`);
        const reasonSelect = document.getElementById(`reason${i}`);

        if (hoursSelect) {
            hoursSelect.addEventListener('change', () => updateInstallHead(i));
        }
        if (reasonSelect) {
            reasonSelect.addEventListener('change', () => updateInstallHead(i));
        }
    }
    const fieldsToSync = ['replaceDate', 'replaceTime', 'installDate', 'installTime'];

    fieldsToSync.forEach(prefix => {
        const sourceInput = document.getElementById(`${prefix}1`); // Chỉ áp dụng cho dao 1
        if (sourceInput) {
            // 1. Lưu lại giá trị cũ khi người dùng focus vào ô
            sourceInput.addEventListener('focus', () => {
                tool1OldValue = sourceInput.value;
            });

            // 2. Khi giá trị thay đổi, đồng bộ cho các dao còn lại
            sourceInput.addEventListener('change', () => {
                syncTool1Values(sourceInput, prefix);
            });
        }
    });
});

// 1. Điền dropdown máy
function initMachineList() {
    const select = document.getElementById('machine');
    const shiftSelect = document.getElementById('shift');

    // Khởi tạo theo ca hiện tại
    rebuildMachineOptions();

    // Khi chọn máy → tự fill Supervisor
    select.addEventListener('change', () => {
        updateSupervisorOptions();
    });

    // Khi đổi Ca → cập nhật lại text "DS-/NS-" nhưng giữ nguyên máy đang chọn
    shiftSelect.addEventListener('change', () => {
        rebuildMachineOptions();
        updateSupervisorOptions();
        setDefaultDateTime();
    });
}

// 2. Ca làm việc tự động Day/Night theo giờ hiện tại
function initShiftAuto() {
    const shiftSelect = document.getElementById('shift');
    const now = new Date();
    const hour = now.getHours(); // 0–23

    // Day Shift: 6h–20h ; Night Shift: 20h–6h
    let currentShift;
    if (hour >= 6 && hour < 20) {
        currentShift = "Day Shift";
    } else {
        currentShift = "Night Shift";
    }

    shiftSelect.value = currentShift;
    // Nếu muốn cho người dùng chỉnh lại thì giữ nguyên select;
    // nếu không cho chỉnh, có thể thêm: shiftSelect.disabled = true;
}

// 3. MSS = ngày thứ Tư kết thúc tuần sản xuất (Thứ Năm đến Thứ Tư)
function getMssWeekEndingWednesday(referenceDate = new Date()) {
    const weekEnding = new Date(referenceDate);
    const daysUntilWednesday = (3 - weekEnding.getDay() + 7) % 7;
    weekEnding.setDate(weekEnding.getDate() + daysUntilWednesday);
    return `${String(weekEnding.getMonth() + 1).padStart(2, '0')}${String(weekEnding.getDate()).padStart(2, '0')}`;
}

function initMssPersist() {
    const mssInput = document.getElementById('mss');
    if (!mssInput) return;
    mssInput.value = getMssWeekEndingWednesday();
    mssInput.readOnly = true;
    mssInput.title = 'MSS là ngày thứ Tư kết thúc tuần sản xuất (Thứ Năm đến Thứ Tư).';
}

// 4. Supervisor auto theo máy + nút +
function initSupervisorAuto() {
    const supervisorSelect = document.getElementById('supervisor');
    const btnAdd = document.getElementById('btnAddSupervisor');
    const btnRemove = document.getElementById('btnRemoveSupervisor');

    // Khởi tạo 4 Sup mặc định
    supervisorSelect.innerHTML = "";
    [
        "Hoàng Văn Mạnh Hùng",
        "Ngô Định Kỳ",
        "Bùi Văn Đặng",
        "Nguyễn Minh Vấn"
    ].forEach(name => {
        const opt = document.createElement('option');
        opt.value = name;
        opt.textContent = name;
        supervisorSelect.appendChild(opt);
    });

    supervisorSelect.dataset.auto = "true";

    supervisorSelect.addEventListener('change', () => {
        supervisorSelect.dataset.auto = "false";
    });

    // Thêm Sup mới
    btnAdd.addEventListener('click', () => {
        const name = prompt("Nhập tên Supervisor mới:");
        if (name && name.trim()) {
            const trimmed = name.trim();
            const opt = document.createElement('option');
            opt.value = trimmed;
            opt.textContent = trimmed;
            supervisorSelect.appendChild(opt);
            supervisorSelect.value = trimmed;
            supervisorSelect.dataset.auto = "false";
        }
    });

    // ⭐ Xóa Sup đang chọn khỏi dropdown
    btnRemove.addEventListener('click', () => {
        const currentValue = supervisorSelect.value;
        if (!currentValue) {
            alert("Chưa có Supervisor nào được chọn.");
            return;
        }

        // Nếu là 4 Sup mặc định, hỏi lại cho chắc
        const defaultSup = [
            "Hoàng Văn Mạnh Hùng",
            "Ngô Định Kỳ",
            "Bùi Văn Đặng",
            "Nguyễn Minh Vấn"
        ];
        if (defaultSup.includes(currentValue)) {
            const ok = confirm(`Bạn có chắc muốn xóa Supervisor mặc định: "${currentValue}" khỏi danh sách?`);
            if (!ok) return;
        }

        // Xóa option hiện tại
        const options = Array.from(supervisorSelect.options);
        const target = options.find(opt => opt.value === currentValue);
        if (target) {
            supervisorSelect.removeChild(target);
        }

        // Chọn option đầu tiên còn lại (nếu có)
        if (supervisorSelect.options.length > 0) {
            supervisorSelect.selectedIndex = 0;
        } else {
            supervisorSelect.value = ""; // không còn Sup nào
        }

        // Đánh dấu là manual
        supervisorSelect.dataset.auto = "false";
    });
}

// Làm tròn time string HH:mm tới mốc gần nhất trong danh sách
function snapToNearestTime(currentTime, candidates) {
    if (!currentTime) return null;
    const [ch, cm] = currentTime.split(':').map(Number);
    const currentMinutes = ch * 60 + cm;

    let best = null;
    let bestDiff = Infinity;

    candidates.forEach(t => {
        const [h, m] = t.split(':').map(Number);
        const minutes = h * 60 + m;
        const diff = Math.abs(minutes - currentMinutes);
        if (diff < bestDiff) {
            bestDiff = diff;
            best = t;
        }
    });

    return best;
}

// Kiểm tra currentTime nằm trong khoảng [from, to] (HH:mm)
function isBetween(currentTime, from, to) {
    if (!currentTime) return false;
    const [ch, cm] = currentTime.split(':').map(Number);
    const [fh, fm] = from.split(':').map(Number);
    const [th, tm] = to.split(':').map(Number);

    const cur = ch * 60 + cm;
    const start = fh * 60 + fm;
    const end = th * 60 + tm;
    return cur >= start && cur <= end;
}

// Dựa trên máy → fill danh sách Sup phù hợp
function updateSupervisorOptions() {
    const machineSelect = document.getElementById('machine');
    const shiftSelect = document.getElementById('shift');
    const supervisorSelect = document.getElementById('supervisor');
    const machineNumber = parseInt((machineSelect.value.match(/\d+/) || [])[0], 10);

    if (!machineNumber || !shiftSelect.value || !supervisorSelect) return;

    const supervisorGroup = machineNumber >= 4 && machineNumber <= 14
        ? SUPERVISORS.group_4_14
        : machineNumber >= 15 && machineNumber <= 21
            ? SUPERVISORS.group_15_21
            : null;

    if (!supervisorGroup) return;

    // Phần tử đầu là ca ngày, phần tử thứ hai là ca đêm.
    const defaultSupervisor = shiftSelect.value === 'Night Shift'
        ? supervisorGroup[1]
        : supervisorGroup[0];

    // Áp lại mặc định khi đổi máy/ca; sau đó người dùng vẫn có thể chọn tên khác.
    supervisorSelect.value = defaultSupervisor;
    supervisorSelect.dataset.auto = 'true';
}

// 5. Ngày/giờ thay & lắp mặc định = hiện tại, lý do & loại dao mặc định
function setDefaultDateTime() {
    const now = new Date();
    const todayStr = now.toISOString().split('T')[0];       // yyyy-MM-dd
    const timeStr = now.toTimeString().substring(0, 5);     // HH:mm

    // Ngày chung
    const dateInput = document.getElementById('date');
    if (dateInput) dateInput.value = todayStr;

    const shift = document.getElementById('shift').value;

    // ===== 1. Xác định giờ thay mặc định =====
    let defaultReplaceTime = timeStr;

    // Điều kiện của bạn:
    // - Nếu thời gian thay nằm trong khoảng 17:00-18:30 -> mặc định 17:30
    // - Nếu trong khoảng 19:15-20:30 -> mặc định 19:45
    if (isBetween(timeStr, "17:00", "18:30")) {
        defaultReplaceTime = "17:30";
    } else if (isBetween(timeStr, "19:15", "20:30")) {
        defaultReplaceTime = "19:45";
    }

    // ===== 2. Xác định giờ lắp mặc định =====
    let defaultInstallTime = timeStr;

    if (shift === "Day Shift") {
        // Ca ngày: lắp gần các mốc 7:00, 8:00, 9:00
        defaultInstallTime = snapToNearestTime(timeStr, ["07:00", "08:00", "09:00"]);
    } else {
        // Ca đêm: lắp gần 19:45
        defaultInstallTime = "19:45"; // nếu muốn snap thật: snapToNearestTime(timeStr, ["19:45"])
    }

    // ===== 3. Gán cho 4 dao =====
    for (let i = 1; i <= 4; i++) {
        // Ngày thay / lắp
        document.getElementById(`replaceDate${i}`).value = todayStr;
        document.getElementById(`installDate${i}`).value = todayStr;

        // Giờ thay / lắp
        document.getElementById(`replaceTime${i}`).value = defaultReplaceTime;
        document.getElementById(`installTime${i}`).value = defaultInstallTime;

        // Giờ thực tế mặc định 0
        document.getElementById(`actualHours${i}`).value = "0";

        // Lý do thay mặc định Cuối ca thay
        document.getElementById(`reason${i}`).value = "Cuối ca thay";

        // Loại dao mặc định MỚI
        document.getElementById(`toolType${i}`).value = "MỚI";

        // ⭐ ĐẦU DAO THÁO / LẮP THEO CA
        const removeHeadSpan  = document.getElementById(`removeHead${i}`);
        const installHeadSpan = document.getElementById(`installHead${i}`);

        if (shift === "Day Shift") {
            // Ca ngày luôn quản lý Dao 1–4.
            if (removeHeadSpan)  removeHeadSpan.textContent  = i;       // 1..4
            if (installHeadSpan) installHeadSpan.textContent = i;       // 1..4
        } else {
            // Ca đêm luôn quản lý Dao 5–8.
            if (removeHeadSpan)  removeHeadSpan.textContent  = 4 + i;   // 5..8
            if (installHeadSpan) installHeadSpan.textContent = 4 + i;   // 5..8
        }
        updateInstallHead(i);
    }
}

// Cập nhật Đầu dao lắp cho 1 vị trí dao (1..4) dựa trên giờ thực tế + lý do + ca
function updateInstallHead(i) {
    const shift = document.getElementById('shift').value;

    const actualHours = parseInt(document.getElementById(`actualHours${i}`).value || "0", 10);
    const reason      = document.getElementById(`reason${i}`).value || "";

    const removeHeadSpan  = document.getElementById(`removeHead${i}`);
    const installHeadSpan = document.getElementById(`installHead${i}`);

    if (!removeHeadSpan || !installHeadSpan) return;

    // Nếu dao bị hư: giờ > 0 và lý do ≠ "Cuối ca thay"
    const isDamaged = actualHours > 0 && reason && reason !== "Cuối ca thay";

    if (isDamaged) {
        // 🔧 Dao hư → lắp lại dao mới vào đúng vị trí vừa tháo (Đầu dao lắp = Đầu dao tháo)
        installHeadSpan.textContent = removeHeadSpan.textContent;
    } else {
        // Cuối ca thay hoặc giờ chạy = 0 → giữ đúng bộ dao của ca hiện tại.
        if (shift === "Day Shift") {
            installHeadSpan.textContent = i.toString();
        } else {
            installHeadSpan.textContent = (4 + i).toString();
        }
    }
}

// ==================== LƯU TẤT CẢ 4 DAO ====================
async function saveAllTools() {
    const machine = document.getElementById('machine').value;
    const shift = document.getElementById('shift').value;
    const supervisor = document.getElementById('supervisor').value;
    const mss = document.getElementById('mss').value;
    const date = document.getElementById('date').value;

    if (!machine) {
        showToast('❌ Vui lòng chọn máy CNC', 'error');
        return;
    }

    const toolsData = [];

    for (let i = 1; i <= 4; i++) {
        const replaceDate = document.getElementById(`replaceDate${i}`).value || null;
        const replaceTime = document.getElementById(`replaceTime${i}`).value || null;
        const actualHours = parseInt(document.getElementById(`actualHours${i}`).value) || 0;
        const reason      = document.getElementById(`reason${i}`).value;
        const installDate = document.getElementById(`installDate${i}`).value || null;
        const installTime = document.getElementById(`installTime${i}`).value || null;
        const toolType    = document.getElementById(`toolType${i}`).value;
        const material    = "PLYWOOD";

        const supplierHiddenInput = document.getElementById(`supplier${i}`);
        const supplier = supplierHiddenInput ? supplierHiddenInput.value.trim() : '';
        const removeHeadSpan  = document.getElementById(`removeHead${i}`);
        const installHeadSpan = document.getElementById(`installHead${i}`);
        const removeToolNumber  = removeHeadSpan ? parseInt(removeHeadSpan.textContent)  : null;
        const installToolNumber = installHeadSpan ? parseInt(installHeadSpan.textContent) : null;

        // ✅ XÁC ĐỊNH CÓ THÁO HAY KHÔNG
        const hasReplace = !!reason && actualHours > 0 && !!replaceDate;

        // ------ THÁO DAO ------
        if (hasReplace) {
            toolsData.push({
                toolPosition: i,
                toolNumber: removeToolNumber,
                replaceDate: replaceDate,
                replaceTime: replaceTime,
                actualHours: actualHours,
                reason: reason,
                material: material,
                installDate: null,
                installTime: null,
                toolType: null,
                supplier: null
            });
        }

        // ------ LẮP DAO ------
        // Chỉ lắp nếu:
        //  - Dao này vừa được THÁO (hasReplace = true)
        //    → Thay dao mới vào đúng vị trí đó
        if (hasReplace && (installDate || installTime)) {
            toolsData.push({
                toolPosition: i,
                toolNumber: installToolNumber,
                replaceDate: null,
                replaceTime: null,
                actualHours: 0,
                reason: null,
                material: material,
                installDate: installDate,
                installTime: installTime,
                toolType: toolType,
                supplier: supplier
            });
        }


    }

    if (toolsData.length === 0) {
        showToast('❌ Không có dao nào được thay', 'error');
        return;
    }

    try {
        const response = await fetch('/api/tools/change', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                machineName: machine,
                shift: shift,
                supervisor: supervisor,
                mss: mss,
                date: date,
                tools: toolsData
            })
        });

        if (response.ok) {
            const result = await response.json();
            showToast(`✅ ${result.message}`, 'success');
            clearAllRows();
            setDefaultDateTime();
            loadDashboard();
            loadHistory();
        } else {
            let errorMessage = `HTTP ${response.status}`;
            try {
                const text = await response.text();
                if (text) errorMessage += ` - ${text}`;
            } catch (_) {}
            showToast(`❌ Lỗi lưu: ${errorMessage}`, 'error');
        }
    } catch (error) {
        showToast(`❌ Lỗi kết nối: ${error.message}`, 'error');
    }
}

// ==================== CHUYỂN TAB ====================
function switchTab(tabName) {
    document.querySelectorAll('.tab-content').forEach(tab => {
        tab.classList.remove('active');
    });
    document.querySelectorAll('.tab-btn').forEach(btn => {
        btn.classList.remove('active');
    });

    document.getElementById(`tab-${tabName}`).classList.add('active');
    event.target.classList.add('active');

    if (tabName === 'dashboard') loadDashboard();
    if (tabName === 'history') loadHistory();
    if (tabName === 'supplier-report') loadSupplierReport();
}

// ==================== DASHBOARD "QUẢN LÝ" ====================
async function loadDashboard() {
    const container = document.getElementById('dashboardContent');
    if (!container) return;

    container.innerHTML = '<p class="loading-text">Đang tải dữ liệu...</p>';

    try {
        const response = await fetch('/api/tools/status');
        const data = await response.json();

        let html = '<div style="display: grid; grid-template-columns: 1fr 1fr; gap: 30px;">';

        html += '<div>';
        html += '<h2 style="text-align: center; margin-bottom: 20px; color: #f39c12;">☀️ Quản lý dao - Ca ngày (Dao 1 → 4)</h2>';
        html += renderMachineGrid(data, 'day');    // dùng toàn bộ data
        html += '</div>';

        html += '<div>';
        html += '<h2 style="text-align: center; margin-bottom: 20px; color: #9b59b6;">🌙 Quản lý dao - Ca đêm (Dao 5 → 8)</h2>';
        html += renderMachineGrid(data, 'night');  // dùng toàn bộ data
        html += '</div>';

        html += '</div>';
        container.innerHTML = html;
    } catch (error) {
        container.innerHTML = `<p style="color: #e74c3c;">❌ Lỗi: ${error.message}</p>`;
    }
}

function renderMachineGrid(data, shiftType) {
    const machineMap = {};

    data.forEach(item => {
        const machine = item.machineName;
        const toolAddress = item.toolAddress || '';
        const parts = toolAddress.split('Dao');
        const toolNumber = parts.length === 2 ? parseInt(parts[1], 10) : 0; // 1..8

        if (!toolNumber) return;

        // DS: Dao 1-4 ; NS: Dao 5-8
        const isDs = toolNumber >= 1 && toolNumber <= 4;
        const isNs = toolNumber >= 5 && toolNumber <= 8;

        if (shiftType === 'day' && !isDs) return;
        if (shiftType === 'night' && !isNs) return;

        // Vị trí vật lý 1-4
        const pos = (toolNumber <= 4) ? toolNumber : toolNumber - 4;

        if (!machineMap[machine]) {
            machineMap[machine] = {};
        }
        machineMap[machine][pos] = {
            ...item,
            toolNumber
        };
    });

    let html = '<div class="machine-status-grid">';

    MACHINES.forEach(machine => {
        const prefix = shiftType === 'night' ? 'NS' : 'DS';
        const displayName = `${prefix}-${machine}`;

        html += '<div class="machine-status-card">';
        html += `<h3 class="machine-status-title">${displayName}</h3>`;

        for (let pos = 1; pos <= 4; pos++) {
            const tool = machineMap[machine]?.[pos];
            const version = tool?.currentVersion || '-';
            const hours   = tool?.currentVersionHours || 0;

            // DS: Dao 1-4 ; NS: Dao 5-8
            const displayIndex = (shiftType === 'night') ? pos + 4 : pos;

            let hoursStyle = '';
            let hoursLabel = '';

            if (hours === 0 && version !== '-') {
                hoursStyle = 'color: #27ae60; font-weight: bold;';
                hoursLabel = 'MỚI';
            } else if (hours > 0 && hours <= 20) {
                hoursStyle = 'color: #3498db;';
                hoursLabel = `${hours}h`;
            } else if (hours > 20 && hours <= 40) {
                hoursStyle = 'color: #f39c12;';
                hoursLabel = `${hours}h ⚠️`;
            } else if (hours > 40) {
                hoursStyle = 'color: #e74c3c; font-weight: bold;';
                hoursLabel = `${hours}h 🔴`;
            }

            html += '<div class="machine-tool-status-row">';
            html += `<span class="machine-tool-label">Dao số ${displayIndex}</span>`;
            html += `<span>`;
            html += `<strong class="machine-tool-version">${version}</strong>`;
            if (version !== '-') {
                html += `<span style="${hoursStyle} font-size: 12px;">${hoursLabel}</span>`;
            }
            html += `</span>`;
            html += '</div>';
        }

        html += '</div>';
    });

    html += '</div>';
    return html;
}

function buildMissingSummary(data) {
    const now = new Date();
    const currentTimeStr = now.toTimeString().substring(0, 5); // "HH:mm"
    const today = now.toISOString().split('T')[0];             // "yyyy-MM-dd"

    // Khung giờ cảnh báo
    const dayAlertFrom = "19:45";
    const dayAlertTo   = "20:00";
    const nightAlertFrom = "06:45";
    const nightAlertTo   = "07:00";

    const isDayAlertTime = isTimeInRange(currentTimeStr, dayAlertFrom, dayAlertTo);
    const isNightAlertTime = isTimeInRange(currentTimeStr, nightAlertFrom, nightAlertTo);

    // Nếu không trong khung giờ cảnh báo → không hiển thị gì
    if (!isDayAlertTime && !isNightAlertTime) {
        return "";
    }

    // Lấy danh sách máy từ MACHINES global
    const allMachines = MACHINES;

    const missingMachines = [];

    allMachines.forEach(machine => {
        if (isDayAlertTime) {
            // Kiểm tra ca ngày
            const hasDayLog = data.some(t =>
                t.machineName === machine &&
                t.shift === "Day Shift" &&
                t.reason === "Cuối ca thay" &&
                t.replaceDate && t.replaceDate.startsWith(today)
            );
            if (!hasDayLog) {
                missingMachines.push({ machine, shift: "Day Shift", time: `${dayAlertFrom}–${dayAlertTo}` });
            }
        }

        if (isNightAlertTime) {
            // Kiểm tra ca đêm
            const hasNightLog = data.some(t =>
                t.machineName === machine &&
                t.shift === "Night Shift" &&
                t.reason === "Cuối ca thay" &&
                t.replaceDate && t.replaceDate.startsWith(today)
            );
            if (!hasNightLog) {
                missingMachines.push({ machine, shift: "Night Shift", time: `${nightAlertFrom}–${nightAlertTo}` });
            }
        }
    });

    if (missingMachines.length === 0) return "";

    let html = `<div style="margin-bottom: 20px; padding: 15px; border-radius: 8px; background: rgba(231, 76, 60, 0.1); border:1px solid #e74c3c;">`;
    html += `<h3 style="margin-bottom: 10px; color: #e74c3c;">⚠️ CẢNH BÁO: Máy chưa ghi “Cuối ca thay” trong hôm nay</h3>`;

    missingMachines.forEach(m => {
        html += `<div style="margin-bottom: 5px;">
                     - <strong>${m.machine}</strong> (${m.shift}) – đang trong khung giờ cảnh báo ${m.time}
                 </div>`;
    });

    html += `</div>`;
    return html;
}

// ==================== LỊCH SỬ (giữ nguyên như trước) ====================
function formatCncHistoryDate(value) {
    if (!value) return '';
    const datePart = String(value).split('T')[0];
    const parts = datePart.split('-');
    return parts.length === 3 ? `${parts[2]}/${parts[1]}/${parts[0]}` : datePart;
}

function formatCncHistoryTime(value) {
    if (!value) return '';
    return String(value).substring(0, 5);
}

function getCncToolNumber(item) {
    const match = String(item.toolAddress || '').match(/-Dao(\d+)$/i);
    if (match) return Number(match[1]);
    return item.shift === 'Night Shift' ? Number(item.toolPosition || 0) + 4 : Number(item.toolPosition || 0);
}

function getHistoryReasonClass(reason) {
    const classes = {
        'Mẻ': 'reason-me',
        'Cháy': 'reason-chay',
        'Cùn': 'reason-cun',
        'Gãy': 'reason-gay'
    };
    return classes[String(reason || '').trim()] || '';
}

// ==================== LỊCH SỬ CNC - BỐ CỤC GIỐNG FILE EXCEL ====================
function showCncHistorySheet(sheetId, button) {
    document.querySelectorAll('.cnc-history-sheet').forEach(sheet => sheet.classList.remove('active'));
    document.querySelectorAll('.cnc-sheet-tab').forEach(tab => tab.classList.remove('active'));

    const activeSheet = document.getElementById(sheetId);
    activeSheet?.classList.add('active');
    button?.classList.add('active');

    // L?ch s? ???c x?p t? c? ??n m?i; khi m? sheet lu?n ??t b?n ghi m?i nh?t ? ??y khung nh?n.
    requestAnimationFrame(() => {
        const tableWrap = activeSheet?.querySelector('.cnc-history-table-wrap');
        if (tableWrap) tableWrap.scrollTop = tableWrap.scrollHeight;
    });
}

// ==================== LỊCH SỬ CNC - TẢI DẦN THEO THÁNG ====================
const cncHistoryState = {
    initialized: false, machine: MACHINES[0], shift: 'Day Shift', months: [],
    loadedStart: -1, loadedEnd: -1, recordsByMonth: new Map(), search: '', editMode: false,
    dirtyIds: new Set(), nextDraftId: -1
};

function cncMonthBounds(month) {
    const [year, value] = month.split('-').map(Number);
    const from = `${year}-${String(value).padStart(2, '0')}-01`;
    const next = new Date(year, value, 1);
    const to = new Date(next.getTime() - next.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
    return { from, to };
}

async function fetchCncHistoryMonth(index) {
    if (index < 0 || index >= cncHistoryState.months.length) return;
    const month = cncHistoryState.months[index];
    if (cncHistoryState.recordsByMonth.has(month)) return;
    const range = cncMonthBounds(month);
    const params = new URLSearchParams({ machine: cncHistoryState.machine, shift: cncHistoryState.shift,
        fromDate: range.from, toDate: range.to, search: cncHistoryState.search, page: '1', pageSize: '2000' });
    const response = await fetch(`/api/tools/history-page?${params}`);
    if (!response.ok) throw new Error(await response.text() || `HTTP ${response.status}`);
    const result = await response.json();
    cncHistoryState.recordsByMonth.set(month, result.items || []);
}

async function loadCncHistoryMonth(direction) {
    const target = direction === 'older' ? cncHistoryState.loadedStart - 1 : cncHistoryState.loadedEnd + 1;
    if (target < 0 || target >= cncHistoryState.months.length) return;
    cncHistoryState.recordsByMonth = new Map();
    await fetchCncHistoryMonth(target);
    cncHistoryState.loadedStart = cncHistoryState.loadedEnd = target;
    cncHistoryState.editMode = false; cncHistoryState.dirtyIds.clear();
    renderCncHistory();
    requestAnimationFrame(() => {
        const nextWrap = document.querySelector('.cnc-history-table-wrap');
        if (nextWrap) nextWrap.scrollTop = direction === 'older' ? nextWrap.scrollHeight : 0;
    });
}

function applyCncHistoryFilters() {
    cncHistoryState.search = document.getElementById('searchHistory')?.value.trim() || '';
    loadHistory(false, true);
}

function selectCncHistorySheet(machine, shift) {
    cncHistoryState.machine = machine;
    cncHistoryState.shift = shift;
    loadHistory(false, true);
}

async function loadHistory(resetControls = true, resetMonths = false) {
    const container = document.getElementById('historyContent');
    if (!container) return;
    if (resetControls) cncHistoryState.search = '';
    container.innerHTML = '<p class="loading-text">Đang tải dữ liệu...</p>';
    try {
        if (!cncHistoryState.initialized || resetMonths || resetControls) {
            const response = await fetch(`/api/tools/history-months?machine=${encodeURIComponent(cncHistoryState.machine)}&shift=${encodeURIComponent(cncHistoryState.shift)}`);
            if (!response.ok) throw new Error(await response.text() || `HTTP ${response.status}`);
            cncHistoryState.months = await response.json();
            cncHistoryState.recordsByMonth = new Map();
            cncHistoryState.loadedStart = cncHistoryState.loadedEnd = cncHistoryState.months.length - 1;
            if (cncHistoryState.loadedEnd >= 0) await fetchCncHistoryMonth(cncHistoryState.loadedEnd);
            cncHistoryState.initialized = true;
        }
        renderCncHistory();
        requestAnimationFrame(() => {
            const wrap = document.querySelector('.cnc-history-table-wrap');
            if (wrap) wrap.scrollTop = wrap.scrollHeight;
        });
    } catch (error) {
        container.innerHTML = `<p style="color:#e74c3c">❌ Lỗi: ${escapeHtml(error.message)}</p>`;
    }
}

function renderCncHistory() {
    const container = document.getElementById('historyContent');
    const getHistoryTime = item => `${String(item.replaceDate || item.installDate || item.date || '').split('T')[0]}T${item.replaceTime || item.installTime || '00:00:00'}`;
    const records = [...cncHistoryState.recordsByMonth.values()].flat()
        .sort((a, b) => {
            const aDraft = Number(a.id) < 0;
            const bDraft = Number(b.id) < 0;
            if (aDraft !== bDraft) return aDraft ? 1 : -1;
            if (aDraft && bDraft) return Number(b.id) - Number(a.id);
            return getHistoryTime(a).localeCompare(getHistoryTime(b)) || Number(a.id) - Number(b.id);
        });
    const machineNumber = Number((cncHistoryState.machine.match(/(\d+)/) || [0, 0])[1]);
    const shiftCode = cncHistoryState.shift === 'Night Shift' ? 'NS' : 'DS';
    const older = cncHistoryState.loadedStart > 0;
    const newer = cncHistoryState.loadedEnd < cncHistoryState.months.length - 1;
    const loadedLabel = cncHistoryState.loadedStart >= 0
        ? cncHistoryState.months[cncHistoryState.loadedStart] : 'Chưa có dữ liệu';
    const monthButton = (direction, label) => `<button class="cnc-month-load" onclick="loadCncHistoryMonth('${direction}')"><span>＋</span>${label}</button>`;
    let html = `
        <div class="cnc-reason-legend"><span class="reason-me">Mẻ</span><span class="reason-chay">Cháy</span><span class="reason-cun">Cùn</span><span>Cuối ca thay</span><span class="reason-gay">Gãy</span></div>
        <div class="history-toolbar">
            <button class="btn btn-export" onclick="exportToExcel()">📊 Xuất Excel</button>
            <button class="btn btn-primary cnc-history-edit-btn" onclick="toggleCncHistoryEdit()">${cncHistoryState.editMode ? 'Hủy chỉnh sửa' : '✏️ Chỉnh sửa lịch sử'}</button>
            ${cncHistoryState.editMode ? '<button class="btn btn-primary" onclick="addCncHistoryRow()">＋ Thêm bộ 4 dao</button>' : ''}
            ${cncHistoryState.editMode ? '<button class="btn btn-export" onclick="saveCncHistoryEdits()">💾 Lưu thay đổi</button>' : ''}
            <input type="text" id="searchHistory" value="${escapeHtml(cncHistoryState.search)}" placeholder="🔍 MSS, supervisor, lý do..." class="input-field">
            <button class="btn btn-primary" onclick="applyCncHistoryFilters()">Lọc</button>
        </div>
        <div class="cnc-sheet-tabs">${MACHINES.map((machine, index) => `
            <button class="cnc-sheet-tab ${cncHistoryState.machine === machine && cncHistoryState.shift === 'Day Shift' ? 'active' : ''}" style="grid-column:${index + 1};grid-row:1" onclick="selectCncHistorySheet('${machine}','Day Shift')">DS-${machine}</button>
            <button class="cnc-sheet-tab ${cncHistoryState.machine === machine && cncHistoryState.shift === 'Night Shift' ? 'active' : ''}" style="grid-column:${index + 1};grid-row:2" onclick="selectCncHistorySheet('${machine}','Night Shift')">NS-${machine}</button>`).join('')}</div>
        <section class="cnc-history-sheet active"><div class="cnc-history-sheet-title">${shiftCode}-${cncHistoryState.machine} · ${records.length} bản ghi · ${loadedLabel}</div>
        ${older ? monthButton('older', `Tải tháng ${cncHistoryState.months[cncHistoryState.loadedStart - 1]}`) : ''}
        <div class="cnc-history-table-wrap"><table class="cnc-history-excel-table"><thead><tr>
            <th>Ca làm việc</th><th>Supervisor</th><th>MSS</th><th>Máy</th><th>Ngày lắp</th><th>Giờ lắp</th><th>Đầu dao</th><th>Số thứ tự dao</th><th>Đợt cấp</th><th>Ngày thay</th><th>Giờ thay</th><th>Đầu dao</th><th>Giờ thực tế</th><th>Lý do thay</th><th>Loại nguyên liệu</th><th>Loại dao</th><th>Hành động</th>
        </tr></thead><tbody class="historyTableBody">`;
    const editCell = (item, field, value, display = null, type = 'text') => cncHistoryState.editMode
        ? `<td><input class="cnc-history-cell-input" type="${type}" data-id="${item.id}" data-field="${field}" value="${escapeHtml(value ?? '')}" onchange="markCncHistoryDirty(${item.id})" onkeydown="handleCncHistoryCellKey(event)"></td>`
        : `<td>${display === null ? escapeHtml(value ?? '') : display}</td>`;
    for (const item of records) {
        const toolNumber = getCncToolNumber(item);
        html += `<tr data-history-id="${item.id}" class="${getHistoryReasonClass(item.reason)}">
            ${editCell(item, 'shift', item.shift)}${editCell(item, 'supervisor', item.supervisor)}${editCell(item, 'mss', item.mss)}<td>${machineNumber}</td>
            ${editCell(item, 'installDate', String(item.installDate || '').split('T')[0], formatCncHistoryDate(item.installDate), 'date')}
            ${editCell(item, 'installTime', formatCncHistoryTime(item.installTime), formatCncHistoryTime(item.installTime), 'time')}
            ${editCell(item, 'toolPosition', item.toolPosition, null, 'number')}
            ${editCell(item, 'toolNumber', toolNumber, null, 'number')}
            ${editCell(item, 'toolVersion', item.toolVersion, null, 'number')}
            ${editCell(item, 'replaceDate', String(item.replaceDate || '').split('T')[0], formatCncHistoryDate(item.replaceDate), 'date')}
            ${editCell(item, 'replaceTime', formatCncHistoryTime(item.replaceTime), formatCncHistoryTime(item.replaceTime), 'time')}
            <td>${escapeHtml(item.toolPosition ?? '')}</td>
            ${editCell(item, 'actualHours', item.actualHours, null, 'number')}
            ${editCell(item, 'reason', item.reason)}${editCell(item, 'material', item.material)}${editCell(item, 'toolType', item.toolType)}
            <td>${Number(item.id) < 0
                ? `<button class="cnc-history-delete" onclick="removeCncHistoryDraft(${item.id})">✕</button>`
                : `<button class="cnc-history-delete" onclick="undoRecord(${item.id})">🗑️</button>`}</td></tr>`;
    }
    if (cncHistoryState.editMode) {
        html += '<tr class="cnc-history-add-row"><td colspan="17"><button type="button" onclick="addCncHistoryRow()">＋ Thêm bộ 4 dao</button></td></tr>';
    }
    html += `</tbody></table></div>${newer ? monthButton('newer', `Tải tháng ${cncHistoryState.months[cncHistoryState.loadedEnd + 1]}`) : ''}</section>`;
    container.innerHTML = html;
    document.getElementById('searchHistory')?.addEventListener('keydown', event => { if (event.key === 'Enter') applyCncHistoryFilters(); });
}

async function toggleCncHistoryEdit() {
    if (!cncHistoryState.editMode) {
        try {
            // Mỗi lần bắt đầu một lượt chỉnh sửa đều phải xác thực lại.
            // Token vừa nhận vẫn được dùng cho nút Lưu trong cùng lượt này.
            sessionStorage.removeItem('cnc_go_lead_token');
            await cncAuthorization('lead');
        }
        catch (error) { showToast('❌ ' + error.message, 'error'); return; }
    }
    cncHistoryState.editMode = !cncHistoryState.editMode;
    cncHistoryState.dirtyIds.clear();
    if (!cncHistoryState.editMode) {
        for (const [month, rows] of cncHistoryState.recordsByMonth) {
            cncHistoryState.recordsByMonth.set(month, rows.filter(item => Number(item.id) >= 0));
        }
    }
    renderCncHistory();
}

function addCncHistoryRow() {
    if (!cncHistoryState.editMode) return;
    const today = new Date().toISOString().slice(0, 10);
    let month = cncHistoryState.loadedStart >= 0 ? cncHistoryState.months[cncHistoryState.loadedStart] : today.slice(0, 7);
    if (!cncHistoryState.recordsByMonth.has(month)) {
        cncHistoryState.recordsByMonth.set(month, []);
        if (!cncHistoryState.months.includes(month)) cncHistoryState.months.push(month);
        cncHistoryState.months.sort();
        cncHistoryState.loadedStart = cncHistoryState.loadedEnd = cncHistoryState.months.indexOf(month);
    }
    const defaultDate = today.startsWith(month) ? today : `${month}-01`;
    const draftIds = [];
    for (let position = 1; position <= 4; position++) {
        const id = cncHistoryState.nextDraftId--;
        const toolNumber = cncHistoryState.shift === 'Night Shift' ? position + 4 : position;
        cncHistoryState.recordsByMonth.get(month).push({
            id, shift: cncHistoryState.shift, supervisor: '', mss: '', date: defaultDate,
            machineName: cncHistoryState.machine, installDate: defaultDate, installTime: '',
            toolPosition: position, toolAddress: `${cncHistoryState.machine.replaceAll(' ', '')}-Dao${toolNumber}`,
            toolVersion: 1, replaceDate: null, replaceTime: null, actualHours: null,
            reason: '', material: 'PLYWOOD', toolType: 'MỚI'
        });
        cncHistoryState.dirtyIds.add(id);
        draftIds.push(id);
    }
    renderCncHistory();
    requestAnimationFrame(() => {
        const wrap = document.querySelector('.cnc-history-table-wrap');
        if (wrap) wrap.scrollTop = wrap.scrollHeight;
        document.querySelector(`tr[data-history-id="${draftIds[0]}"] input`)?.focus();
    });
}

function removeCncHistoryDraft(id) {
    for (const [month, rows] of cncHistoryState.recordsByMonth) {
        cncHistoryState.recordsByMonth.set(month, rows.filter(item => Number(item.id) !== Number(id)));
    }
    cncHistoryState.dirtyIds.delete(Number(id));
    renderCncHistory();
}

function markCncHistoryDirty(id) {
    cncHistoryState.dirtyIds.add(Number(id));
    document.querySelector(`tr[data-history-id="${id}"]`)?.classList.add('cnc-history-row-dirty');
}

function handleCncHistoryCellKey(event) {
    if (event.key !== 'Enter') return;
    event.preventDefault();
    const input = event.currentTarget;
    const cellIndex = input.closest('td').cellIndex;
    const nextRow = input.closest('tr').nextElementSibling;
    nextRow?.cells[cellIndex]?.querySelector('input')?.focus();
}

async function saveCncHistoryEdits() {
    const rows = [...cncHistoryState.dirtyIds].map(id => {
        const row = document.querySelector(`tr[data-history-id="${id}"]`);
        const values = Object.fromEntries([...row.querySelectorAll('input[data-field]')].map(input => [input.dataset.field, input.value.trim()]));
        const source = [...cncHistoryState.recordsByMonth.values()].flat().find(item => Number(item.id) === Number(id));
        return { id, ...values, machineName: cncHistoryState.machine,
            date: String(source?.date || values.replaceDate || values.installDate || '').split('T')[0],
            toolPosition: Number(values.toolPosition || 0), toolNumber: Number(values.toolNumber || 0),
            toolVersion: Number(values.toolVersion || 0), actualHours: values.actualHours === '' ? null : Number(values.actualHours) };
    });
    if (!rows.length) { showToast('Chưa có ô nào được thay đổi', 'error'); return; }
    try {
        const response = await fetch('/api/tools/history/batch-update', { method:'PUT', headers:{'Content-Type':'application/json','Authorization':await cncAuthorization('lead')}, body:JSON.stringify({rows}) });
        const result = await response.json().catch(() => ({}));
        if (!response.ok) throw new Error(result.detail || result.title || 'Không lưu được thay đổi');
        showToast('✅ ' + result.message, 'success');
        cncHistoryState.editMode = false; cncHistoryState.dirtyIds.clear();
        cncHistoryState.recordsByMonth = new Map(); await fetchCncHistoryMonth(cncHistoryState.loadedStart); renderCncHistory();
    } catch (error) { showToast('❌ ' + error.message, 'error'); }
}
async function undoRecord(id) {
    let reason;
    if (cncHistoryState.editMode) {
        if (!confirm('Bạn có chắc muốn xóa dòng lịch sử này?')) return;
        reason = 'Xóa trong chế độ chỉnh sửa lịch sử';
    } else {
        reason = prompt('Nhập lý do hoàn tác (bắt buộc lưu audit log):');
        if (reason === null || !reason.trim()) return;
    }
    try {
        const response = await fetch(`/api/tools/change/${id}/undo`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'Authorization': await cncAuthorization('manager') }, body: JSON.stringify({ reason: reason.trim() }) });
        const result = await response.json();
        if (!response.ok) throw new Error(result?.detail || 'Không thể hoàn tác');
        showToast(result.message, 'success'); loadHistory(false, true); loadDashboard();
    } catch (error) { showToast('❌ ' + error.message, 'error'); }
}

async function cncAuthorization(role) {
    if (!['manager', 'lead'].includes(role)) throw new Error('Quyền đăng nhập không hợp lệ.');
    const cacheKey = `cnc_go_${role}_token`;
    let token = sessionStorage.getItem(cacheKey);
    if (token) return `Bearer ${token}`;
    const password = prompt(role === 'manager' ? 'Nhập mật khẩu Manager:' : 'Nhập mật khẩu Lead/Supervisor:');
    if (!password) throw new Error('Cần đăng nhập để thực hiện thao tác này');
    const response = await fetch('/api/cnc/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ role, password }) });
    if (!response.ok) throw new Error('Mật khẩu không đúng');
    const result = await response.json(); sessionStorage.setItem(cacheKey, result.token);
    return `Bearer ${result.token}`;
}
function exportToExcel() {
    window.location.href = '/api/tools/export';
}

// ==================== BÁO CÁO SUPPLIER ====================
let supplierPerformanceChart = null;

function escapeHtml(value) {
    return String(value ?? '')
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#039;');
}

function getSupplierDisplayName(code) {
    return code;
}

function getSupplierToolNumber(toolAddress) {
    const match = String(toolAddress || '').match(/Dao(\d+)$/i);
    return match ? match[1] : String(toolAddress || '');
}

async function loadSupplierReport() {
    const container = document.getElementById('supplierReportContent');
    if (!container) return;

    container.innerHTML = '<p class="loading-text">Đang tải dữ liệu...</p>';

    try {
        const response = await fetch('/api/tools/supplier-performance');
        if (!response.ok) {
            const errorText = await response.text();
            throw new Error(errorText || `HTTP ${response.status}`);
        }

        const data = await response.json();
        if (!Array.isArray(data)) {
            throw new Error('Dữ liệu báo cáo Supplier không hợp lệ.');
        }

        if (data.length === 0) {
            container.innerHTML = '<div class="supplier-report-card"><p class="loading-text">Chưa có dữ liệu của An Bình hoặc Trang Tuyển.</p></div>';
            return;
        }

        container.innerHTML = `
            <div class="supplier-report-card">
                <h2>Tổng thời gian chạy theo Supplier, máy và dao</h2>
                <div class="supplier-chart-wrap">
                    <canvas id="supplierPerformanceChart"></canvas>
                </div>
            </div>
            <div class="supplier-report-card">
                <h2>Chi tiết dữ liệu</h2>
                <div style="overflow-x: auto;">
                    <table>
                        <thead>
                            <tr>
                                <th>Supplier</th>
                                <th>Máy</th>
                                <th>Dao số</th>
                                <th>Tổng thời gian chạy</th>
                            </tr>
                        </thead>
                        <tbody>
                            ${data.map(item => `
                                <tr>
                                    <td style="font-weight: bold;">${escapeHtml(getSupplierDisplayName(item.supplier))}</td>
                                    <td>${escapeHtml(item.machineName)}</td>
                                    <td style="text-align: center;">${escapeHtml(getSupplierToolNumber(item.toolNumber))}</td>
                                    <td style="font-weight: bold; color: #3498db;">${Number(item.totalHours).toLocaleString('vi-VN')} giờ</td>
                                </tr>
                            `).join('')}
                        </tbody>
                    </table>
                </div>
            </div>`;

        renderSupplierPerformanceChart(data);
    } catch (error) {
        container.innerHTML = `<p style="color: #e74c3c;">❌ Lỗi: ${escapeHtml(error.message)}</p>`;
    }
}

function renderSupplierPerformanceChart(data) {
    const canvas = document.getElementById('supplierPerformanceChart');
    if (!canvas || typeof Chart === 'undefined') return;

    if (supplierPerformanceChart) supplierPerformanceChart.destroy();

    const suppliers = [...new Set(data.map(item => item.supplier))];
    const colors = ['#3498db', '#e74c3c', '#2ecc71', '#f1c40f', '#9b59b6', '#1abc9c'];

    supplierPerformanceChart = new Chart(canvas, {
        type: 'bar',
        data: {
            labels: data.map(item => `${getSupplierDisplayName(item.supplier)} - ${item.machineName} - Dao ${getSupplierToolNumber(item.toolNumber)}`),
            datasets: [{
                label: 'Tổng thời gian chạy',
                data: data.map(item => item.totalHours),
                backgroundColor: data.map(item => colors[suppliers.indexOf(item.supplier) % colors.length])
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            scales: { y: { beginAtZero: true, title: { display: true, text: 'Tổng thời gian chạy (giờ)' } } },
            plugins: { legend: { display: false } }
        }
    });
}
// ==================== TOAST ====================
function showToast(message, type = 'info') {
    const toast = document.createElement('div');
    toast.className = `toast toast-${type}`;
    toast.innerHTML = `
        <span class="toast-icon">${type === 'success' ? '✓' : '✗'}</span>
        <span class="toast-message">${message}</span>
        <button class="toast-close" onclick="this.parentElement.remove()">×</button>
    `;

    let container = document.querySelector('.toast-container');
    if (!container) {
        container = document.createElement('div');
        container.className = 'toast-container';
        document.body.appendChild(container);
    }

    container.appendChild(toast);
    setTimeout(() => toast.remove(), 5000);
}

// ==================== MODAL HELPERS ====================
function openModal(modal) {
    if (modal) modal.classList.add('active');
}

function closeModal(modal) {
    if (modal) modal.classList.remove('active');
}

// ==================== SIGNALR ====================
function initSignalR() {
    if (!window.signalR) return;

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/orderHub")
        .build();

    connection.on("ToolStatusUpdated", (data) => {
        console.log('Tool status updated:', data);
        const dashboardTab = document.getElementById('tab-dashboard');
        if (dashboardTab && dashboardTab.classList.contains('active')) {
            loadDashboard();
        }
    });

    connection.start().catch(err => console.error('SignalR error:', err));
}

// Trả về text hiển thị cho máy theo Ca
function getMachineDisplayName(machine) {
    const shift = document.getElementById('shift').value;
    const prefix = (shift === 'Night Shift') ? 'NS' : 'DS';  // mặc định Day = DS
    return `${prefix}-${machine}`; // Ví dụ: DS-Heian 4, NS-Heian 4
}

// Dựng lại danh sách máy theo Ca hiện tại, giữ nguyên máy đang chọn
function rebuildMachineOptions() {
    const select = document.getElementById('machine');
    const currentValue = select.value; // máy đang chọn (vd "Heian 7")

    // Xóa option cũ
    select.innerHTML = '<option value="">-- Chọn máy --</option>';

    MACHINES.forEach(machine => {
        const option = document.createElement('option');
        option.value = machine;                        // GIÁ TRỊ LƯU VẪN LÀ "Heian 4"
        option.textContent = getMachineDisplayName(machine); // HIỂN THỊ: DS-Heian 4 / NS-Heian 4
        select.appendChild(option);
    });

    // Nếu trước đó đã chọn máy, set lại để không mất lựa chọn
    if (currentValue) {
        select.value = currentValue;
    }
}

// ==================== SUPPLIER LOGIC ====================
function populateSupplierDatalist() {
    const select = document.getElementById('supplierInput');
    if (select && select.options.length !== SUPPLIERS.length + 1) {
        select.innerHTML = '<option value="">-- Không chọn Supplier --</option>'
            + SUPPLIERS.map(s => `<option value="${escapeHtml(s)}">${escapeHtml(s)}</option>`).join('');
    }
}

function handleToolTypeChange(toolIndex) {
    const toolTypeSelect = document.getElementById(`toolType${toolIndex}`);
    if (!toolTypeSelect) return;

    const isSharpenedTool = ['MÀI LẦN 1', 'MÀI LẦN 2', 'MÀI LẦN 3'].includes(toolTypeSelect.value);
    const supplierHiddenInput = document.getElementById(`supplier${toolIndex}`);

    if (!isSharpenedTool) {
        if (supplierHiddenInput) supplierHiddenInput.value = '';
        return;
    }

    currentToolIndexForSupplier = toolIndex;
    const modal = document.getElementById('modalSupplier');
    const supplierInput = document.getElementById('supplierInput');
    supplierInput.value = supplierHiddenInput ? supplierHiddenInput.value : '';
    populateSupplierDatalist();
    positionSupplierPopover(toolTypeSelect);
    openModal(modal);
    setTimeout(() => supplierInput.focus(), 0);
}

function positionSupplierPopover(anchorElement) {
    const popover = document.querySelector('#modalSupplier .supplier-popover-content');
    if (!popover || !anchorElement) return;

    const rect = anchorElement.getBoundingClientRect();
    const popoverWidth = 320;
    const estimatedHeight = 245;
    const gap = 10;

    let left = rect.right + gap;
    if (left + popoverWidth > window.innerWidth - 8) {
        left = rect.left - popoverWidth - gap;
    }

    let top = rect.top;
    if (top + estimatedHeight > window.innerHeight - 8) {
        top = window.innerHeight - estimatedHeight - 8;
    }

    popover.style.left = `${Math.max(8, left)}px`;
    popover.style.top = `${Math.max(8, top)}px`;
}
function confirmSupplier() {
    if (currentToolIndexForSupplier === null) return;

    const supplierInput = document.getElementById('supplierInput');
    const supplierName = supplierInput.value.trim();
    if (supplierName && !SUPPLIERS.includes(supplierName)) {
        showToast('Supplier chỉ có thể là An Bình hoặc Trang Tuyển', 'error');
        return;
    }

    let supplierHiddenInput = document.getElementById(`supplier${currentToolIndexForSupplier}`);
    if (!supplierHiddenInput) {
        supplierHiddenInput = document.createElement('input');
        supplierHiddenInput.type = 'hidden';
        supplierHiddenInput.id = `supplier${currentToolIndexForSupplier}`;
        document.body.appendChild(supplierHiddenInput);
    }
    supplierHiddenInput.value = supplierName;

    closeModal(document.getElementById('modalSupplier'));
    showToast(supplierName
        ? `Đã gán Supplier "${supplierName}" cho dao ${currentToolIndexForSupplier}`
        : `Đã để trống Supplier cho dao ${currentToolIndexForSupplier}`, 'success');
    currentToolIndexForSupplier = null;
}

function cancelSupplier() {
    if (currentToolIndexForSupplier === null) return;

    const supplierHiddenInput = document.getElementById(`supplier${currentToolIndexForSupplier}`);
    if (supplierHiddenInput) supplierHiddenInput.value = '';
    closeModal(document.getElementById('modalSupplier'));
    currentToolIndexForSupplier = null;
}

function clearAllRows() {
    // Xóa supplier ẩn, nếu có
    for (let i = 1; i <= 4; i++) {
        const supplierHiddenInput = document.getElementById(`supplier${i}`);
        if (supplierHiddenInput) {
            supplierHiddenInput.value = '';
        }
    }
    
    // Nếu muốn xóa hết dữ liệu người nhập trước khi setDefaultDateTime:
    // for (let i = 1; i <= 4; i++) {
    //     document.getElementById(`replaceDate${i}`).value = '';
    //     document.getElementById(`replaceTime${i}`).value = '';
    //     document.getElementById(`actualHours${i}`).value = '0';
    //     document.getElementById(`reason${i}`).value = '';
    //     document.getElementById(`installDate${i}`).value = '';
    //     document.getElementById(`installTime${i}`).value = '';
    //     document.getElementById(`toolType${i}`).value = 'MỚI';
    // }
}

function isTimeInRange(timeStr, fromStr, toStr) {
    // timeStr, fromStr, toStr: "HH:mm"
    if (!timeStr) return false;
    const [th, tm] = timeStr.split(':').map(Number);
    const [fh, fm] = fromStr.split(':').map(Number);
    const [ph, pm] = toStr.split(':').map(Number);

    const total = th * 60 + tm;
    const from  = fh * 60 + fm;
    const to    = ph * 60 + pm;

    return total >= from && total <= to;
}
