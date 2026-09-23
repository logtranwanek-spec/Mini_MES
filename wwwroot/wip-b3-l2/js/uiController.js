/**
 * UI Controller - Storage Layout Focus with Shelf Detail Card
 * Handles all UI rendering and user interactions
 */

const UIController = {
    elements: {
        searchMOInput: null,
        searchMOBtn: null,
        addMOInput: null,
        addMOBtn: null,
        heroPanelBody: null,
        heroPanelEmpty: null,
        heroPanelCards: null,
        heroPanelFooter: null,
        heroPanelCount: null,
        heroClearBtn: null,
        shelfDetailModal: null,
        shelfCardLocation: null,
        shelfCardMOList: null,
        shelfCardAddBtn: null
    },

    heroPanelCards: [],
    selectedCardIds: [],
    currentShelfCode: null,
    OVERDUE_THRESHOLD_HOURS: 72,

    /**
     * Initialize UI elements
     */
    init() {
        this.elements.searchMOInput = document.getElementById('searchMOInput');
        this.elements.searchMOBtn = document.getElementById('searchMOBtn');
        this.elements.addMOInput = document.getElementById('addMOInput');
        this.elements.addMOBtn = document.getElementById('addMOBtn');
        this.elements.heroPanelBody = document.getElementById('hero-panel-body');
        this.elements.heroPanelEmpty = document.getElementById('hero-panel-empty');
        this.elements.heroPanelCards = document.getElementById('hero-panel-cards');
        this.elements.heroPanelFooter = document.getElementById('hero-panel-footer');
        this.elements.heroPanelCount = document.getElementById('hero-panel-count');
        this.elements.heroClearBtn = document.getElementById('hero-clear-btn');
        this.elements.shelfDetailModal = document.getElementById('shelf-detail-modal');
        this.elements.shelfCardLocation = document.getElementById('shelf-card-location');
        this.elements.shelfCardMOList = document.getElementById('shelf-card-mo-list');
        this.elements.shelfCardAddBtn = document.getElementById('shelf-card-add-btn');

        this.setupEventListeners();
        this.setupWarehouseEventDelegation();
        this.setupHeroPanelScrollListener();
        this.setupExcelImport();
        this.setupShelfCardModalListeners();
        this.render();
        this.selectProduct('MO');
        const checkBtn = document.getElementById('mo-status-check-btn');
        const checkInput = document.getElementById('mo-status-input');
        if (checkBtn && checkInput) {
            checkBtn.addEventListener('click', () => this.handleCheckMoStatus());
            checkInput.addEventListener('keypress', (e) => {
                if (e.key === 'Enter') this.handleCheckMoStatus();
            });
        }
        document.getElementById('mo-status-close-btn')?.addEventListener('click', () => this.closeMoStatus());
        document.addEventListener('keydown', event => {
            if (event.key === 'Escape' && !document.getElementById('mo-status-results')?.classList.contains('hidden')) {
                this.closeMoStatus();
            }
        });
    },

    /**
     * Setup event listeners
     */
    setupEventListeners() {
        // Search functionality
        if (this.elements.searchMOBtn) {
            this.elements.searchMOBtn.addEventListener('click', () => this.handleSearch());
        }

        if (this.elements.searchMOInput) {
            this.elements.searchMOInput.addEventListener('keypress', (e) => {
                if (e.key === 'Enter') {
                    this.handleSearch();
                    e.preventDefault();
                }
            });
        }

        // Add MO functionality
        if (this.elements.addMOBtn) {
            this.elements.addMOBtn.addEventListener('click', () => this.handleAddMO());
        }

        if (this.elements.addMOInput) {
            this.elements.addMOInput.addEventListener('keypress', (e) => {
                if (e.key === 'Enter') {
                    e.preventDefault();
                    this.handleAddMO();
                }
            });
        }
    },

    setupWarehouseEventDelegation() {
        const grid = document.getElementById('storage-warehouse-grid');
        if (!grid || grid.dataset.delegatedClick === '1') return;
        grid.dataset.delegatedClick = '1';
        grid.addEventListener('click', event => {
            const slot = event.target.closest('.warehouse-slot[data-shelf]');
            if (slot && grid.contains(slot)) this.handleStorageSlotClick(slot.dataset.shelf);
        });
    },

    /**
     * Setup shelf card modal listeners
     */
    setupShelfCardModalListeners() {
        if (!this.elements.shelfDetailModal) return;

        // Close modal when clicking overlay (not the card)
        this.elements.shelfDetailModal.addEventListener('click', (e) => {
            if (e.target === this.elements.shelfDetailModal) {
                this.closeShelfDetailCard();
            }
        });

        // Close on Escape key
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape' && !this.elements.shelfDetailModal.classList.contains('hidden')) {
                this.closeShelfDetailCard();
            }
        });

        // Event delegation cho nút xóa MO (tránh lỗi nhúng chuỗi vào onclick)
        if (this.elements.shelfCardMOList) {
            this.elements.shelfCardMOList.addEventListener('click', (e) => {
                const btn = e.target.closest('.shelf-card-remove-mo');
                if (!btn) return;
                e.preventDefault();
                e.stopPropagation();
                const cardId = btn.getAttribute('data-card-id');
                const moNumber = btn.getAttribute('data-mo-number');
                const entryId = btn.getAttribute('data-entry-id');
                if (cardId && moNumber && entryId) {
                    this.removeMOFromShelfCard(cardId, moNumber, entryId);
                } else {
                    console.error('Thiếu data-card-id hoặc data-mo-number trên nút xóa', btn);
                    BarcodeScanner.showScanFeedback('Lỗi: không đọc được thông tin MO', 'error');
                }
            });
        }
    },

    /**
     * Setup scroll listener for hero panel
     */
    setupHeroPanelScrollListener() {
        const wrapper = document.getElementById('hero-panel-body-wrapper');
        if (!wrapper) return;
        
        wrapper.addEventListener('scroll', () => {
            this.updateHeroPanelScrollState();
        });
        
        window.addEventListener('resize', () => {
            this.updateHeroPanelScrollState();
        });
    },

    /**
     * Update hero panel scroll state
     */
    updateHeroPanelScrollState() {
        const wrapper = document.getElementById('hero-panel-body-wrapper');
        if (!wrapper) return;
        
        const isScrollable = wrapper.scrollHeight > wrapper.clientHeight;
        const isScrolledToBottom = wrapper.scrollHeight - wrapper.scrollTop <= wrapper.clientHeight + 10;
        
        if (isScrollable && !isScrolledToBottom) {
            wrapper.classList.add('has-scroll');
        } else {
            wrapper.classList.remove('has-scroll');
        }
        
        if (isScrolledToBottom) {
            wrapper.classList.add('scrolled-to-bottom');
        } else {
            wrapper.classList.remove('scrolled-to-bottom');
        }
    },

    /**
     * NEW: Toggle card selection
     */
    toggleCardSelection(cardId) {
        const index = this.selectedCardIds.indexOf(cardId);
        
        if (index === -1) {
            // Add to selection
            this.selectedCardIds.push(cardId);
        } else {
            // Remove from selection
            this.selectedCardIds.splice(index, 1);
        }
        
        // Update UI
        this.updateSelectionUI();
        
        console.log(`Selected cards: ${this.selectedCardIds.length}`, this.selectedCardIds);
    },

    /**
     * Update selection UI - dim unselected cards
     */
    updateSelectionUI() {
        const heroPanelBody = document.getElementById('hero-panel-body');
        const allCards = document.querySelectorAll('.hero-search-card-multi');
        
        // Add/remove 'has-selection' class to enable dimming
        if (this.selectedCardIds.length > 0) {
            heroPanelBody?.classList.add('hero-panel-has-selection');
        } else {
            heroPanelBody?.classList.remove('hero-panel-has-selection');
        }
        
        // Update card selected state
        allCards.forEach(cardEl => {
            const cardId = cardEl.dataset.cardId;
            cardEl.setAttribute('aria-pressed', String(this.selectedCardIds.includes(cardId)));
            if (this.selectedCardIds.includes(cardId)) {
                cardEl.classList.add('selected');
            } else {
                cardEl.classList.remove('selected');
            }
        });
        
        // Update count badge
        this.updateHeroPanelCount();
    },

    /**
     * NEW: Select all cards
     */
    selectAllCards() {
        this.selectedCardIds = this.heroPanelCards.map(card => card.id);
        this.updateSelectionUI();
    },

    /**
     * NEW: Deselect all cards
     */
    deselectAllCards() {
        this.selectedCardIds = [];
        this.updateSelectionUI();
    },


    /**
     * Setup Excel import functionality
     */
    setupExcelImport() {
        const dropZone = document.getElementById('excel-drop-zone');
        const fileInput = document.getElementById('excel-file-input');
        
        if (!dropZone || !fileInput) {
            console.warn('Excel import elements not found');
            return;
        }
        
        // Click to upload
        dropZone.addEventListener('click', () => fileInput.click());
        
        // File selected
        fileInput.addEventListener('change', (e) => {
            const file = e.target.files[0];
            if (file) this.handleExcelUpload(file);
        });
        
        // Drag & drop
        dropZone.addEventListener('dragover', (e) => {
            e.preventDefault();
            dropZone.style.borderColor = 'var(--accent)';
            dropZone.style.background = 'rgba(124,156,90,0.05)';
        });
        
        dropZone.addEventListener('dragleave', () => {
            dropZone.style.borderColor = 'var(--olive-300)';
            dropZone.style.background = 'transparent';
        });
        
        dropZone.addEventListener('drop', (e) => {
            e.preventDefault();
            dropZone.style.borderColor = 'var(--olive-300)';
            dropZone.style.background = 'transparent';
            
            const file = e.dataTransfer.files[0];
            if (file && (file.name.endsWith('.xlsx') || file.name.endsWith('.xls'))) {
                this.handleExcelUpload(file);
            } else {
                BarcodeScanner.showScanFeedback('Please upload Excel file (.xlsx or .xls)', 'error');
            }
        });
    },

    /**
     * Handle Excel file upload
     */
    async handleExcelUpload(file) {
        const fileNameEl = document.getElementById('import-file-name');
        
        if (!fileNameEl) {
            console.error('import-file-name element not found');
            return;
        }
        
        fileNameEl.textContent = 'Parsing...';
        
        try {
            if (typeof ExcelParser === 'undefined') {
                throw new Error('ExcelParser module not loaded');
            }
            
            const schedule = await ExcelParser.parseFile(file);
            
            fileNameEl.textContent = file.name;
            
            const summaryEl = document.getElementById('import-summary');
            const summaryText = document.getElementById('import-summary-text');
            
            if (summaryEl && summaryText) {
                summaryText.textContent = `Loaded ${schedule.moList.length} MOs${schedule.date ? ' for ' + schedule.date : ''}`;
                summaryEl.classList.remove('hidden');
            }
            
            this.updateMODashboard();
            
            BarcodeScanner.playSuccess();
            BarcodeScanner.showScanFeedback(`✓ Imported ${schedule.moList.length} MOs`, 'success');
            
            console.log('Excel import successful:', schedule);
            
        } catch (error) {
            console.error('Excel parse error:', error);
            fileNameEl.textContent = 'Click to upload';
            BarcodeScanner.showScanFeedback('Failed to parse Excel: ' + error.message, 'error');
        }
    },

    /**
     * Update navbar delivery schedule
     */
    updateMODashboard() {
        if (typeof ExcelParser === 'undefined') {
            console.warn('ExcelParser not available');
            return;
        }
        
        const analysis = ExcelParser.analyzeSchedule();
        if (!analysis) {
            console.warn('No analysis data available');
            return;
        }
        
        this.updateNavbarSchedule(analysis);
        console.log('Navbar schedule updated:', analysis);
    },

    /**
     * Update navbar delivery schedule dropdown
     */
    updateNavbarSchedule(analysis) {
        if (!analysis) return;

        const indicator = document.getElementById('delivery-schedule-indicator');
        if (indicator) {
            indicator.classList.remove('hidden');
        }

        const urgentBadge = document.getElementById('urgent-badge');
        const upcomingBadge = document.getElementById('upcoming-badge');

        const urgentCount = (analysis.overdue?.length || 0) + (analysis.urgent?.length || 0);
        if (urgentCount > 0 && urgentBadge) {
            urgentBadge.classList.remove('hidden');
            urgentBadge.textContent = urgentCount;
        } else if (urgentBadge) {
            urgentBadge.classList.add('hidden');
        }

        if (analysis.upcoming.length > 0 && upcomingBadge) {
            upcomingBadge.classList.remove('hidden');
            upcomingBadge.textContent = analysis.upcoming.length;
        } else if (upcomingBadge) {
            upcomingBadge.classList.add('hidden');
        }

        const inWIPCount = document.getElementById('dropdown-in-wip-count');
        const missingCount = document.getElementById('dropdown-missing-count');

        if (inWIPCount) inWIPCount.textContent = analysis.inWIP.length;
        if (missingCount) missingCount.textContent = analysis.missing.length;

        const overdueSection = document.createElement('div');
        overdueSection.id = 'dropdown-overdue-section';
        
        if (analysis.overdue && analysis.overdue.length > 0) {
            overdueSection.innerHTML = `
                <div class="px-4 py-2 text-xs font-semibold flex items-center gap-2" 
                    style="background: rgba(127, 29, 29, 0.15); color: #7f1d1d">
                    <iconify-icon icon="solar:danger-bold" width="14"></iconify-icon>
                    🚨 OVERDUE - Delivery date passed!
                </div>
                <div class="px-3 py-2">
                    ${analysis.overdue.map(delivery => `
                        <div class="flex items-center justify-between py-2 border-b" style="border-color: rgba(12,12,9,0.06)">
                            <div class="flex flex-col">
                                <span class="text-sm font-medium" style="color: var(--olive-950)">${delivery.moNumber}</span>
                                <span class="text-[10px]" style="color: #7f1d1d">
                                    ${delivery.deliveryDate} - ${delivery.time}
                                    ${delivery.daysDiff !== null ? ` (${Math.abs(delivery.daysDiff)} day${Math.abs(delivery.daysDiff) !== 1 ? 's' : ''} ago)` : ''}
                                </span>
                            </div>
                            <span class="text-xs font-bold px-2 py-0.5 rounded" style="background: rgba(127,29,29,0.1); color: #7f1d1d">LATE</span>
                        </div>
                    `).join('')}
                </div>
            `;
        }

        const urgentSection = document.getElementById('dropdown-urgent-section');
        const urgentList = document.getElementById('dropdown-urgent-list');
        
        const urgentNonOverdue = analysis.urgent.filter(d => !d.isOverdue);

        if (urgentNonOverdue.length > 0 && urgentSection && urgentList) {
            urgentSection.classList.remove('hidden');
            urgentList.innerHTML = urgentNonOverdue.map(delivery => `
                <div class="flex items-center justify-between py-2 border-b" style="border-color: rgba(12,12,9,0.06)">
                    <div class="flex flex-col">
                        <span class="text-sm font-medium" style="color: var(--olive-950)">${delivery.moNumber}</span>
                        <span class="text-[10px]" style="color: #dc2626">${delivery.deliveryDate || ''} ${delivery.time}</span>
                    </div>
                    <span class="text-xs font-semibold" style="color: #dc2626">NOW</span>
                </div>
            `).join('');
        } else if (urgentSection) {
            urgentSection.classList.add('hidden');
        }

        const upcomingSection = document.getElementById('dropdown-upcoming-section');
        const upcomingList = document.getElementById('dropdown-upcoming-list');
        const upcomingTitle = document.getElementById('dropdown-upcoming-title');

        if (analysis.upcoming.length > 0 && upcomingSection && upcomingList) {
            upcomingSection.classList.remove('hidden');
            
            const now = new Date();
            const currentHour = now.getHours();
            if (upcomingTitle) {
                upcomingTitle.textContent = `⏰ Next Hour (${currentHour}:00 - ${currentHour + 1}:00)`;
            }

            upcomingList.innerHTML = analysis.upcoming.map(delivery => `
                <div class="flex items-center justify-between py-2 border-b" style="border-color: rgba(12,12,9,0.06)">
                    <div class="flex flex-col">
                        <span class="text-sm font-medium" style="color: var(--olive-950)">${delivery.moNumber}</span>
                        <span class="text-[10px]" style="color: #f59e0b">${delivery.deliveryDate || ''} ${delivery.time}</span>
                    </div>
                    <span class="text-xs font-semibold" style="color: #f59e0b">SOON</span>
                </div>
            `).join('');
        } else if (upcomingSection) {
            upcomingSection.classList.add('hidden');
        }

        const missingSection = document.getElementById('dropdown-missing-section');
        const missingList = document.getElementById('dropdown-missing-list');

        if (analysis.missing.length > 0 && missingSection && missingList) {
            missingSection.classList.remove('hidden');
            missingList.innerHTML = analysis.missing.slice(0, 30).map(delivery => `
                <div class="flex items-center justify-between py-1.5 text-xs">
                    <span style="color: var(--olive-700)">${delivery.moNumber}</span>
                    <span style="color: var(--olive-400)">${delivery.time || 'N/A'}</span>
                </div>
            `).join('');

            if (analysis.missing.length > 30) {
                missingList.innerHTML += `<div class="text-xs text-center py-2" style="color: var(--olive-400)">+ ${analysis.missing.length - 30} more...</div>`;
            }
        } else if (missingSection) {
            missingSection.classList.add('hidden');
        }

        const scrollContainer = document.querySelector('#schedule-dropdown .overflow-y-auto');
        if (scrollContainer && analysis.overdue && analysis.overdue.length > 0) {
            const existingOverdue = document.getElementById('dropdown-overdue-section');
            if (existingOverdue) existingOverdue.remove();
            
            scrollContainer.insertBefore(overdueSection, scrollContainer.firstChild);
        }

        const emptyState = document.getElementById('dropdown-empty');
        if (emptyState) {
            emptyState.classList.add('hidden');
        }
    },

    /**
     * UPDATED: Handle Add MO - now shows vehicle count dialog
     */
    selectedProductType: 'MO',
    selectProduct(type) {
        this.selectedProductType = type;
        this.setMode('add');
        const zones = { MO: 'C–G', Cushion: 'I / K', Fiber: 'A / B', Decking: 'M / N' };
        this.elements.addMOInput.placeholder = `Nhập hoặc quét mã ${type} · Dãy ${zones[type]}`;
        document.querySelectorAll('[data-product-type]').forEach(button => {
            button.classList.toggle('active', button.dataset.productType === type);
            button.setAttribute('aria-pressed', String(button.dataset.productType === type));
        });
    },
    handleAddMO() {
        const moNumber = this.elements.addMOInput.value.trim();
        
        if (!moNumber) {
            BarcodeScanner.showScanFeedback('❌ Vui lòng nhập mã MO!', 'warning');
            this.elements.addMOInput.focus();
            return;
        }
        
        if (moNumber.length < 2) {
            BarcodeScanner.showScanFeedback('❌ Mã MO quá ngắn!', 'warning');
            this.elements.addMOInput.focus();
            return;
        }

        // Show vehicle count dialog
        this.promptVehicleCount(moNumber);
    },

    /**
     * NEW: Show dialog to ask for vehicle count
     */
    promptVehicleCount(moNumber) {
        const productType = this.selectedProductType;
        // Create modal overlay
        const overlay = document.createElement('div');
        overlay.id = 'vehicle-count-modal';
        overlay.style.cssText = `
            position: fixed;
            top: 0;
            left: 0;
            width: 100%;
            height: 100%;
            background: rgba(0, 0, 0, 0.5);
            backdrop-filter: blur(4px);
            z-index: 9999;
            display: flex;
            align-items: center;
            justify-content: center;
            animation: fadeIn 0.2s ease;
        `;

        // Create modal
        const modal = document.createElement('div');
        modal.style.cssText = `
            background: white;
            padding: 2rem;
            border-radius: 1rem;
            box-shadow: 0 20px 60px rgba(0, 0, 0, 0.3);
            max-width: 400px;
            width: 90%;
            animation: slideUp 0.3s ease;
        `;

        modal.innerHTML = `
            <div style="text-align: center;">
                <div style="width: 60px; height: 60px; background: linear-gradient(135deg, #7c9c5a 0%, #5b7a3d 100%); border-radius: 50%; display: flex; align-items: center; justify-content: center; margin: 0 auto 1.5rem;">
                    <svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="white" stroke-width="2">
                        <rect x="1" y="3" width="15" height="13"></rect>
                        <rect x="16" y="8" width="7" height="13"></rect>
                        <line x1="1" y1="16" x2="16" y2="16"></line>
                    </svg>
                </div>
                
                <h2 style="color: var(--olive-950); font-size: 1.25rem; font-weight: 600; margin-bottom: 0.5rem;">
                    ${productType}: ${this.escapeHtml(moNumber)}
                </h2>
                
                <p style="color: var(--olive-500); font-size: 0.875rem; margin-bottom: 1.5rem;">
                    Lần này thêm bao nhiêu xe vào kho?
                </p>
                
                <!-- Quick buttons -->
                <div style="display: grid; grid-template-columns: repeat(3, 1fr); gap: 0.5rem; margin-bottom: 1rem;">
                    <button class="vehicle-quick-btn" data-count="1" style="padding: 0.75rem; border: 2px solid var(--olive-300); background: white; color: var(--olive-700); border-radius: 0.5rem; font-weight: 600; cursor: pointer; transition: all 0.2s;">
                        1 xe
                    </button>
                    <button class="vehicle-quick-btn" data-count="2" style="padding: 0.75rem; border: 2px solid var(--olive-300); background: white; color: var(--olive-700); border-radius: 0.5rem; font-weight: 600; cursor: pointer; transition: all 0.2s;">
                        2 xe
                    </button>
                    <button class="vehicle-quick-btn" data-count="3" style="padding: 0.75rem; border: 2px solid var(--olive-300); background: white; color: var(--olive-700); border-radius: 0.5rem; font-weight: 600; cursor: pointer; transition: all 0.2s;">
                        3 xe
                    </button>
                </div>
                
                <!-- Custom input -->
                <div style="display: flex; gap: 0.5rem; margin-bottom: 1rem;">
                    <input type="number" id="vehicle-count-input" min="1" max="10" value="1" placeholder="Số khác..."
                        style="flex: 1; padding: 0.75rem; border: 2px solid var(--olive-300); border-radius: 0.5rem; font-size: 0.875rem; outline: none;"
                        onfocus="this.style.borderColor='var(--olive-500)'"
                        onblur="this.style.borderColor='var(--olive-300)'">
                    <button id="confirm-custom-btn" style="padding: 0.75rem 1.5rem; border: none; background: linear-gradient(135deg, var(--accent) 0%, #5b7a3d 100%); color: white; border-radius: 0.5rem; font-weight: 600; cursor: pointer; white-space: nowrap;">
                        OK
                    </button>
                </div>
                
                <button id="cancel-vehicle-btn" style="width: 100%; padding: 0.75rem; border: 2px solid var(--olive-300); background: white; color: var(--olive-700); border-radius: 0.5rem; font-weight: 500; cursor: pointer;">
                    Hủy
                </button>
            </div>
        `;

        overlay.appendChild(modal);
        document.body.appendChild(overlay);

        // Add hover effects
        const style = document.createElement('style');
        style.textContent = `
            @keyframes fadeIn {
                from { opacity: 0; }
                to { opacity: 1; }
            }
            @keyframes slideUp {
                from { transform: translateY(20px); opacity: 0; }
                to { transform: translateY(0); opacity: 1; }
            }
            .vehicle-quick-btn:hover {
                background: var(--olive-50) !important;
                border-color: var(--accent) !important;
                transform: translateY(-2px);
            }
            .vehicle-quick-btn:active {
                transform: translateY(0);
            }
            #confirm-custom-btn:hover {
                transform: translateY(-1px);
                box-shadow: 0 4px 12px rgba(124, 156, 90, 0.3);
            }
        `;
        document.head.appendChild(style);

        // Event handlers
        const quickButtons = modal.querySelectorAll('.vehicle-quick-btn');
        quickButtons.forEach(btn => {
            btn.onclick = async () => {
                const count = parseInt(btn.dataset.count);
                if (await this.createMultiVehicleMO(moNumber, count, productType)) overlay.remove();
            };
        });

        const customInput = modal.querySelector('#vehicle-count-input');
        const confirmBtn = modal.querySelector('#confirm-custom-btn');
        confirmBtn.onclick = async () => {
            const count = parseInt(customInput.value);
            if (count > 0 && count <= 10) {
                if (await this.createMultiVehicleMO(moNumber, count, productType)) overlay.remove();
            } else {
                customInput.style.borderColor = '#DC2626';
                BarcodeScanner.showScanFeedback('Số lượng phải từ 1-10', 'error');
            }
        };

        const cancelBtn = modal.querySelector('#cancel-vehicle-btn');
        cancelBtn.onclick = () => {
            overlay.remove();
            this.elements.addMOInput.focus();
        };

        // Enter to confirm
        customInput.onkeypress = (e) => {
            if (e.key === 'Enter') {
                confirmBtn.click();
            }
        };

        // Escape to cancel
        overlay.onkeydown = (e) => {
            if (e.key === 'Escape') {
                overlay.remove();
                this.elements.addMOInput.focus();
            }
        };

        // Focus input
        setTimeout(() => customInput.focus(), 100);
    },

    /**
     * NEW: Create multiple cards for multi-vehicle MO
     */
    async createMultiVehicleMO(moNumber, vehicleCount, productType = this.selectedProductType) {
        let createdCards;
        try {
            createdCards = await WIPManager.createMultiVehicleCards(moNumber, vehicleCount, productType);
        } catch (error) {
            console.error('Không thể tạo MO nhiều xe:', error);
            BarcodeScanner.playError();
            BarcodeScanner.showScanFeedback(error.message || 'Không thể tạo MO nhiều xe. Vui lòng thử lại.', 'error');
            return false;
        }
        if (!Array.isArray(createdCards) || createdCards.length === 0) {
            console.error('Dữ liệu trả về khi tạo nhiều xe không hợp lệ:', createdCards);
            BarcodeScanner.playError();
            BarcodeScanner.showScanFeedback('Không thể tạo MO nhiều xe. Vui lòng tải lại trang.', 'error');
            return false;
        }
        BarcodeScanner.playSuccess();
        const locations = createdCards.map(c => c.shelfCode).join(', ');
        const message = vehicleCount > 1
            ? `<span class="notification-card-message-strong">${vehicleCount} xe đã được thêm</span>`
            : `<span class="notification-card-message-strong">Vị trí: ${createdCards[0].shelfCode}</span>`;
        BarcodeScanner.showCardNotification('success', `Thêm ${productType} thành công`, message,
            vehicleCount > 1 ? `Vị trí: ${locations}` : `MO: "${moNumber}"`);
        this.elements.addMOInput.value = '';
        this.elements.addMOInput.focus();
        return true;
    },
    /**
     * Main render function
     */
    render() {
        this.renderStorageLayout();
        this.updateOverdueTicker();
    },

    /**
     * Render Storage Layout Visualization
     */
    renderStorageLayout() {
        const container = document.getElementById('storage-warehouse-grid');
        if (!container) return;

        const allCards = WIPManager.getAll();
        
        const OVERDUE_THRESHOLD = this.OVERDUE_THRESHOLD_HOURS || 72;
        const now = new Date();
        
        const occupancyMap = {};
        allCards.forEach(card => {
            occupancyMap[card.shelfCode] = {
                moNumbers: card.moNumbers || [card.moNumber],
                createdAt: card.createdAt,
                isOverdue: card.createdAt 
                    ? ((now - new Date(card.createdAt)) / (1000 * 60 * 60)) >= OVERDUE_THRESHOLD
                    : false
            };
        });

        const leftColumn = [
            { type: 'normal', dataLine: 'K', displayLine: 'K' },
            { type: 'normal', dataLine: 'P', displayLine: 'P' },
            { type: 'normal', dataLine: 'O', displayLine: 'O' },
            { type: 'normal', dataLine: 'N', displayLine: 'N' },
            { type: 'normal', dataLine: 'M', displayLine: 'M' },
            { type: 'normal', dataLine: 'L', displayLine: 'L' },
            { type: 'normal', dataLine: 'J', displayLine: 'J' }
        ];

        const rightColumn = [
            { type: 'normal', dataLine: 'I', displayLine: 'I' },
            { type: 'normal', dataLine: 'H', displayLine: 'H' },
            { type: 'normal', dataLine: 'G', displayLine: 'G' },
            { type: 'normal', dataLine: 'F', displayLine: 'F' },
            { type: 'normal', dataLine: 'E', displayLine: 'E' },
            { type: 'normal', dataLine: 'D', displayLine: 'D' },
            { type: 'normal', dataLine: 'C', displayLine: 'C' },
            { type: 'normal', dataLine: 'B', displayLine: 'B' },
            { type: 'normal', dataLine: 'A', displayLine: 'A' }
        ];

        let leftHTML = '';
        let rightHTML = '';

        leftColumn.forEach(item => {
            leftHTML += this.renderLineByType(item, occupancyMap, true);
        });

        rightColumn.forEach(item => {
            rightHTML += this.renderLineByType(item, occupancyMap, false);
        });

        const template = document.createElement('template');
        template.innerHTML = `
            <div class="warehouse-column-left">
                ${leftHTML}
            </div>
            <div class="warehouse-column-right">
                ${rightHTML}
            </div>
        `;
        container.replaceChildren(template.content.cloneNode(true));

        const totalOccupied = allCards.length;
        const totalSlots = Object.keys(ShelfLocations.locations).length;
        const occupancyRate = Math.round((totalOccupied / totalSlots) * 100);
        
        const occupancyEl = document.getElementById('storage-occupancy-rate');
        if (occupancyEl) {
            occupancyEl.textContent = `${occupancyRate}%`;
        }
    },

    /**
     * NEW: Update visual for specific shelf locations only
     * @param {Array} shelfCodes - Array of shelf codes to update (e.g., ["A-05", "A-06"])
     */
    updateSpecificSlots(shelfCodes) {
        const uniqueShelves = [...new Set(shelfCodes || [])];
        if (!uniqueShelves.length) return;

        const now = Date.now();
        const thresholdMs = (this.OVERDUE_THRESHOLD_HOURS || 72) * 60 * 60 * 1000;
        const affectedLines = new Set();

        uniqueShelves.forEach(shelfCode => {
            const cards = WIPManager.getByShelf(shelfCode);
            const moNumbers = cards.flatMap(card => card.moNumbers || []);
            const oldestCreatedAt = cards.reduce((oldest, card) => {
                const value = card.createdAt ? new Date(card.createdAt).getTime() : now;
                return Math.min(oldest, value);
            }, now);
            const stateClass = cards.length === 0 ? 'empty' : (now - oldestCreatedAt >= thresholdMs ? 'overdue' : 'occupied');
            const stateLabel = stateClass === 'empty' ? 'Trống' : stateClass === 'overdue' ? 'QUÁ HẠN' : 'Đã dùng';
            const tooltip = `${shelfCode} - ${stateLabel}${moNumbers.length ? `\nMO: ${moNumbers.join(', ')}` : ''}`;

            document.querySelectorAll(`.warehouse-slot[data-shelf="${CSS.escape(shelfCode)}"]`).forEach(slot => {
                slot.className = `warehouse-slot ${stateClass}`;
                slot.style.cssText = '';
                slot.dataset.tooltip = tooltip;
            });
            affectedLines.add(shelfCode.split('-')[0]);
        });

        affectedLines.forEach(line => {
            const row = document.querySelector(`.warehouse-line-row[data-line="${CSS.escape(line)}"]`);
            const badge = row?.querySelector('.warehouse-stats-badge');
            if (badge) {
                const capacity = ShelfLocations.getLineLocations(line).length;
                const occupiedShelves = [...WIPManager.cardsByShelf.keys()].filter(shelf => shelf.startsWith(`${line}-`)).length;
                badge.textContent = `${occupiedShelves}/${capacity}`;
            }
        });

        const occupancyEl = document.getElementById('storage-occupancy-rate');
        if (occupancyEl) occupancyEl.textContent = `${Math.round((WIPManager.getCount() / Object.keys(ShelfLocations.locations).length) * 100)}%`;
        this.updateOverdueTicker();
    },
    /**
     * Render a line based on its type
     */
    renderLineByType(item, occupancyMap, isLeftColumn = false) {
        const { type, dataLine, displayLine } = item;
        
        switch (type) {
            case 'cushion':
                return this.renderCushionRack(dataLine);
            
            case 'arrangement':
                return this.renderArrangementArea(dataLine);
            
            case 'ready':
                return this.renderReadyToGo(dataLine);
            
            case 'other':
                return this.renderOtherArea(dataLine);
                        
            case 'Fiber':
                return this.renderFiberArea(dataLine);
            
            case 'normal':
                return this.renderNormalLine(dataLine, occupancyMap, displayLine, isLeftColumn);

            default:
                return '';
        }
    },

    /**
     * Render Cushion Rack
     */
    renderCushionRack(dataLine) {
        return `
            <div class="warehouse-line-row warehouse-line-cushion warehouse-line-no-label" data-line="${dataLine}">
                <div class="warehouse-cushion-box">
                    <iconify-icon icon="solar:sofa-bold" width="20" style="color: rgba(59, 130, 246, 0.8)"></iconify-icon>
                    <span class="cushion-text">Cushion rack</span>
                </div>
            </div>
        `;
    },

    /**
     * Render Arrangement Area
     */
    renderArrangementArea(dataLine) {
        return `
            <div class="warehouse-line-row warehouse-line-arrangement warehouse-line-no-label" data-line="${dataLine}">
                <div class="warehouse-arrangement-box">
                    <iconify-icon icon="solar:layers-minimalistic-bold" width="20" style="color: rgba(251, 191, 36, 0.8)"></iconify-icon>
                    <span class="arrangement-text">Arrangement Area</span>
                </div>
            </div>
        `;
    },

    /**
     * Render Ready to go
     */
    renderReadyToGo(dataLine) {
        return `
            <div class="warehouse-line-row warehouse-line-ready warehouse-line-no-label" data-line="${dataLine}">
                <div class="warehouse-ready-box">
                    <iconify-icon icon="solar:check-circle-bold" width="20" style="color: rgba(124, 156, 90, 0.8)"></iconify-icon>
                    <span class="ready-text">Ready to go</span>
                </div>
            </div>
        `;
    },

    /**
     * Render Other area
     */
    renderOtherArea(dataLine) {
        return `
            <div class="warehouse-line-row warehouse-line-other warehouse-line-no-label" data-line="${dataLine}">
                <div class="warehouse-other-box">
                    <iconify-icon icon="solar:widget-5-bold" width="20" style="color: rgba(124, 156, 90, 0.8)"></iconify-icon>
                    <span class="other-text">Other area</span>
                </div>
            </div>
        `;
    },

    /**
    * Render Fiber Area (giống Arrangement Area nhưng text "Fiber")
    */
    renderFiberArea(dataLine) {
        return `
            <div class="warehouse-line-row warehouse-line-arrangement warehouse-line-no-label" data-line="${dataLine}">
                <div class="warehouse-arrangement-box">
                    <iconify-icon icon="solar:layers-minimalistic-bold" width="20"
                                style="color: rgba(251, 191, 36, 0.8)"></iconify-icon>
                    <span class="arrangement-text">Fiber</span>
                </div>
            </div>
        `;
    },

    /**
    * Render normal storage line with boxes
    */
    renderNormalLine(dataLine, occupancyMap, displayLine, isLeftColumn = false) {
        if (dataLine === 'I' || dataLine === 'K') return this.renderTieredRack(dataLine, occupancyMap, isLeftColumn);
        const lineColor = ShelfLocations.getAreaColor(`${dataLine}-01`);
        
        // Capacity cho từng line (khớp với shelfLocations.js)
        const lineCapacities = ShelfLocations.lineCapacities;
        
        const maxPosition = lineCapacities[dataLine] || 70;
        
        // Expanded lines use two rows of 38 positions; E retains its existing layout.
        const isSingleRow = maxPosition <= 38;

        let occupiedCount = 0;
        for (let pos = 1; pos <= maxPosition; pos++) {
            const code = `${dataLine}-${pos.toString().padStart(2, '0')}`;
            if (occupancyMap[code]) occupiedCount++;
        }
        
        let slotsHTML = '';

        if (isSingleRow) {
            // ===== SINGLE ROW =====
            if (dataLine === 'O') {
                // O: Ngược 38→1
                for (let pos = maxPosition; pos >= 1; pos--) {
                    const code = `${dataLine}-${pos.toString().padStart(2, '0')}`;
                    const occupied = occupancyMap[code];
                    
                    let slotClass = 'warehouse-slot empty';
                    let tooltip = `${code} - Trống`;
                    
                    if (occupied) {
                        if (occupied.isOverdue) {
                            slotClass = 'warehouse-slot overdue';
                            tooltip = `${code} - QUÁ HẠN\nMO: ${occupied.moNumbers.join(', ')}`;
                        } else {
                            slotClass = 'warehouse-slot occupied';
                            tooltip = `${code} - Đã dùng\nMO: ${occupied.moNumbers.join(', ')}`;
                        }
                    }
                    
                    slotsHTML += `
                        <div class="${slotClass}" 
                            data-shelf="${code}" 
                            data-tooltip="${tooltip}">
                        </div>
                    `;
                }
            } else {
                // G: Thuận 1→38
                for (let pos = 1; pos <= maxPosition; pos++) {
                    const code = `${dataLine}-${pos.toString().padStart(2, '0')}`;
                    const occupied = occupancyMap[code];
                    
                    let slotClass = 'warehouse-slot empty';
                    let tooltip = `${code} - Trống`;
                    
                    if (occupied) {
                        if (occupied.isOverdue) {
                            slotClass = 'warehouse-slot overdue';
                            tooltip = `${code} - QUÁ HẠN\nMO: ${occupied.moNumbers.join(', ')}`;
                        } else {
                            slotClass = 'warehouse-slot occupied';
                            tooltip = `${code} - Đã dùng\nMO: ${occupied.moNumbers.join(', ')}`;
                        }
                    }
                    
                    slotsHTML += `
                        <div class="${slotClass}" 
                            data-shelf="${code}" 
                            data-tooltip="${tooltip}">
                        </div>
                    `;
                }
            }
            
        } else {
            // ===== DOUBLE ROW =====
            const slotsPerRow = Math.ceil(maxPosition / 2);
            
            if (dataLine === 'M' || dataLine === 'N') {
                // M, N: Hàng 1 thuận (39→76), Hàng 2 ngược (38→1)
                
                // HÀNG 1: Thuận (slotsPerRow + 1 → maxPosition)
                for (let pos = slotsPerRow + 1; pos <= maxPosition; pos++) {
                    const code = `${dataLine}-${pos.toString().padStart(2, '0')}`;
                    const occupied = occupancyMap[code];
                    
                    let slotClass = 'warehouse-slot empty';
                    let tooltip = `${code} - Trống`;
                    
                    if (occupied) {
                        if (occupied.isOverdue) {
                            slotClass = 'warehouse-slot overdue';
                            tooltip = `${code} - QUÁ HẠN\nMO: ${occupied.moNumbers.join(', ')}`;
                        } else {
                            slotClass = 'warehouse-slot occupied';
                            tooltip = `${code} - Đã dùng\nMO: ${occupied.moNumbers.join(', ')}`;
                        }
                    }
                    
                    slotsHTML += `
                        <div class="${slotClass}" 
                            data-shelf="${code}" 
                            data-tooltip="${tooltip}">
                        </div>
                    `;
                }
                
                // HÀNG 2: Ngược (slotsPerRow → 1)
                for (let pos = slotsPerRow; pos >= 1; pos--) {
                    const code = `${dataLine}-${pos.toString().padStart(2, '0')}`;
                    const occupied = occupancyMap[code];
                    
                    let slotClass = 'warehouse-slot empty';
                    let tooltip = `${code} - Trống`;
                    
                    if (occupied) {
                        if (occupied.isOverdue) {
                            slotClass = 'warehouse-slot overdue';
                            tooltip = `${code} - QUÁ HẠN\nMO: ${occupied.moNumbers.join(', ')}`;
                        } else {
                            slotClass = 'warehouse-slot occupied';
                            tooltip = `${code} - Đã dùng\nMO: ${occupied.moNumbers.join(', ')}`;
                        }
                    }
                    
                    slotsHTML += `
                        <div class="${slotClass}" 
                            data-shelf="${code}" 
                            data-tooltip="${tooltip}">
                        </div>
                    `;
                }
                
            } else {
                // A-F, H-L: Hàng 1 ngược (76→39), Hàng 2 thuận (1→38)
                
                // HÀNG 1: Ngược (maxPosition → slotsPerRow + 1)
                for (let pos = maxPosition; pos > slotsPerRow; pos--) {
                    const code = `${dataLine}-${pos.toString().padStart(2, '0')}`;
                    const occupied = occupancyMap[code];
                    
                    let slotClass = 'warehouse-slot empty';
                    let tooltip = `${code} - Trống`;
                    
                    if (occupied) {
                        if (occupied.isOverdue) {
                            slotClass = 'warehouse-slot overdue';
                            tooltip = `${code} - QUÁ HẠN\nMO: ${occupied.moNumbers.join(', ')}`;
                        } else {
                            slotClass = 'warehouse-slot occupied';
                            tooltip = `${code} - Đã dùng\nMO: ${occupied.moNumbers.join(', ')}`;
                        }
                    }
                    
                    slotsHTML += `
                        <div class="${slotClass}" 
                            data-shelf="${code}" 
                            data-tooltip="${tooltip}">
                        </div>
                    `;
                }
                
                // Thêm ô trống cho Line E (hàng 1)
                if (dataLine === 'E') {
                    slotsHTML += `<div class="warehouse-slot-spacer" style="visibility: hidden; pointer-events: none;"></div>`;
                }
                
                // HÀNG 2: Thuận (1 → slotsPerRow)
                for (let pos = 1; pos <= slotsPerRow; pos++) {
                    const code = `${dataLine}-${pos.toString().padStart(2, '0')}`;
                    const occupied = occupancyMap[code];
                    
                    let slotClass = 'warehouse-slot empty';
                    let tooltip = `${code} - Trống`;
                    
                    if (occupied) {
                        if (occupied.isOverdue) {
                            slotClass = 'warehouse-slot overdue';
                            tooltip = `${code} - QUÁ HẠN\nMO: ${occupied.moNumbers.join(', ')}`;
                        } else {
                            slotClass = 'warehouse-slot occupied';
                            tooltip = `${code} - Đã dùng\nMO: ${occupied.moNumbers.join(', ')}`;
                        }
                    }
                    
                    slotsHTML += `
                        <div class="${slotClass}" 
                            data-shelf="${code}" 
                            data-tooltip="${tooltip}">
                        </div>
                    `;
                }
                
                // Thêm ô trống cho Line E (hàng 2)
                if (dataLine === 'E') {
                    slotsHTML += `<div class="warehouse-slot-spacer" style="visibility: hidden; pointer-events: none;"></div>`;
                }
            }
        }

        let containerClass;
        if (isSingleRow) {
            containerClass = 'warehouse-slots-container-single';
        } else if (dataLine === 'E') {
            containerClass = 'warehouse-slots-container-e';
        } else {
            containerClass = 'warehouse-slots-container';
        }

        const rowBaseClass = isSingleRow ? 'warehouse-line-row warehouse-line-single-row'
                                        : 'warehouse-line-row';
        
        if (isLeftColumn) {
            return `
                <div class="${rowBaseClass} warehouse-line-right" data-line="${dataLine}">
                    <div class="warehouse-stats-badge">
                        ${occupiedCount}/${maxPosition}
                    </div>
                    <div class="${containerClass}">
                        ${slotsHTML}
                    </div>
                    <div class="warehouse-line-label">
                        <span>${displayLine}</span>
                        <iconify-icon icon="solar:box-minimalistic-bold" width="16" style="color: ${lineColor}"></iconify-icon>
                    </div>
                </div>
            `;
        }
        
        return `
            <div class="${rowBaseClass}" data-line="${dataLine}">
                <div class="warehouse-line-label">
                    <iconify-icon icon="solar:box-minimalistic-bold" width="16" style="color: ${lineColor}"></iconify-icon>
                    <span>${displayLine}</span>
                </div>
                <div class="${containerClass}">
                    ${slotsHTML}
                </div>
                <div class="warehouse-stats-badge">
                    ${occupiedCount}/${maxPosition}
                </div>
            </div>
        `;
    },

    renderTieredRack(line, occupancyMap, isLeftColumn) {
        const capacity = ShelfLocations.lineCapacities[line];
        const lineIcon = `<iconify-icon icon="solar:box-minimalistic-bold" width="16" style="color: ${ShelfLocations.getAreaColor(`${line}-01`)}"></iconify-icon>`;
        const slotsPerTier = line === 'I' ? 25 : 30;
        const escape = value => String(value).replace(/[&<>"']/g, ch =>
            ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch]);
        let occupiedCount = 0;
        const tiers = [3, 2, 1].map(tier => {
            const slots = Array.from({ length: slotsPerTier }, (_, index) => {
                const position = (tier - 1) * slotsPerTier + index + 1;
                const code = `${line}-${String(position).padStart(2, '0')}`;
                const occupied = occupancyMap[code];
                if (occupied) occupiedCount++;
                const status = occupied ? (occupied.isOverdue ? 'overdue' : 'occupied') : 'empty';
                const tooltip = `${code} · Tầng ${tier} · Ô ${index + 1}` +
                    (occupied ? `\nMW: ${occupied.moNumbers.join(', ')}` : ' · Trống');
                return `<div class="warehouse-slot ${status}" data-shelf="${code}" data-tooltip="${escape(tooltip)}">${position}</div>`;
            }).join('');
            return `<div class="warehouse-rack-tier" data-tier="${tier}"><div class="warehouse-tier-slots" style="--slots-per-tier: ${slotsPerTier}">${slots}</div></div>`;
        }).join('');
        const extraSlots = line === 'I' ? Array.from({ length: 16 }, (_, index) => {
            const code = `I-${76 + index}`;
            const occupied = occupancyMap[code];
            if (occupied) occupiedCount++;
            const status = occupied ? (occupied.isOverdue ? 'overdue' : 'occupied') : 'empty';
            const tooltip = `${code} · Dãy 1 tầng · Ô ${index + 1}` +
                (occupied ? `\nMW: ${occupied.moNumbers.join(', ')}` : ' · Trống');
            return `<div class="warehouse-slot ${status}" data-shelf="${code}" data-tooltip="${escape(tooltip)}">${index + 1}</div>`;
        }).join('') : '';
        const extraRow = extraSlots ? `<div class="warehouse-rack-extra" data-extra-row="I"><span>Dãy 1 tầng · Ô 1–16</span><div class="warehouse-tier-slots" style="--slots-per-tier: 16">${extraSlots}</div></div>` : '';
        return `<div class="warehouse-line-row warehouse-tiered-rack ${isLeftColumn ? 'warehouse-line-right' : ''}" data-line="${line}">
            <div class="warehouse-line-label">${isLeftColumn ? `<span>${line}</span>${lineIcon}` : `${lineIcon}<span>${line}</span>`}</div>
            <div class="warehouse-rack-tiers">${tiers}${extraRow}</div>
            <div class="warehouse-stats-badge">${occupiedCount}/${capacity}</div>
        </div>`;
    },

    /**
     * Handle storage slot click - Opens shelf detail card
     */
    handleStorageSlotClick(shelfCode) {
        this.currentShelfCode = shelfCode;
        this.openShelfDetailCard(shelfCode);
    },

    /**
     * Open shelf detail card
     */
    openShelfDetailCard(shelfCode) {
        const cards = WIPManager.getByShelf(shelfCode);
        
        // Update location header
        this.elements.shelfCardLocation.textContent = shelfCode;
        
        // Render MO list
        this.renderShelfCardMOList(cards);
        
        // Show modal
        this.elements.shelfDetailModal.classList.remove('hidden');
        
        // Disable body scroll
        document.body.style.overflow = 'hidden';
        
        console.log(`Opened shelf detail card for: ${shelfCode}`);
    },

    /**
     * Close shelf detail card
     */
    closeShelfDetailCard() {
        this.elements.shelfDetailModal.classList.add('hidden');
        this.currentShelfCode = null;
        
        // Re-enable body scroll
        document.body.style.overflow = '';
        
        console.log('Closed shelf detail card');
    },

    /**
     * Render MO list in shelf card
     */
    renderShelfCardMOList(cards) {
        if (!this.elements.shelfCardMOList) return;
        
        // Get all MOs at this shelf — dùng chung normalize với WIPManager
        const allMOs = [];
        cards.forEach(card => {
            const moNumbers = (typeof WIPManager.normalizeMONumbers === 'function')
                ? WIPManager.normalizeMONumbers(card)
                : (Array.isArray(card.moNumbers) ? card.moNumbers : (card.moNumber ? [card.moNumber] : []));

            // Sửa dữ liệu lỗi ngay trên card (moNumbers từng là string, v.v.)
            card.moNumbers = moNumbers;

            moNumbers.forEach(mo => {
                if (mo == null || String(mo).trim() === '') return;
                allMOs.push({
                    moNumber: String(mo).trim(),
                    createdAt: card.createdAt,
                    cardId: card.id,
                    entryId: card.moEntryIds?.[mo]
                });
            });
        });
        
        // Update Add button state
        const isAtMax = allMOs.length >= WIPManager.MAX_MOS_PER_CARD;
        if (this.elements.shelfCardAddBtn) {
            this.elements.shelfCardAddBtn.disabled = isAtMax;
        }
        
        if (allMOs.length === 0) {
            // Empty state
            this.elements.shelfCardMOList.innerHTML = `
                <div class="shelf-card-empty">
                    <iconify-icon icon="solar:box-linear" width="48"></iconify-icon>
                    <p>Vị trí này đang trống<br>Nhấn "Thêm MO" để thêm</p>
                </div>
            `;
            return;
        }
        
        // Render MO items — dùng data-* thay vì onclick để tránh lỗi ký tự đặc biệt
        const html = allMOs.map(mo => {
            const createdDate = new Date(mo.createdAt);
            const dateStr = createdDate.toLocaleDateString('vi-VN', {
                day: '2-digit',
                month: '2-digit',
                year: 'numeric'
            });
            const timeStr = createdDate.toLocaleTimeString('vi-VN', {
                hour: '2-digit',
                minute: '2-digit',
                hour12: false
            });
            
            // Escape thuộc tính HTML
            const safeCardId = String(mo.cardId).replace(/"/g, '&quot;');
            const safeMoNumber = String(mo.moNumber).replace(/"/g, '&quot;');
            const safeEntryId = String(mo.entryId || '').replace(/"/g, '&quot;');
            
            return `
                <div class="shelf-card-mo-item">
                    <div class="shelf-card-mo-number">
                        <iconify-icon icon="solar:box-minimalistic-bold" width="20"></iconify-icon>
                        ${this.escapeHtml(mo.moNumber)}
                    </div>
                    <div class="shelf-card-mo-time">
                        <iconify-icon icon="solar:clock-circle-linear" width="14"></iconify-icon>
                        ${dateStr} • ${timeStr}
                    </div>
                    <button type="button" class="shelf-card-remove-mo" 
                            data-card-id="${safeCardId}"
                            data-mo-number="${safeMoNumber}"
                            data-entry-id="${safeEntryId}"
                            title="Xóa MO này">
                        <iconify-icon icon="solar:trash-bin-minimalistic-bold" width="16"></iconify-icon>
                    </button>
                </div>
            `;
        }).join('');
        
        this.elements.shelfCardMOList.innerHTML = html;
    },

    /**
     * Escape HTML để tránh XSS khi hiển thị mã MO
     */
    escapeHtml(str) {
        if (str == null) return '';
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    },

    /**
     * Add MO to current shelf
     */
    async addMOToShelf() {
        if (!this.currentShelfCode) return;
        
        const moNumber = prompt('Nhập mã MO:');
        if (!moNumber || moNumber.trim() === '') return;
        
        const trimmedMO = moNumber.trim();
        
        // Create or add to card
        const card = await WIPManager.createCard(this.currentShelfCode, trimmedMO);
        
        if (card) {
            BarcodeScanner.playSuccess();
            BarcodeScanner.showScanFeedback(`✓ Đã thêm MO "${trimmedMO}" vào ${this.currentShelfCode}`, 'success');
            
            // Refresh card display
            const cards = WIPManager.getByShelf(this.currentShelfCode);
            this.renderShelfCardMOList(cards);
            

        } else {
            BarcodeScanner.playError();
            BarcodeScanner.showScanFeedback('Không thể thêm MO (đã đầy hoặc lỗi)', 'error');
        }
    },

    /**
     * Remove MO from shelf card
     * Truyền thêm currentShelfCode để tìm chắc chắn hơn
     */
    async removeMOFromShelfCard(cardId, moNumber, entryId) {
        if (!cardId || !moNumber || !entryId) {
            BarcodeScanner.showScanFeedback('Dữ liệu MO không đầy đủ. Vui lòng tải lại danh sách.', 'error');
            return;
        }
        if (!confirm(`Xác nhận xóa MO "${moNumber}" khỏi kệ?`)) return;

        const success = await WIPManager.removeMOFromCard(cardId, moNumber, entryId);
        if (!success) {
            BarcodeScanner.playError();
            BarcodeScanner.showScanFeedback(`Không xóa được MO "${moNumber}" vì dữ liệu kệ đã thay đổi.`, 'error');
            return;
        }

        BarcodeScanner.playSuccess();
        BarcodeScanner.showScanFeedback(`✓ Đã xóa MO "${moNumber}"`, 'success');
        const shelfCode = this.currentShelfCode;
        const cards = shelfCode ? WIPManager.getByShelf(shelfCode) : [];
        if (cards.length === 0) this.closeShelfDetailCard();
        else this.renderShelfCardMOList(cards);
    },
    /**
     * Handle search
     */
    handleSearch() {
        const searchTerm = this.elements.searchMOInput.value.trim().toUpperCase();
        if (!searchTerm) {
            BarcodeScanner.showScanFeedback('Vui lòng nhập mã MO hoặc vị trí!', 'warning');
            return;
        }

        const foundCards = WIPManager.search(searchTerm);

        if (foundCards.length === 0) {
            BarcodeScanner.showScanFeedback(`Không tìm thấy "${searchTerm}"`, 'error');
        } else {
            const virtualCards = [];
            const isSearchingBySpecificMO = !ShelfLocations.isValid(searchTerm);

            foundCards.forEach(card => {
                const moNumbers = card.moNumbers || (card.moNumber ? [card.moNumber] : []);

                if (isSearchingBySpecificMO) {
                    const matchingMOs = moNumbers.filter(mo => mo.toUpperCase().includes(searchTerm));
                    matchingMOs.forEach(mo => {
                        // TẠO BẢN SAO SÂU để tránh lỗi tham chiếu
                        const cleanCardCopy = JSON.parse(JSON.stringify(card));
                        
                        virtualCards.push({
                            ...cleanCardCopy,
                            id: `${card.id}-${mo}`,
                            moNumbers: [mo], // Chỉ chứa MO này
                            isVirtual: true,
                            originalCardId: card.id
                        });
                    });
                } else {
                    virtualCards.push(card);
                }
            });
            
            this.updateHeroPanel(virtualCards);
            BarcodeScanner.showScanFeedback(`✓ Tìm thấy ${virtualCards.length} kết quả`, 'success');
        }
        this.elements.searchMOInput.value = '';
    },

    /**
     * Update hero panel with search results
     */
    updateHeroPanel(newCards) {
        if (!this.elements.heroPanelCards || !this.elements.heroPanelEmpty) return;

        console.log('updateHeroPanel nhận được:', newCards);
        console.log('Danh sách thẻ HIỆN TẠI (trước khi thêm):', this.heroPanelCards.length, this.heroPanelCards.map(c => c.id));

        newCards.forEach(newCard => {
            const alreadyExists = this.heroPanelCards.some(existingCard => existingCard.id === newCard.id);
            if (!alreadyExists) {
                this.heroPanelCards.push(newCard);
                console.log(`Đã thêm thẻ mới: ${newCard.id}`);
            } else {
                console.log(`Thẻ ${newCard.id} đã tồn tại, bỏ qua.`);
            }
        });

        console.log('Danh sách thẻ MỚI (sau khi thêm):', this.heroPanelCards.length, this.heroPanelCards.map(c => c.id));

        if (this.heroPanelCards.length === 0) {
            this.clearHeroPanel();
            return;
        }

        this.elements.heroPanelEmpty.classList.add('hidden');
        this.elements.heroPanelCards.classList.remove('hidden');
        this.elements.heroPanelFooter.classList.remove('hidden');
        this.elements.heroClearBtn.classList.remove('hidden');

        this.updateHeroPanelDisplay();
    },

    /**
     * Update hero panel display
     */
    updateHeroPanelDisplay() {
        if (!this.elements.heroPanelCards) return;

        console.log(`updateHeroPanelDisplay đang vẽ lại ${this.heroPanelCards.length} thẻ.`);

        if (this.heroPanelCards.length === 0) {
            this.clearHeroPanel();
            return;
        }

        const cardsHTML = this.heroPanelCards.map(card => this.generateCardHTML(card, true)).join('');
        this.elements.heroPanelCards.innerHTML = cardsHTML;

        this.updateHeroPanelCount();
        
        this.elements.heroPanelEmpty.classList.add('hidden');
        this.elements.heroPanelCards.classList.remove('hidden');
        this.elements.heroPanelFooter.classList.remove('hidden');
        this.elements.heroClearBtn.classList.remove('hidden');

        const selectionButtons = document.getElementById('selection-buttons');
        if (selectionButtons) {
            selectionButtons.classList.remove('hidden');
        }

        // Sync selected class on the newly rendered cards
        this.updateSelectionUI();

        // Force show footer + clear button (tránh bị ẩn sau clearHeroPanel)
        if (this.elements.heroPanelFooter) {
            this.elements.heroPanelFooter.classList.remove('hidden');
        }
        if (this.elements.heroClearBtn) {
            this.elements.heroClearBtn.classList.remove('hidden');
        }
        
        setTimeout(() => {
            this.updateHeroPanelScrollState();
        }, 100);
    },

    /**
     * NEW: Update hero panel count (show selected / total)
     */
    updateHeroPanelCount() {
        if (!this.elements.heroPanelCount) return;
        
        const total = this.heroPanelCards.length;
        const selected = this.selectedCardIds.length;
        
        if (selected > 0) {
            this.elements.heroPanelCount.innerHTML = `
                <span class="selection-count-badge">
                    ${selected}/${total}
                </span>
            `;
        } else {
            this.elements.heroPanelCount.textContent = total;
        }
    },

    /**
     * Generate card HTML for hero panel
     */
    generateCardHTML(card, isHeroPanel = false) {
        console.log('--- ĐANG VẼ THẺ ---', JSON.parse(JSON.stringify(card)));
        const createdDate = card.createdAt ? new Date(card.createdAt) : new Date();
        const now = new Date();
        const hoursDiff = (now - createdDate) / (1000 * 60 * 60);
        const hoursOnShelf = Math.floor(hoursDiff);

        const isOverdue = hoursDiff >= this.OVERDUE_THRESHOLD_HOURS;
        const overdueClass = isOverdue ? 'card-overdue-alert' : '';
        const badgeClass = isOverdue ? 'overdue-badge-alert' : '';

        const dateObj = new Date(card.createdAt || new Date());
        const dateStr = dateObj.toLocaleDateString('vi-VN', {
            day: '2-digit',
            month: '2-digit',
            year: 'numeric'
        });
        const timeStr = dateObj.toLocaleTimeString('vi-VN', {
            hour: '2-digit',
            minute: '2-digit',
            hour12: false
        });

        const moNumbers = Array.isArray(card.moNumbers)
            ? card.moNumbers
            : (card.moNumber ? [card.moNumber] : []);

        if (isHeroPanel) {
            const area = ({ A: 'Fiber', B: 'Fiber', C: 'Kit', D: 'Kit', E: 'Kit', F: 'Kit', G: 'Kit',
                I: 'Cushion', K: 'Cushion', M: 'Decking', N: 'Decking' })[card.shelfCode?.[0]] || 'Chưa phân khu';
            const selected = this.selectedCardIds.includes(card.id);
            return `<div class="hero-search-card-multi search-result-card ${selected ? 'selected' : ''}"
                data-card-id="${this.escapeHtml(card.id)}" data-shelf="${this.escapeHtml(card.shelfCode || '')}"
                role="button" tabindex="0" aria-pressed="${selected}"
                onclick="UIController.toggleCardSelection(this.dataset.cardId)"
                onkeydown="if(event.key==='Enter'||event.key===' '){event.preventDefault();UIController.toggleCardSelection(this.dataset.cardId)}">
                <div class="search-result-location">${this.escapeHtml(area)} · <strong>${this.escapeHtml(card.shelfCode || '')}</strong></div>
                <div class="search-result-codes">${moNumbers.map(mo => `<div>MO: ${this.escapeHtml(mo)}</div>`).join('')}</div>
                <div class="search-result-date">${dateStr} · ${timeStr}${isOverdue ? ` · Lưu kho ${hoursOnShelf}h` : ''}</div>
            </div>`;
        }

        // Vehicle badge (if any)
        let vehicleBadge = '';
        if (card.vehicleInfo) {
            const { vehicleNumber, totalVehicles, baseMO } = card.vehicleInfo;
            vehicleBadge = `
                <div class="vehicle-badge" title="Xe ${vehicleNumber}/${totalVehicles} của MO ${baseMO}">
                    <iconify-icon icon="solar:delivery-bold" width="10"></iconify-icon>
                    <span>${vehicleNumber}/${totalVehicles}</span>
                </div>
            `;
        }

        // Build fixed 3-slot MO lines for consistent card height
        const moLinesHTML = [];
        for (let i = 0; i < 3; i++) {
            if (i < moNumbers.length) {
                moLinesHTML.push(`
                    <div class="mo-line" data-mo="${moNumbers[i]}">
                        <div class="flex-1 min-w-0">
                            <div class="text-base"
                                style="
                                    color: var(--olive-950);
                                    letter-spacing: -0.008em;
                                    line-height: 1.2;
                                "
                                title="${moNumbers[i]}">
                                MO: ${moNumbers[i]}
                            </div>
                        </div>
                        ${i === 0 ? vehicleBadge : ''}
                    </div>
                `);
            } else {
                // Invisible placeholder to keep height consistent
                moLinesHTML.push(`
                    <div class="mo-line mo-placeholder">
                        <div class="flex-1 min-w-0">
                            <div class="text-base" style="color: transparent; line-height: 1.2;">
                                ···
                            </div>
                        </div>
                    </div>
                `);
            }
        }

        const isSelected = this.selectedCardIds.includes(card.id);
        const selectedClass = isSelected ? 'selected' : '';

        return `
            <div class="hero-search-card-multi group relative ${overdueClass} ${selectedClass}" 
                data-card-id="${card.id}" 
                data-shelf="${card.shelfCode || ''}"
                style="height: 173px !important; min-height: 173px !important; max-height: 173px !important; display: flex !important; flex-direction: column !important; overflow: hidden;"
                onclick="UIController.toggleCardSelection('${card.id}')">

                <div class="card-location">
                    <div class="location-icon">
                        <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                            <path d="M21 10c0 7-9 13-9 13s-9-6-9-13a9 9 0 0 1 18 0z"></path>
                            <circle cx="12" cy="10" r="3"></circle>
                        </svg>
                    </div>
                    <div class="location-text" title="${card.shelfCode || ''}">
                        ${card.shelfCode || ''}
                    </div>
                </div>

                <div class="card-datetime">
                    <div class="datetime-icon">
                        <svg xmlns="http://www.w3.org/2000/svg" width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                            <rect x="3" y="4" width="18" height="18" rx="2" ry="2"></rect>
                            <line x1="16" y1="2" x2="16" y2="6"></line>
                            <line x1="8" y1="2" x2="8" y2="6"></line>
                            <line x1="3" y1="10" x2="21" y2="10"></line>
                        </svg>
                    </div>
                    <div class="datetime-text" title="${card.timestamp || ''}">
                        ${dateStr} • ${timeStr}
                    </div>
                </div>

                <div class="px-3 pb-3">
                    ${moLinesHTML.join('')}
                </div>
                
                ${isOverdue ? `
                    <div class="overdue-badge ${badgeClass}"
                        title="${hoursOnShelf} giờ trên kệ - QUÁ 3 NGÀY!">
                        <iconify-icon icon="solar:danger-triangle-bold" width="12"></iconify-icon>
                        <span>${hoursOnShelf}h</span>
                    </div>
                ` : ''}
            </div>
        `;
    },

    /**
     * Clear hero panel
     */
    clearHeroPanel() {
        this.heroPanelCards = [];
        this.selectedCardIds = []; 
        this.elements.heroPanelEmpty.classList.remove('hidden');
        this.elements.heroPanelCards.classList.add('hidden');
        this.elements.heroPanelFooter.classList.add('hidden');
        this.elements.heroClearBtn.classList.add('hidden');

        const selectionButtons = document.getElementById('selection-buttons');
        if (selectionButtons) {
            selectionButtons.classList.add('hidden');
        }
        
        const wrapper = document.getElementById('hero-panel-body-wrapper');
        const heroPanelBody = document.getElementById('hero-panel-body');
        if (heroPanelBody) {
            heroPanelBody.classList.remove('hero-panel-has-selection');
        }   
        if (wrapper) {
            wrapper.classList.remove('has-scroll', 'scrolled-to-bottom');
        }
    },

    /**
     * UPDATED: Remove only SELECTED cards from shelves
     */
    // Thay thế toàn bộ hàm removeHeroPanelFromShelves cũ bằng hàm này
    async removeHeroPanelFromShelves() {
        const selectedCards = this.selectedCardIds.length === 0
            ? [...this.heroPanelCards]
            : this.heroPanelCards.filter(card => this.selectedCardIds.includes(card.id));
        if (selectedCards.length === 0) {
            BarcodeScanner.showScanFeedback('Không có MO nào để lấy ra!', 'info');
            return;
        }

        // Use the current server snapshot. Shelf code is not a valid entry ID.
        const removalList = [];
        for (const panelCard of selectedCards) {
            const cardId = panelCard.isVirtual ? panelCard.originalCardId : panelCard.id;
            const currentCard = WIPManager.cards.find(card => card.id === cardId);
            const moNumber = panelCard.moNumbers?.[0];
            const entryId = currentCard?.moEntryIds?.[moNumber];
            if (!currentCard || !moNumber || !entryId) {
                await WipApi.refresh().catch(() => {});
                BarcodeScanner.showScanFeedback('Dữ liệu kệ đã thay đổi. Vui lòng tìm lại MO trước khi xuất.', 'warning');
                return;
            }
            removalList.push({ cardId, moNumber, entryId });
        }

        if (!confirm(`Xác nhận lấy ${removalList.length} MO khỏi kệ?\n\n${removalList.map(item => `${item.moNumber} (tại ${WIPManager.cards.find(card => card.id === item.cardId)?.shelfCode || ''})`).join('\n')}`)) return;
        const success = await WIPManager.removeMOs(removalList);
        if (!success) {
            BarcodeScanner.showScanFeedback('Không thể xuất MO vì dữ liệu kệ đã thay đổi. Danh sách đã được cập nhật.', 'error');
            return;
        }

        const removedIds = new Set(selectedCards.map(card => card.id));
        const remainingCards = this.heroPanelCards
            .filter(card => !removedIds.has(card.id))
            .map(card => JSON.parse(JSON.stringify(card)));
        this.clearHeroPanel();
        if (remainingCards.length) this.updateHeroPanel(remainingCards);
        BarcodeScanner.showScanFeedback(`✓ Đã lấy ${removalList.length} MO khỏi kệ`, 'success');
    },

    /**
     * Set mode (add/find)
     */
    setMode(mode) {
        console.log('═════════════════════════════════════');
        console.log(`🎯 setMode() called with mode: "${mode}"`);
        console.log('═════════════════════════════════════');
        
        const addModeBtn = document.getElementById('add-mode-btn');
        const findModeBtn = document.getElementById('find-mode-btn');
        
        console.log('Button elements found:', {
            addModeBtn: !!addModeBtn,
            findModeBtn: !!findModeBtn
        });
        
        const addMOInput = document.getElementById('addMOInput');
        const addMOBtn = document.getElementById('addMOBtn');
        const addMOContainer = addMOInput?.closest('.glass-light');
        
        const searchMOInput = document.getElementById('searchMOInput');
        const searchMOBtn = document.getElementById('searchMOBtn');
        const searchMOContainer = searchMOInput?.closest('.glass-light');
        
        const heroPanelBody = document.getElementById('hero-panel-body');
        
        console.log('Input elements found:', {
            addMOInput: !!addMOInput,
            addMOBtn: !!addMOBtn,
            addMOContainer: !!addMOContainer,
            searchMOInput: !!searchMOInput,
            searchMOBtn: !!searchMOBtn,
            searchMOContainer: !!searchMOContainer
        });
        
        if (!addModeBtn || !findModeBtn) {
            console.error('❌ Mode buttons not found in DOM!');
            return;
        }
        
        if (mode === 'add') {
            console.log('➡️ Activating ADD mode...');
            
            // Toggle button states
            addModeBtn.classList.add('active');
            findModeBtn.classList.remove('active');
            console.log('✓ Button classes toggled');
            
            // Enable Add MO section
            if (addMOContainer) {
                addMOContainer.classList.remove('opacity-50', 'pointer-events-none');
                console.log('✓ Add MO container enabled');
            }
            if (addMOInput) {
                addMOInput.disabled = false;
                setTimeout(() => {
                    addMOInput.focus();
                    console.log('✓ Add MO input focused');
                }, 100);
            }
            if (addMOBtn) {
                addMOBtn.disabled = false;
                console.log('✓ Add MO button enabled');
            }
            
            // Disable Find MO section
            if (searchMOContainer) {
                searchMOContainer.classList.add('opacity-50', 'pointer-events-none');
                console.log('✓ Search container disabled');
            }
            if (searchMOInput) {
                searchMOInput.disabled = true;
            }
            if (searchMOBtn) {
                searchMOBtn.disabled = true;
            }
            if (heroPanelBody) {
                const panel = heroPanelBody.parentElement;
                if (panel) {
                    panel.classList.add('opacity-50', 'pointer-events-none');
                    console.log('✓ Hero panel disabled');
                }
            }
            
            console.log('✅ ADD MO MODE ACTIVATED');
            
        } else if (mode === 'find') {
            document.querySelectorAll('[data-product-type]').forEach(button => {
                button.classList.remove('active');
                button.setAttribute('aria-pressed', 'false');
            });
            console.log('➡️ Activating FIND mode...');
            
            // Toggle button states
            findModeBtn.classList.add('active');
            addModeBtn.classList.remove('active');
            console.log('✓ Button classes toggled');
            
            // Enable Find MO section
            if (searchMOContainer) {
                searchMOContainer.classList.remove('opacity-50', 'pointer-events-none');
                console.log('✓ Search container enabled');
            }
            if (searchMOInput) {
                searchMOInput.disabled = false;
                setTimeout(() => {
                    searchMOInput.focus();
                    console.log('✓ Search input focused');
                }, 100);
            }
            if (searchMOBtn) {
                searchMOBtn.disabled = false;
            }
            if (heroPanelBody) {
                const panel = heroPanelBody.parentElement;
                if (panel) {
                    panel.classList.remove('opacity-50', 'pointer-events-none');
                    console.log('✓ Hero panel enabled');
                }
            }
            
            // Disable Add MO section
            if (addMOContainer) {
                addMOContainer.classList.add('opacity-50', 'pointer-events-none');
                console.log('✓ Add MO container disabled');
            }
            if (addMOInput) {
                addMOInput.disabled = true;
            }
            if (addMOBtn) {
                addMOBtn.disabled = true;
            }
            
            console.log('✅ FIND MO MODE ACTIVATED');
        }
        
        console.log('═════════════════════════════════════\n');
    },

    async handleCheckMoStatus() {
        const input = document.getElementById('mo-status-input');
        const resultsContainer = document.getElementById('mo-status-results');
        const singleContainer = document.getElementById('mo-status-single');
        const familyContainer = document.getElementById('mo-status-family');
        const moNumber = input.value.trim().toUpperCase();
        if (!moNumber) return;

        const inEl = resultsContainer.querySelector('#mo-status-in span');
        const outEl = resultsContainer.querySelector('#mo-status-out span');
        const durationEl = resultsContainer.querySelector('#mo-status-duration span');
        const notFoundEl = resultsContainer.querySelector('#mo-status-not-found');
        const baseMO = WIPManager.getBaseMO(moNumber);
        const formatTime = value => value ? new Date(value).toLocaleString('vi-VN') : '---';
        const renderFamily = items => {
            singleContainer.classList.add('hidden');
            familyContainer.classList.remove('hidden');
            familyContainer.innerHTML = items.map(item => `
                <div class="mo-family-status-item">
                    <div class="mo-family-status-title">
                        <strong>${this.escapeHtml(item.moNumber)}</strong>
                        ${item.shelfCode ? `<span>Vị trí ${this.escapeHtml(item.shelfCode)}</span>` : ''}
                    </div>
                    <div><strong>Scan In:</strong> ${formatTime(item.scanIn)}</div>
                    <div><strong>Scan Out:</strong> ${item.inWarehouse ? 'Đang trong kho' : formatTime(item.scanOut)}</div>
                    <div><strong>Thời gian lưu kho:</strong> ${this.escapeHtml(item.duration || '---')}</div>
                </div>`).join('');
        };

        resultsContainer.classList.remove('hidden');
        document.getElementById('mo-status-close-btn')?.classList.remove('hidden');
        document.querySelector('.hero-section')?.classList.add('mo-status-expanded');
        singleContainer.classList.remove('hidden');
        familyContainer.classList.add('hidden');
        familyContainer.replaceChildren();
        notFoundEl.classList.add('hidden');
        inEl.textContent = 'Đang tìm...';
        outEl.textContent = '---';
        durationEl.textContent = '---';

        const activeFamily = WIPManager.getVehiclesByBaseMO(baseMO).flatMap(card =>
            (card.moNumbers || []).filter(mo => WIPManager.getBaseMO(mo).toUpperCase() === baseMO)
                .map(mo => ({ moNumber: mo, shelfCode: card.shelfCode,
                    scanIn: card.moCreatedAt?.[mo] || card.createdAt, scanOut: null,
                    duration: null, inWarehouse: true })));

        if (activeFamily.length > 1 || activeFamily.some(item => item.moNumber.toUpperCase() !== moNumber)) {
            renderFamily(activeFamily.sort((a, b) => a.moNumber.localeCompare(b.moNumber, undefined, { numeric: true })));
            return;
        }
        if (activeFamily.length === 1) {
            inEl.textContent = formatTime(activeFamily[0].scanIn);
            outEl.textContent = 'Đang trong kho';
            durationEl.textContent = '---';
            return;
        }

        if (typeof LogManager !== 'undefined') {
            const familyHistory = await LogManager.getMoFamilyHistory(baseMO);
            if (familyHistory.length > 1 || familyHistory.some(item => item.moNumber.toUpperCase() !== moNumber)) {
                renderFamily(familyHistory.map(item => ({ ...item, inWarehouse: !item.scanOut })));
                return;
            }
            const history = familyHistory[0] || await LogManager.getMoHistory(moNumber);
            if (history.scanIn) {
                inEl.textContent = formatTime(history.scanIn);
                outEl.textContent = history.scanOut ? formatTime(history.scanOut) : 'Chưa scan out';
                durationEl.textContent = history.duration || '---';
                return;
            }
        }

        notFoundEl.classList.remove('hidden');
        inEl.textContent = '---';
        outEl.textContent = '---';
        durationEl.textContent = '---';
    },

    closeMoStatus() {
        document.getElementById('mo-status-results')?.classList.add('hidden');
        document.getElementById('mo-status-close-btn')?.classList.add('hidden');
        const input = document.getElementById('mo-status-input');
        if (input) {
            input.value = '';
            input.focus();
        }
        document.querySelector('.hero-section')?.classList.remove('mo-status-expanded');
    },

    /**
     * Update overdue ticker
     */
    updateOverdueTicker() {
        const tickerTrack = document.getElementById('overdue-ticker-track');
        const tickerCount = document.getElementById('overdue-ticker-count');
        const tickerBanner = document.getElementById('overdue-ticker-banner');
        if (!tickerTrack || !tickerBanner) return;

        const now = new Date();
        const overdueCards = WIPManager.getAll().filter(card => {
            const created = card.createdAt ? new Date(card.createdAt) : new Date();
            return (now - created) / (1000 * 60 * 60) >= this.OVERDUE_THRESHOLD_HOURS;
        });

        if (overdueCards.length === 0) {
            tickerBanner.classList.add('ticker-hidden');
            return;
        }

        tickerBanner.classList.remove('ticker-hidden');
        if (tickerCount) tickerCount.textContent = overdueCards.length;

        const items = overdueCards.map(card => {
            const moNumbers = card.moNumbers || [card.moNumber];
            const hrs = Math.floor((now - new Date(card.createdAt)) / (1000 * 60 * 60));
            return `<span class="ticker-item">
                <span class="ticker-dot"></span>
                <span class="ticker-mo">${moNumbers.join(', ')}</span>
                <span class="ticker-loc">${card.shelfCode}</span>
                <span class="ticker-hrs">${hrs}h</span>
            </span>`;
        }).join('');

        tickerTrack.innerHTML = `<div class="ticker-content ticker-measure-only" style="visibility:hidden;">${items}</div>`;

        requestAnimationFrame(() => {
            const measureEl = tickerTrack.querySelector('.ticker-measure-only');
            if (!measureEl) return;
            const contentWidth = measureEl.offsetWidth;

            tickerTrack.innerHTML = `
                <div class="ticker-content ticker-copy-a">${items}</div>
                <div class="ticker-content ticker-copy-b">${items}</div>
            `;

            const duration = Math.max(15, Math.round(contentWidth / 80));

            let styleEl = document.getElementById('ticker-keyframes');
            if (!styleEl) {
                styleEl = document.createElement('style');
                styleEl.id = 'ticker-keyframes';
                document.head.appendChild(styleEl);
            }
            styleEl.textContent = `
                @keyframes ticker-scroll {
                    0%   { transform: translateX(0); }
                    100% { transform: translateX(-${contentWidth}px); }
                }
                .ticker-copy-a {
                    animation: ticker-scroll ${duration}s linear infinite;
                }
                .ticker-copy-b {
                    animation: ticker-scroll ${duration}s linear infinite;
                    animation-delay: -${duration / 2}s;
                }
                .ticker-track:hover .ticker-copy-a,
                .ticker-track:hover .ticker-copy-b {
                    animation-play-state: paused;
                }
                @media (prefers-reduced-motion: reduce) {
                    .ticker-copy-a { animation: none; }
                    .ticker-copy-b { display: none; }
                }
            `;
        });
    }
};

