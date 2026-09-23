# Mở rộng kho WIP B3–L2

## Phân khu nhập hàng — 23/09/2026

Mã MO và số xe được quản lý riêng theo khu (C–G, I/K, A/B, M/N). Một MO có một xe ở mỗi khu được giữ mã gốc tại từng khu; thêm xe trong cùng khu mới tạo XE1, XE2… I và K thuộc cùng một khu nên cùng đánh số xe Cushion. Khi mở backend mới, database tự chuyển ràng buộc duy nhất từ toàn kho sang từng khu trong một giao dịch, giữ nguyên ID, thời gian nhập, mã và lịch sử hiện có. Không tự đổi lại hậu tố của hàng đã lưu trước thay đổi này. Xuất hàng vẫn đối chiếu CardId và EntryId để phân biệt các khu có cùng mã.

Thay thế quy tắc chưa phân khu bên dưới: Thêm MO → C–G; Thêm Cushion → I/K (bao gồm hàng I-76–I-91); Thêm Fiber → A/B; Thêm Decking → M/N. Chọn nút loại hàng rồi nhập/quét mã và số xe. Backend nhận `productType`, chỉ cấp ô trống trong khu tương ứng và báo hết chỗ nếu khu đầy; giao dịch nhiều xe được hoàn tác toàn bộ khi không đủ ô. Chọn ô thủ công cũng phải thuộc khu đang chọn. Không tự di chuyển tồn kho cũ. Các nút mới kiểm tra backend hỗ trợ phân khu trước khi gửi nhập hàng; cần triển khai backend mới cùng giao diện.

Các dãy A–P hiện cho phép chọn vị trí để nhập MO, chuyển kệ và xuất kho. Sơ đồ kho và dashboard hiển thị từng vị trí của cả 16 dãy. Xuất cả dãy và hoàn tác vẫn yêu cầu quyền manager.

Chưa gán dãy cho Seat Decking, Fiber hoặc Cushion; chờ xác định vị trí thực tế. Quét tự chọn kệ và nhập nhiều xe vẫn chọn kệ trống trong C–G để giữ luồng kit hiện hành.

Các dãy A/B/H/J/L/M/N/O/P và khu kit C/D/F/G có 76 vị trí mỗi dãy; E có 74. Dãy I nằm trên H, gồm 3 tầng × 25 ô (I-01–I-25, I-26–I-50, I-51–I-75). Dãy K đối diện I, gồm 3 tầng × 30 ô (K-01–K-30, K-31–K-60, K-61–K-90). I và K được bỏ khỏi vị trí cũ. Tổng cộng 1.243 vị trí. Các dãy 76 vị trí hiển thị hai hàng, mỗi hàng 38 ô. Thay đổi sơ đồ không tự chuyển hoặc xóa hàng tồn.

Tài liệu này thay thế giới hạn C–G cho thao tác chọn vị trí và xuất dãy trong README cũ. Không cần chuyển đổi database. Cần phát hành backend cùng giao diện và khởi động lại bản server mới để áp dụng.

## Trang giao hàng xuống WIP WNK3

B3–L2 tự cập nhật kế hoạch qua `POST /api/wip-b3-l2/delivery/sync` khi mở trang, mỗi 5 phút khi tab đang hiển thị, hoặc bằng nút “Cập nhật file kế hoạch”. API đọc file RUN KIT nguồn và lưu kế hoạch mà không tải chi tiết MX; không cần mở hoặc bấm cập nhật ở WNK3. Hai màn hình dùng chung dữ liệu kế hoạch và trạng thái nhận, không có hai bản trạng thái độc lập. Khóa đồng bộ bảo vệ bước ghi kế hoạch khi hai bên cập nhật cùng lúc; các tab B3 dùng thời gian chờ tối thiểu 60 giây giữa các lượt nhập file. Nếu file nguồn lỗi, vẫn đọc danh sách đã lưu và hiển thị thông báo riêng. Cần build và chạy backend mới để có API này; Ctrl+F5 riêng giao diện chưa đủ.

Đường dẫn: `/wip-b3-l2/delivery.html`, có liên kết từ kho và trang nhận WIP WNK3. Chọn ngày/file, tìm MX/MW/FITEM, lọc chưa nhận/đã nhận/nhận thiếu và cuộn đến khung giờ bên nhận.

Trang đọc trực tiếp Orders của bên nhận, không tạo trạng thái nhận riêng. SignalR yêu cầu tải lại khi bên nhận cập nhật; kiểm tra lại mỗi 10 giây và khi quay lại tab để đồng bộ cả khi mất SignalR. Trang giao không xác nhận nhận thay bên dưới và không tự xuất tồn kho khi mở vị trí.

Người dùng xác nhận mã lưu kho là MW. Vị trí được đối chiếu chính xác MW (bỏ khoảng trắng đầu/cuối, không phân biệt hoa/thường), bao gồm các xe `MW-XE1`, `MW-XE2`… Không suy đoán quan hệ từ MX/MO hay tìm chuỗi con. MW không còn tồn sẽ không hiển thị vị trí; việc không tìm thấy tồn không có nghĩa bên nhận đã nhận hàng.

Lead time riêng bên giao đang chờ xác nhận: hiện chỉ hiển thị giờ bên nhận, không tự trừ thời gian. Vùng C–G mặc định là Kit; các dãy khác là “Chưa phân khu”. Khi có phân khu chính thức, thêm cấu hình `WipB3L2:Areas` trong appsettings, với khóa là tên dãy và giá trị là tên khu vực. Cấu hình này chỉ đặt nhãn trên trang giao, không thay đổi luồng tự xếp kit.

API chỉ đọc: `/api/wip-b3-l2/delivery/orders?date=yyyy-MM-dd&fileType=Other` và `/api/wip-b3-l2/delivery/locations?date=yyyy-MM-dd&orderId=...`. Danh sách dùng khóa ngày `dd.MM` hiện có của Orders, cùng giới hạn lưu lịch sử như màn hình nhận.

Nếu server đang chạy chưa có API giao hàng (404 khi tải danh sách), giao diện dùng `/orders` của bên nhận và `/api/wip-b3-l2/state` để tra MW. Chế độ tương thích dùng nhãn Kit C–G và “Chưa phân khu” cho các dãy còn lại; cấu hình nhãn tùy chỉnh cần backend mới. Ngày chưa có dữ liệu được báo riêng, không tự chuyển sang kế hoạch ngày khác.

I: 3 x 25 + 16 = 91. Extra single-tier row below the three-tier rack displays 1-16 and uses unique shelf codes I-76 through I-91. Existing I-01 through I-75 remain unchanged.
