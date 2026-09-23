/**
 * Navbar Controller - WITH DELIVERY SCHEDULE DROPDOWN
 */

const NavbarController = {
    elements: {
        navbar: null,
        logo: null,
        logoImage: null,
        logoText: null,
        navLinks: [],
        navBtn: null,
        scheduleToggleBtn: null,
        scheduleDropdown: null,
        scheduleChevron: null
    },

    /**
     * Initialize navbar
     */
    init() {
        this.elements.navbar = document.getElementById('navbar');
        this.elements.logo = document.getElementById('logo');
        this.elements.logoImage = document.getElementById('logoImage');
        this.elements.logoText = document.getElementById('logoText');
        this.elements.navLinks = document.querySelectorAll('.nav-link');
        this.elements.navBtn = document.querySelector('.nav-btn');
        this.elements.scheduleToggleBtn = document.getElementById('schedule-toggle-btn');
        this.elements.scheduleDropdown = document.getElementById('schedule-dropdown');
        this.elements.scheduleChevron = document.getElementById('schedule-chevron');

        window.addEventListener('scroll', () => this.handleScroll());
        this.handleScroll(); // Run once on load

        // Setup dropdown toggle
        this.setupScheduleDropdown();
    },

    /**
     * Setup schedule dropdown toggle
     */
    setupScheduleDropdown() {
        if (!this.elements.scheduleToggleBtn || !this.elements.scheduleDropdown) return;

        // Toggle dropdown on button click
        this.elements.scheduleToggleBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            this.toggleScheduleDropdown();
        });

        // Close dropdown when clicking outside
        document.addEventListener('click', (e) => {
            if (!this.elements.scheduleDropdown.contains(e.target) && 
                !this.elements.scheduleToggleBtn.contains(e.target)) {
                this.closeScheduleDropdown();
            }
        });

        // Update current time every minute
        this.updateCurrentTime();
        setInterval(() => this.updateCurrentTime(), 60000);
    },

    /**
     * Toggle schedule dropdown
     */
    toggleScheduleDropdown() {
        const isOpen = !this.elements.scheduleDropdown.classList.contains('hidden');
        
        if (isOpen) {
            this.closeScheduleDropdown();
        } else {
            this.openScheduleDropdown();
        }
    },

    /**
     * Open schedule dropdown
     */
    openScheduleDropdown() {
        this.elements.scheduleDropdown.classList.remove('hidden');
        if (this.elements.scheduleChevron) {
            this.elements.scheduleChevron.style.transform = 'rotate(180deg)';
        }
    },

    /**
     * Close schedule dropdown
     */
    closeScheduleDropdown() {
        this.elements.scheduleDropdown.classList.add('hidden');
        if (this.elements.scheduleChevron) {
            this.elements.scheduleChevron.style.transform = 'rotate(0deg)';
        }
    },

    /**
     * Update current time display
     */
    updateCurrentTime() {
        const timeEl = document.getElementById('schedule-current-time');
        if (!timeEl) return;

        const now = new Date();
        const timeStr = now.toLocaleTimeString('vi-VN', {
            hour: '2-digit',
            minute: '2-digit',
            hour12: false
        });
        const dateStr = now.toLocaleDateString('vi-VN', {
            day: '2-digit',
            month: '2-digit',
            year: 'numeric'
        });

        timeEl.textContent = `Current time: ${timeStr} - ${dateStr}`;
    },

    handleScroll() {
        const scrolled = window.scrollY > 50;

        if (scrolled) {
            this.elements.navbar.style.background = '#f4f4f0';
            this.elements.navbar.style.backdropFilter = 'none';
            this.elements.navbar.style.borderBottom = '1px solid rgba(12,12,9,0.09)';
            this.elements.navbar.style.boxShadow = '0 1px 16px rgba(12,12,9,0.05)';
            this.elements.navbar.style.paddingTop = '0.75rem';
            this.elements.navbar.style.paddingBottom = '0.75rem';

            if (this.elements.logoImage) {
                this.elements.logoImage.style.height = '3rem';
                this.elements.logoImage.style.width = '3rem';
            }
            
            if (this.elements.navBtn) {
                this.elements.navBtn.style.background = '#0c0c09';
                this.elements.navBtn.style.color = '#fff';
                this.elements.navBtn.style.borderColor = 'transparent';
            }
        } else {
            this.elements.navbar.style.background = 'transparent';
            this.elements.navbar.style.backdropFilter = '';
            this.elements.navbar.style.borderBottom = '1px solid rgba(12,12,9,0.07)';
            this.elements.navbar.style.boxShadow = 'none';
            this.elements.navbar.style.paddingTop = '1rem';
            this.elements.navbar.style.paddingBottom = '1rem';
            
            if (this.elements.logoImage) {
                this.elements.logoImage.style.height = '4rem';
                this.elements.logoImage.style.width = '4rem';
            }
            
            if (this.elements.navBtn) {
                this.elements.navBtn.style.background = 'rgba(12,12,9,0.07)';
                this.elements.navBtn.style.color = '#0c0c09';
                this.elements.navBtn.style.borderColor = 'rgba(12,12,9,0.12)';
            }
        }
    }
};
