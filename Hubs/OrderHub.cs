using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;          // ✅ cần cho FirstOrDefaultAsync       

namespace OrderTrackingWeb.Hubs
{
    /// <summary>
    /// SignalR Hub để xử lý real-time updates cho Order Tracking
    /// </summary>
    public class OrderHub : Hub
    {
        // Dictionary để track số người online (static để share giữa các instance),
        private readonly BlowFillDbContext _blowDb;
        public OrderHub(BlowFillDbContext blowDb)
        {
            _blowDb = blowDb;
        }
        private static readonly Dictionary<string, DateTime> ConnectedUsers = new();
        private static readonly object LockObject = new();

        /// <summary>
        /// Được gọi khi client kết nối thành công
        /// </summary>
        public override async Task OnConnectedAsync()
        {
            string connectionId = Context.ConnectionId;
            
            lock (LockObject)
            {
                ConnectedUsers[connectionId] = DateTime.Now;
            }
            
            Console.WriteLine($"✅ Client connected: {connectionId} (Total: {ConnectedUsers.Count})");
            
            // Gửi số người online đến tất cả client
            await Clients.All.SendAsync("UserCountChanged", ConnectedUsers.Count);
            
            // Gửi welcome message cho client mới kết nối
            await Clients.Caller.SendAsync("Connected", new
            {
                connectionId = connectionId,
                message = "Connected to Order Tracking Hub",
                serverTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
            
            await base.OnConnectedAsync();
        }

        /// <summary>
        /// Được gọi khi client ngắt kết nối
        /// </summary>
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            string connectionId = Context.ConnectionId;
            
            lock (LockObject)
            {
                ConnectedUsers.Remove(connectionId);
            }
            
            if (exception != null)
            {
                Console.WriteLine($"❌ Client disconnected with error: {connectionId} - {exception.Message}");
            }
            else
            {
                Console.WriteLine($"👋 Client disconnected: {connectionId} (Total: {ConnectedUsers.Count})");
            }
            
            // Thông báo số người online mới
            await Clients.All.SendAsync("UserCountChanged", ConnectedUsers.Count);
            
            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// Client gọi để thông báo đã quét barcode
        /// </summary>
        /// <param name="odrno">Mã MX</param>
        /// <param name="status">Trạng thái (Received/Lack/NOT FOUND)</param>
        /// <param name="note">Ghi chú (nếu có)</param>
        public async Task NotifyOrderUpdate(string odrno, string status, string note = "")
        {
            Console.WriteLine($"📡 Broadcasting order update: {odrno} → {status}");
            
            // Gửi đến TẤT CẢ client (bao gồm cả người gửi)
            await Clients.All.SendAsync("OrderUpdated", new
            {
                odrno = odrno,
                status = status,
                note = note,
                time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                updatedBy = Context.ConnectionId
            });
        }

        /// <summary>
        /// Client gọi để yêu cầu refresh toàn bộ dashboard
        /// </summary>
        public async Task RequestRefresh()
        {
            Console.WriteLine($"🔄 Broadcasting refresh request from {Context.ConnectionId}");
            
            // Gửi đến tất cả client trừ người gửi
            await Clients.Others.SendAsync("RefreshRequested", new
            {
                requestedBy = Context.ConnectionId,
                time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }

        /// <summary>
        /// Server gọi để thông báo có MX mới từ file sync
        /// </summary>
        /// <param name="date">Ngày của file</param>
        /// <param name="fileType">Loại file (Console Lid / Other)</param>
        /// <param name="orderCount">Số lượng MX mới</param>
        public async Task NotifyNewOrders(string date, string fileType, int orderCount)
        {
            Console.WriteLine($"📢 Broadcasting new orders: {date} - {fileType} ({orderCount} orders)");
            
            await Clients.All.SendAsync("NewOrdersAdded", new
            {
                date = date,
                fileType = fileType,
                orderCount = orderCount,
                time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }

        /// <summary>
        /// Client gọi để gửi tin nhắn chat (bonus feature)
        /// </summary>
        public async Task SendMessage(string user, string message)
        {
            Console.WriteLine($"💬 Chat: [{user}] {message}");
            
            await Clients.All.SendAsync("ReceiveMessage", new
            {
                user = user,
                message = message,
                time = DateTime.Now.ToString("HH:mm:ss")
            });
        }

        /// <summary>
        /// Lấy số người đang online
        /// </summary>
        public int GetOnlineCount()
        {
            lock (LockObject)
            {
                return ConnectedUsers.Count;
            }
        }

        public static int OnlineUserCount
        {
            get
            {
                lock (LockObject)
                {
                    return ConnectedUsers.Count;
                }
            }
        }

        /// <summary>
        /// Lấy danh sách connection IDs đang online
        /// </summary>
        public List<string> GetOnlineUsers()
        {
            lock (LockObject)
            {
                return ConnectedUsers.Keys.ToList();
            }
        }
        /// <summary>
        /// Cho client join vào group theo MachineId (dùng cho BlowFill)
        /// </summary>
        public async Task JoinGroup(string machineId)
        {
            if (!string.IsNullOrWhiteSpace(machineId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, machineId.Trim());
                Console.WriteLine($"🔗 Connection {Context.ConnectionId} joined group '{machineId.Trim()}'");
            }
        }

        /// <summary>
        /// Nhận dữ liệu cân từ BlowFillClient và broadcast cho các trình duyệt.
        /// </summary>
        public async Task PushWeightFromClient(string machineId, double weight)
        {
            if (string.IsNullOrWhiteSpace(machineId)) return;
            var machine = machineId.Trim();
            await Clients.Group(machine).SendAsync("ReceiveScaleData", weight);
        }

        /// <summary>
        /// Broadcast context BlowFill (MO, Fiber kit, Target weight, số step)
        /// cho tất cả client trong group MachineId.
        /// </summary>
        public async Task BroadcastBlowFillContext(
            string machineId,
            string mo,
            string fiberKit,
            double targetWeight,
            int totalSteps,
            int currentStep,
            int currentPartIndex
        )
        {
            if (string.IsNullOrWhiteSpace(machineId)) return;

            string machine = machineId.Trim();

            // 1. Broadcast cho tất cả client trong group MachineId
            await Clients.OthersInGroup(machine).SendAsync("BlowFillContextUpdated", new
            {
                machineId = machine,
                mo,
                fiberKit,
                targetWeight,
                totalSteps,
                currentStep,
                currentPartIndex
            });

            // 2. Lưu trạng thái hiện tại vào DB
            try
            {
                var existing = await _blowDb.BlowFillContexts
                    .FirstOrDefaultAsync(c => c.MachineId == machine);

                if (existing == null)
                {
                    existing = new BlowFillContext
                    {
                        MachineId = machine
                    };
                    _blowDb.BlowFillContexts.Add(existing);
                }

                existing.MO = mo ?? "";
                existing.FiberKit = fiberKit ?? "";
                existing.TargetWeight = targetWeight;
                existing.TotalSteps = totalSteps;
                existing.LastUpdate = DateTime.Now;

                await _blowDb.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error saving BlowFillContext: {ex.Message}");
            }

            Console.WriteLine($"📡 BlowFillContextUpdated → Machine={machine}, MO={mo}, FiberKit={fiberKit}, Target={targetWeight}, Steps={totalSteps}");
        }
    }
}
