/**
 * Barcode Scanner Module
 * USB/Hardware barcode scanner only (no camera)
 * AUTO SHELF ASSIGNMENT - Operator only scans MO
 */

const BarcodeScanner = {
    config: {
        scanTimeout: 100,
        successBeep: true,
        errorBeep: true,
        continuousMode: false,
        debugMode: false
    },

    state: {
        scanBuffer: '',
        scanTimer: null,
        lastScanTime: 0
    },

    /**
     * Initialize the barcode scanner
     */
    init() {
        console.log('Initializing Barcode Scanner (Auto Shelf Assignment Mode)...');
        this.initUSBScanner();
        this.log('Barcode Scanner initialized - MO only, auto shelf');
    },

    /**
     * Initialize USB barcode scanner listener
     */
    initUSBScanner() {
        document.addEventListener('keypress', (e) => {
            const target = e.target;
            
            if (target.id === 'searchMOInput') {
                return;
            }
            
            if (target.id === 'addMOInput') {
                return;
            }
            
            if (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA') {
                return;
            }

            this.handleUSBScanInput(e);
        });

        this.log('USB Scanner listener initialized');
    },

    /**
     * Handle USB scanner input
     */
    handleUSBScanInput(event) {
        const char = event.key;

        if (char === 'Enter') {
            if (this.state.scanBuffer.length > 0) {
                this.processScan(this.state.scanBuffer.trim());
                this.state.scanBuffer = '';
            }
            return;
        }

        this.state.scanBuffer += char;

        clearTimeout(this.state.scanTimer);
        this.state.scanTimer = setTimeout(() => {
            if (this.state.scanBuffer.length > 0) {
                this.processScan(this.state.scanBuffer.trim());
                this.state.scanBuffer = '';
            }
        }, this.config.scanTimeout);
    },

    /**
     * Process scanned barcode - AUTO SHELF ASSIGNMENT
     */
    async processScan(scannedCode) {
        this.log(`Scanned MO: ${scannedCode}`);
        
        // Validate scanned code
        if (!scannedCode || scannedCode.trim() === '') {
            this.playError();
            this.showScanFeedback('❌ Mã MO không hợp lệ!', 'error');
            return;
        }
        
        const trimmedCode = scannedCode.trim();
        
        if (trimmedCode.length < 2) {
            this.playError();
            this.showScanFeedback('❌ Mã MO quá ngắn!', 'error');
            return;
        }

        // Allocate a free shelf on the server inside the write transaction.
        const card = await WIPManager.createCard(null, trimmedCode);
        const nextShelf = card?.shelfCode;

        if (card) {
            UIController.render();
            this.playSuccess();
            this.showCardNotification(
                'success',
                'Thêm MO thành công',
                `<span class="notification-card-message-strong">Vị trí: ${nextShelf}</span>`,
                `MO: "${trimmedCode}"`
            );
            this.log(`Card created: MO ${trimmedCode} → ${nextShelf}`);
        } else {
            this.playError();
            this.showScanFeedback('Lỗi khi tạo thẻ', 'error');
        }
    },

    /**
     * Play success sound
     */
    playSuccess() {
        if (!this.config.successBeep) return;
        
        try {
            const audioContext = new (window.AudioContext || window.webkitAudioContext)();
            const oscillator = audioContext.createOscillator();
            const gainNode = audioContext.createGain();
            
            oscillator.connect(gainNode);
            gainNode.connect(audioContext.destination);
            
            oscillator.frequency.value = 800;
            oscillator.type = 'sine';
            
            gainNode.gain.setValueAtTime(0.3, audioContext.currentTime);
            gainNode.gain.exponentialRampToValueAtTime(0.01, audioContext.currentTime + 0.1);
            
            oscillator.start(audioContext.currentTime);
            oscillator.stop(audioContext.currentTime + 0.1);
        } catch (e) {
            this.log('Audio playback failed: ' + e.message);
        }
    },

    /**
     * Play error sound
     */
    playError() {
        if (!this.config.errorBeep) return;
        
        try {
            const audioContext = new (window.AudioContext || window.webkitAudioContext)();
            const oscillator = audioContext.createOscillator();
            const gainNode = audioContext.createGain();
            
            oscillator.connect(gainNode);
            gainNode.connect(audioContext.destination);
            
            oscillator.frequency.value = 200;
            oscillator.type = 'sawtooth';
            
            gainNode.gain.setValueAtTime(0.3, audioContext.currentTime);
            gainNode.gain.exponentialRampToValueAtTime(0.01, audioContext.currentTime + 0.2);
            
            oscillator.start(audioContext.currentTime);
            oscillator.stop(audioContext.currentTime + 0.2);
        } catch (e) {
            this.log('Audio playback failed: ' + e.message);
        }
    },

    /**
     * Show visual scan feedback (simple toast)
     */
    showScanFeedback(message, type = 'success') {
        const existing = document.getElementById('scan-feedback');
        if (existing) existing.remove();

        const typeClasses = {
            success: 'notification notification-success',
            error: 'notification notification-error',
            info: 'notification notification-info',
            warning: 'notification notification-warning'
        };

        const icons = {
            success: '<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"></polyline></svg>',
            error: '<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"></circle><line x1="15" y1="9" x2="9" y2="15"></line><line x1="9" y1="9" x2="15" y2="15"></line></svg>',
            info: '<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"></circle><line x1="12" y1="16" x2="12" y2="12"></line><line x1="12" y1="8" x2="12.01" y2="8"></line></svg>',
            warning: '<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"></path><line x1="12" y1="9" x2="12" y2="13"></line><line x1="12" y1="17" x2="12.01" y2="17"></line></svg>'
        };

        const feedback = document.createElement('div');
        feedback.id = 'scan-feedback';
        feedback.className = `fixed top-24 left-1/2 transform -translate-x-1/2 ${typeClasses[type]} z-50 animate-fade-in-out`;
        feedback.innerHTML = `
            <span class="notification-icon">${icons[type]}</span>
            <span>${message}</span>
        `;
        
        document.body.appendChild(feedback);

        setTimeout(() => {
            feedback.remove();
        }, 3000);
    },

    /**
     * NEW: Show card-style notification (for important alerts)
     * @param {string} type - 'error', 'warning', 'success', 'info'
     * @param {string} title - Notification title
     * @param {string} message - Notification message
     * @param {string} details - Optional details (shown in monospace box)
     */
    showCardNotification(type, title, message, details = null) {
        // Remove existing card notifications
        const existing = document.getElementById('card-notification');
        if (existing) existing.remove();

        // Icon SVGs
        const icons = {
            error: `<svg class="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"></path>
            </svg>`,
            warning: `<svg class="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z"></path>
            </svg>`,
            success: `<svg class="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z"></path>
            </svg>`,
            info: `<svg class="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"></path>
            </svg>`
        };

        const notification = document.createElement('div');
        notification.id = 'card-notification';
        notification.className = `notification-card notification-card-${type}`;
        
        notification.innerHTML = `
            <div class="notification-card-header">
                <div class="notification-card-icon ${type}">
                    ${icons[type]}
                </div>
                <div class="notification-card-content">
                    <h3 class="notification-card-title ${type}">${title}</h3>
                    <p class="notification-card-message ${type}">${message}</p>
                    ${details ? `<div class="notification-card-details ${type}">${details}</div>` : ''}
                </div>
            </div>
            <button class="notification-card-close" onclick="this.parentElement.remove()">
                <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <line x1="18" y1="6" x2="6" y2="18"></line>
                    <line x1="6" y1="6" x2="18" y2="18"></line>
                </svg>
            </button>
        `;
        
        document.body.appendChild(notification);

        // Auto-remove after 5 seconds
        setTimeout(() => {
            if (notification.parentElement) {
                notification.classList.add('removing');
                setTimeout(() => notification.remove(), 300);
            }
        }, 5000);
    },

    /**
     * Toggle continuous scan mode
     */
    toggleContinuousMode() {
        this.config.continuousMode = !this.config.continuousMode;
        const status = this.config.continuousMode ? 'BẬT' : 'TẮT';
        
        this.showScanFeedback(`Chế độ liên tục: ${status}`, 'info');
        
        this.log(`Continuous mode: ${this.config.continuousMode}`);
        return this.config.continuousMode;
    },

    /**
     * Get current status
     */
    getStatus() {
        return {
            continuousMode: this.config.continuousMode,
            mode: 'auto-assign',
            totalSlots: 684,
            availableSlots: this.getAvailableSlotsCount()
        };
    },

    /**
     * Get count of available slots
     */
    getAvailableSlotsCount() {
        const totalSlots = 684;
        const occupiedSlots = WIPManager.getCount();
        return totalSlots - occupiedSlots;
    },

    /**
     * Manual trigger for testing
     */
    simulateScan(code) {
        this.processScan(code);
    },

    /**
     * Log debug messages
     */
    log(message) {
        if (this.config.debugMode) {
            console.log(`[BarcodeScanner] ${message}`);
        }
    }
};
