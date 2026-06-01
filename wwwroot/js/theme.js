// Chạy ngay lập tức khi file được nạp để tránh hiện tượng chớp màn hình (FOUC)
(function() {
    const savedTheme = localStorage.getItem('globalTheme');
    if (savedTheme === 'simple') {
        document.documentElement.classList.add('simple-view');
    }

    // Khi trang đã load xong HTML thì gắn sự kiện cho nút bấm
    document.addEventListener('DOMContentLoaded', () => {
        const btnToggles = document.querySelectorAll('.btn-global-toggle');
        
        function updateButtonText() {
            const isSimple = document.documentElement.classList.contains('simple-view');
            btnToggles.forEach(btn => {
                btn.innerHTML = isSimple ? 'Dark' : 'Light';
            });
        }

        btnToggles.forEach(btn => {
            btn.addEventListener('click', () => {
                document.documentElement.classList.toggle('simple-view');
                const isSimple = document.documentElement.classList.contains('simple-view');
                localStorage.setItem('globalTheme', isSimple ? 'simple' : 'dark');
                updateButtonText();
            });
        });

        updateButtonText();
    });
})();
// ==================== LOGIC NÚT SCROLL TO TOP ====================
document.addEventListener('DOMContentLoaded', () => {
    const scrollToTopBtn = document.getElementById('scrollToTopBtn');

    // Chỉ thực thi nếu nút này tồn tại trên trang
    if (scrollToTopBtn) {
        
        // 1. Lắng nghe sự kiện cuộn chuột của trang
        window.onscroll = function() {
            // Nếu cuộn xuống quá 100px thì hiện nút, ngược lại thì ẩn đi
            if (document.body.scrollTop > 100 || document.documentElement.scrollTop > 100) {
                scrollToTopBtn.style.display = "block";
            } else {
                scrollToTopBtn.style.display = "none";
            }
        };

        // 2. Lắng nghe sự kiện click vào nút
        scrollToTopBtn.addEventListener('click', () => {
            // Cuộn mượt mà về đầu trang
            window.scrollTo({
                top: 0,
                behavior: 'smooth'
            });
        });
    }
});

