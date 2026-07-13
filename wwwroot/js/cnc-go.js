// ==================== CNC GO - TOOL MANAGEMENT (4 DAO CỐ ĐỊNH) ====================

const MACHINES = [];
for (let i = 4; i <= 21; i++) {
    MACHINES.push(`Heian ${i}`);
}

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

// 3. MSS giữ nguyên bằng localStorage
function initMssPersist() {
    const mssInput = document.getElementById('mss');
    const savedMss = localStorage.getItem('cnc_mss');
    if (savedMss) {
        mssInput.value = savedMss;
    }

    mssInput.addEventListener('change', () => {
        localStorage.setItem('cnc_mss', mssInput.value || "");
    });
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
    const machineSel = document.getElementById('machine');
    const supervisorSelect = document.getElementById('supervisor');
    const machineVal = machineSel.value;

    if (!machineVal) {
        // Không đổi gì nếu chưa chọn máy
        return;
    }

    // Xác định nhóm máy
    const num = parseInt(machineVal.replace("Heian", "").trim(), 10);
    let suggestedList = [];

    if (num >= 4 && num <= 14) {
        suggestedList = SUPERVISORS.group_4_14;  // Hùng, Kỳ
    } else if (num >= 15 && num <= 21) {
        suggestedList = SUPERVISORS.group_15_21; // Đặng, Vấn
    }

    if (suggestedList.length === 0) {
        // Không có gợi ý đặc biệt → giữ nguyên Sup hiện tại
        return;
    }

    // ⭐ Chỉ tự động gợi ý Sup nếu hiện tại vẫn đang ở chế độ auto
    //   (tức là người dùng chưa tự chọn Sup)
    if (supervisorSelect.dataset.auto === "true") {
        // Nếu Sup hiện tại không nằm trong nhóm gợi ý, chọn gợi ý đầu tiên
        if (!suggestedList.includes(supervisorSelect.value)) {
            supervisorSelect.value = suggestedList[0];
        }
        // Vẫn giữ dataset.auto = "true" để lần sau nếu đổi máy khác,
        // có thể gợi ý lại (trừ khi người dùng tự đổi Sup).
    }

    // Nếu dataset.auto === "false" → người dùng đã tự chọn Sup,
    //   nên KHÔNG tự đổi Sup nữa khi chọn máy.
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

        // Giờ thực tế mặc định 0 (công nhân sẽ sửa theo controller)
        document.getElementById(`actualHours${i}`).value = "0";

        // Lý do thay mặc định Cuối ca thay
        document.getElementById(`reason${i}`).value = "Cuối ca thay";

        // Loại dao mặc định MỚI
        document.getElementById(`toolType${i}`).value = "MỚI";
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
        const actualHours = parseInt(document.getElementById(`actualHours${i}`).value);
        const reason = document.getElementById(`reason${i}`).value;
        const installDate = document.getElementById(`installDate${i}`).value || null;
        const installTime = document.getElementById(`installTime${i}`).value || null;
        const toolType = document.getElementById(`toolType${i}`).value;
        const material = "PLYWOOD";

        // Nếu không có lý do thay → bỏ qua dao đó
        if (!reason) continue;

        toolsData.push({
            machineName: machine,
            shift: shift,
            toolPosition: i,
            supervisor: supervisor,
            mss: mss,
            date: date,
            toolType: toolType,
            installDate: installDate,
            installTime: installTime,
            replaceDate: replaceDate,
            replaceTime: replaceTime,
            actualHours: actualHours,
            reason: reason,
            material: material
        });
    }

    if (toolsData.length === 0) {
        showToast('❌ Vui lòng chọn ít nhất 1 lý do thay dao', 'error');
        return;
    }

    let successCount = 0;
    for (const toolData of toolsData) {
        try {
            const response = await fetch('/api/tools/change', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(toolData)
            });

            if (response.ok) {
                successCount++;
            } else {
                let errorMessage = `HTTP ${response.status}`;
                try {
                    const text = await response.text();      // lấy raw text
                    if (text) errorMessage += ` - ${text}`;
                } catch (_) {
                    // bỏ qua
                }
                showToast(`❌ Lỗi lưu dao ${toolData.toolPosition}: ${errorMessage}`, 'error');
            }
        } catch (error) {
            showToast(`❌ Lỗi kết nối: ${error.message}`, 'error');
        }
    }

    if (successCount > 0) {
        showToast(`✅ Đã lưu thành công ${successCount} dao`, 'success');
        clearAllRows();
        setDefaultDateTime();  // reset lại mặc định mới
        loadDashboard();
        loadHistory();
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
}

