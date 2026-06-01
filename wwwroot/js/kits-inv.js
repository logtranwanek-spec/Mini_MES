document.addEventListener('DOMContentLoaded', () => {
    const mainInput = document.getElementById('mainInput');
    const statusText = document.getElementById('statusText');
    const actionButtons = document.getElementById('actionButtons');
    const btnCheckout = document.getElementById('btnCheckout');
    const btnCancel = document.getElementById('btnCancel');
    const mapCanvas = document.getElementById('kitsInvMapCanvas');
    
    let currentMx = "";
    let appMode = "IDLE"; // IDLE, PLACING, FOUND

    // ================= BỘ NÃO VẼ BẢN ĐỒ =================
    function initMap() {
        mapCanvas.innerHTML = ''; // Xóa bản đồ cũ
        
        // 🚀 BẠN CÓ THỂ TỰ DO CHỈNH SỬA TỌA ĐỘ VÀ KÍCH THƯỚC Ở ĐÂY 🚀
        // Cú pháp: { id: "Tên_Ô", text: "Chữ_hiện_ra", type: "loại_cart", top: "cách_mép_trên", left: "cách_mép_trái", width: "độ_rộng", height: "độ_cao" }

        const layout = [
            // Hàng Cover Carts (Màu đỏ)
            /*{ id: "COVER-1", text: "COVER1", type: "cover-cart", top: "2%", left: "2%", width: "6%", height: "15%" },
            { id: "COVER-2", text: "COVER2", type: "cover-cart", top: "2%", left: "9%", width: "6%", height: "15%" },
            { id: "COVER-3", text: "COVER3", type: "cover-cart", top: "2%", left: "16%", width: "6%", height: "15%" },
            { id: "COVER-4", text: "COVER4", type: "cover-cart", top: "2%", left: "23%", width: "6%", height: "15%" },
            { id: "COVER-5", text: "COVER5", type: "cover-cart", top: "2%", left: "30%", width: "6%", height: "15%" },
            { id: "COVER-6", text: "COVER6", type: "cover-cart", top: "2%", left: "37%", width: "6%", height: "15%" },            
            { id: "COVER-7", text: "COVER7", type: "cover-cart", top: "2%", left: "44%", width: "6%", height: "15%" },*/

            { id: "R1-C1", text: "A1", type: "parts-cart", top: "28%", left: "15%", width: "6%", height: "15%" },
            { id: "R1-C2", text: "A2", type: "parts-cart", top: "28%", left: "24%", width: "6%", height: "15%" },
            { id: "R1-C3", text: "A3", type: "parts-cart", top: "28%", left: "33%", width: "6%", height: "15%" },
            { id: "R1-C4", text: "A4", type: "parts-cart", top: "28%", left: "42%", width: "6%", height: "15%" },
            { id: "R1-C5", text: "A5", type: "parts-cart", top: "28%", left: "51%", width: "6%", height: "15%" },
            { id: "R1-C6", text: "A6", type: "parts-cart", top: "28%", left: "60%", width: "6%", height: "15%" },
            { id: "R1-C7", text: "A7", type: "parts-cart", top: "28%", left: "69%", width: "6%", height: "15%" },
            { id: "R1-C8", text: "A8", type: "parts-cart", top: "28%", left: "78%", width: "6%", height: "15%" },
            { id: "R1-C9", text: "A9", type: "parts-cart", top: "28%", left: "87%", width: "6%", height: "15%" },

            { id: "R2-C1", text: "B1", type: "parts-cart", top: "46%", left: "15%", width: "6%", height: "15%" },
            { id: "R2-C2", text: "B2", type: "parts-cart", top: "46%", left: "24%", width: "6%", height: "15%" },
            { id: "R2-C3", text: "B3", type: "parts-cart", top: "46%", left: "33%", width: "6%", height: "15%" },
            { id: "R2-C4", text: "B4", type: "parts-cart", top: "46%", left: "42%", width: "6%", height: "15%" },
            { id: "R2-C5", text: "B5", type: "parts-cart", top: "46%", left: "51%", width: "6%", height: "15%" },
            { id: "R2-C6", text: "B6", type: "parts-cart", top: "46%", left: "60%", width: "6%", height: "15%" },
            { id: "R2-C7", text: "B7", type: "parts-cart", top: "46%", left: "69%", width: "6%", height: "15%" },
            { id: "R2-C8", text: "B8", type: "parts-cart", top: "46%", left: "78%", width: "6%", height: "15%" },
            { id: "R2-C9", text: "B9", type: "parts-cart", top: "46%", left: "87%", width: "6%", height: "15%" },

            { id: "R3-C1", text: "C1", type: "parts-cart", top: "64%", left: "15%", width: "6%", height: "15%" },
            { id: "R3-C2", text: "C2", type: "parts-cart", top: "64%", left: "24%", width: "6%", height: "15%" },
            { id: "R3-C3", text: "C3", type: "parts-cart", top: "64%", left: "33%", width: "6%", height: "15%" },
            { id: "R3-C4", text: "C4", type: "parts-cart", top: "64%", left: "42%", width: "6%", height: "15%" },
            { id: "R3-C5", text: "C5", type: "parts-cart", top: "64%", left: "51%", width: "6%", height: "15%" },
            { id: "R3-C6", text: "C6", type: "parts-cart", top: "64%", left: "60%", width: "6%", height: "15%" },
            { id: "R3-C7", text: "C7", type: "parts-cart", top: "64%", left: "69%", width: "6%", height: "15%" },
            { id: "R3-C8", text: "C8", type: "parts-cart", top: "64%", left: "78%", width: "6%", height: "15%" },
            { id: "R3-C9", text: "C9", type: "parts-cart", top: "64%", left: "87%", width: "6%", height: "15%" },

            { id: "R4-C1", text: "D1", type: "parts-cart", top: "82%", left: "15%", width: "6%", height: "15%" },
            { id: "R4-C2", text: "D2", type: "parts-cart", top: "82%", left: "24%", width: "6%", height: "15%" },
            { id: "R4-C3", text: "D3", type: "parts-cart", top: "82%", left: "33%", width: "6%", height: "15%" },
            { id: "R4-C4", text: "D4", type: "parts-cart", top: "82%", left: "42%", width: "6%", height: "15%" },
            { id: "R4-C5", text: "D5", type: "parts-cart", top: "82%", left: "51%", width: "6%", height: "15%" },
            { id: "R4-C6", text: "D6", type: "parts-cart", top: "82%", left: "60%", width: "6%", height: "15%" },
            { id: "R4-C7", text: "D7", type: "parts-cart", top: "82%", left: "69%", width: "6%", height: "15%" },
            { id: "R4-C8", text: "D8", type: "parts-cart", top: "82%", left: "78%", width: "6%", height: "15%" },
            { id: "R4-C9", text: "D9", type: "parts-cart", top: "82%", left: "87%", width: "6%", height: "15%" }
        ];
        
        layout.forEach(item => {
            const slot = document.createElement('div');
            slot.className = `kit-slot ${item.type}`;
            slot.id = item.id;
            slot.textContent = item.text;
            slot.style.top = item.top;
            slot.style.left = item.left;
            slot.style.width = item.width;
            slot.style.height = item.height;

            if (item.type !== 'obstacle') {
                slot.dataset.slotId = item.id;
                slot.addEventListener('click', () => handleSlotClick(slot));
            }
            mapCanvas.appendChild(slot);
        });
    }

    // ... (Toàn bộ code còn lại của file kits-inv.js giữ nguyên không đổi)
    async function loadInventory() {
        document.querySelectorAll('.kit-slot').forEach(slot => {
            if(slot.classList.contains('occupied')) {
                slot.classList.remove('occupied');
                slot.textContent = slot.classList.contains('cover-cart') ? 'Cover cart' : 'PARTS CART';
            }
        });

        try {
            const res = await fetch('/api/kits-inv/inventory');
            const data = await res.json();
            
            data.forEach(item => {
                const slotEl = document.getElementById(item.zoneCode.toUpperCase());
                if (slotEl) {
                    slotEl.classList.add('occupied');
                    slotEl.textContent = item.odrNo;
                }
            });
        } catch (err) { console.error('Lỗi tải bản đồ'); }
    }

    async function handleSlotClick(slotEl) {
        if (appMode === "IDLE") return;
        if (slotEl.classList.contains('occupied')) {
            showToast('Ô này đã có xe hàng!', 'warning');
            return;
        }

        const clickedZone = slotEl.dataset.slotId;
        try {
            const res = await fetch('/api/kits-inv/scan', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ odrno: currentMx, zoneCode: clickedZone })
            });
            const data = await res.json();
            if (res.ok) { showToast(data.message, 'success'); resetUI(); } 
            else { showToast(data.title || 'Lỗi không xác định', 'error'); }
        } catch (err) { showToast('Lỗi kết nối', 'error'); }
    }
    
    mainInput.addEventListener('keypress', async (e) => {
        if (e.key === 'Enter' && mainInput.value.trim() !== '') {
            currentMx = mainInput.value.trim().toUpperCase();
            mainInput.disabled = true;
            document.querySelectorAll('.kit-slot').forEach(s => s.classList.remove('found-blink'));
            mapCanvas.classList.remove('placing-mode');

            try {
                const res = await fetch(`/api/kits-inv/find?odrno=${currentMx}`);
                if (res.ok) {
                    const data = await res.json();
                    appMode = "FOUND";
                    statusText.innerHTML = `Mã <span class="kits-inv-status-highlight">${currentMx}</span> đang ở vị trí <span class="kits-inv-status-highlight">${data.zoneCode}</span>. <br><span class="kits-inv-status-action">Bấm XUẤT KHO hoặc click vào ô trống khác để dời chỗ.</span>`;
                    actionButtons.style.display = "flex";
                    const targetSlot = document.getElementById(data.zoneCode);
                    if (targetSlot) targetSlot.classList.add('found-blink');
                    mapCanvas.classList.add('placing-mode');
                } else {
                    appMode = "PLACING";
                    statusText.innerHTML = `Mã <span class="kits-inv-status-highlight">${currentMx}</span> chưa có trong kho. <br><span class="kits-inv-status-action">👉 Hãy CLICK vào một ô trống trên bản đồ để cất hàng.</span>`;
                    actionButtons.style.display = "flex";
                    btnCheckout.style.display = "none";
                    mapCanvas.classList.add('placing-mode');
                }
            } catch (err) { resetUI(); }
        }
    });

    btnCheckout.addEventListener('click', async () => {
        try {
            const res = await fetch('/api/kits-inv/out', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ odrno: currentMx, zoneCode: "" })
            });
            const data = await res.json();
            if (res.ok) { showToast(data.message, 'success'); resetUI(); } 
            else { showToast(data.title || 'Lỗi không xác định', 'error'); }
        } catch (err) { showToast('Lỗi kết nối', 'error'); }
    });

    btnCancel.addEventListener('click', resetUI);

    function resetUI() {
        appMode = "IDLE";
        currentMx = "";
        mainInput.value = "";
        mainInput.disabled = false;
        statusText.innerHTML = "Vui lòng quét mã MX để bắt đầu.";
        actionButtons.style.display = "none";
        btnCheckout.style.display = "block";
        document.querySelectorAll('.kit-slot').forEach(s => s.classList.remove('found-blink'));

        loadInventory();
        mainInput.focus();
    }


    function showToast(message, type = 'info') {
        const toast = document.createElement('div');
        toast.className = `toast toast-${type}`;
        const icons = { 'success': '✅', 'error': '❌', 'warning': '⚠️', 'info': 'ℹ️' };
        toast.innerHTML = `<div class="toast-icon">${icons[type]}</div><div class="toast-message">${message}</div>`;
        document.getElementById('toastContainer').appendChild(toast);
        setTimeout(() => { toast.style.opacity = '0'; setTimeout(() => toast.remove(), 3000); }, 3000);
    }

    initMap();
    loadInventory();
    mainInput.focus();
});