// ==================== DASHBOARD "QUẢN LÝ" ====================
async function loadDashboard() {
    const container = document.getElementById('dashboardContent');
    if (!container) return;

    container.innerHTML = '<p class="loading-text">Đang tải dữ liệu...</p>';

    try {
        const response = await fetch('/api/tools/status');
        const data = await response.json();

        const dayShift = data.filter(t => t.shift === 'Day Shift');
        const nightShift = data.filter(t => t.shift === 'Night Shift');

        let html = '<div style="display: grid; grid-template-columns: 1fr 1fr; gap: 30px;">';

        html += '<div>';
        html += '<h2 style="text-align: center; margin-bottom: 20px; color: #f39c12;">☀️ Quản lý dao - Ca ngày (Dao 1 → 4)</h2>';
        html += renderMachineGrid(dayShift, 'day');
        html += '</div>';

        html += '<div>';
        html += '<h2 style="text-align: center; margin-bottom: 20px; color: #9b59b6;">🌙 Quản lý dao - Ca đêm (Dao 5 → 8)</h2>';
        html += renderMachineGrid(nightShift, 'night');
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
        if (!machineMap[item.machineName]) {
            machineMap[item.machineName] = {};
        }
        machineMap[item.machineName][item.toolPosition] = item;
    });

    let html = '<div style="display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 15px;">';

    MACHINES.forEach(machine => {
        // ✅ Thêm prefix DS- hoặc NS- chỉ để hiển thị
        const prefix = shiftType === 'night' ? 'NS' : 'DS';
        const displayName = `${prefix}-${machine}`;

        html += '<div style="background: #1a1a1a; border: 1px solid #333; border-radius: 8px; padding: 15px;">';
        html += `<h3 style="text-align: center; margin-bottom: 15px; color: #a8edea;">${displayName}</h3>`;

        for (let pos = 1; pos <= 4; pos++) {
            const tool = machineMap[machine]?.[pos];
            const version = tool?.currentVersion || '-';
            const hours = tool?.totalHours || '';

            // DS: Dao 1–4 ; NS: Dao 5–8
            const displayIndex = shiftType === 'night' ? pos + 4 : pos;

            html += '<div style="display: flex; justify-content: space-between; padding: 8px; border-bottom: 1px solid #2c3e50;">';
            html += `<span style="color: #bdc3c7;">Dao số ${displayIndex}</span>`;
            html += `<span><strong style="color: #f39c12;">${version}</strong> ${
                hours ? `<span style="color: #95a5a6; font-size: 12px;">${hours}h</span>` : ''
            }</span>`;
            html += '</div>';
        }

        html += '</div>';
    });

    html += '</div>';
    return html;
}

// ==================== LỊCH SỬ (giữ nguyên như trước) ====================
async function loadHistory() {
    const container = document.getElementById('historyContent');
    if (!container) return;

    container.innerHTML = '<p class="loading-text">Đang tải dữ liệu...</p>';

    try {
        const response = await fetch('/api/tools/history');
        const data = await response.json();

        let html = `
            <div style="margin-bottom: 20px; display: flex; gap: 15px;">
                <button class="btn btn-export" onclick="exportToExcel()">📊 Xuất Excel</button>
                <input type="text" id="searchHistory" placeholder="🔍 Tìm kiếm..." style="flex: 1;" class="input-field">
            </div>
            <div style="overflow-x: auto;">
                <table>
                    <thead>
                        <tr>
                            <th>Ca</th>
                            <th>Máy</th>
                            <th>Vị trí</th>
                            <th>Version</th>
                            <th>Loại dao</th>
                            <th>Ngày thay</th>
                            <th>Giờ thực tế</th>
                            <th>Lý do</th>
                            <th>Hành động</th>
                        </tr>
                    </thead>
                    <tbody id="historyTableBody">
        `;

        data.forEach(item => {
            html += `
                <tr>
                    <td>${item.shift}</td>
                    <td>${item.machineName}</td>
                    <td>${item.toolPosition}</td>
                    <td style="font-weight: bold; color: #f39c12;">${item.toolVersion}</td>
                    <td>${item.toolType}</td>
                    <td>${item.replaceDate ? new Date(item.replaceDate).toLocaleDateString('vi-VN') : '-'}</td>
                    <td style="font-weight: bold; color: #3498db;">${item.actualHours}h</td>
                    <td>${item.reason}</td>
                    <td><button class="btn btn-secondary" style="padding: 5px 10px; font-size: 12px;" onclick="deleteRecord(${item.id})">🗑️</button></td>
                </tr>
            `;
        });

        html += '</tbody></table></div>';
        container.innerHTML = html;

        document.getElementById('searchHistory').addEventListener('input', (e) => {
            const searchText = e.target.value.toLowerCase();
            const rows = document.querySelectorAll('#historyTableBody tr');
            rows.forEach(row => {
                const text = row.textContent.toLowerCase();
                row.style.display = text.includes(searchText) ? '' : 'none';
            });
        });
    } catch (error) {
        container.innerHTML = `<p style="color: #e74c3c;">❌ Lỗi: ${error.message}</p>`;
    }
}

async function deleteRecord(id) {
    if (!confirm('Bạn có chắc muốn xóa bản ghi này?')) return;

    try {
        const response = await fetch(`/api/tools/change/${id}`, { method: 'DELETE' });
        const result = await response.json();

        if (response.ok) {
            showToast(result.message, 'success');
            loadHistory();
            loadDashboard();
        } else {
            showToast('❌ Lỗi xóa dữ liệu', 'error');
        }
    } catch (error) {
        showToast('❌ Lỗi: ' + error.message, 'error');
    }
}

function exportToExcel() {
    window.location.href = '/api/tools/export';
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
