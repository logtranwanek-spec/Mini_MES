using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;          // ? c?n cho FirstOrDefaultAsync       

namespace OrderTrackingWeb.Hubs
{
    /// <summary>
    /// SignalR Hub d? x? l� real-time updates cho Order Tracking
    /// </summary>
    public class OrderHub : Hub
    {
        // Dictionary d? track s? ngu?i online (static d? share gi?a c�c instance),
        private readonly BlowFillDbContext _blowDb;
        public OrderHub(BlowFillDbContext blowDb)
        {
            _blowDb = blowDb;
        }
        private static readonly Dictionary<string, DateTime> ConnectedUsers = new();
        private static readonly object LockObject = new();

        /// <summary>
        /// �u?c g?i khi client k?t n?i th�nh c�ng
        /// </summary>
        public override async Task OnConnectedAsync()
        {
            string connectionId = Context.ConnectionId;
            
            lock (LockObject)
            {
                ConnectedUsers[connectionId] = DateTime.Now;
            }
            
            Console.WriteLine($"? Client connected: {connectionId} (Total: {ConnectedUsers.Count})");
            
            // G?i s? ngu?i online d?n t?t c? client
            await Clients.All.SendAsync("UserCountChanged", ConnectedUsers.Count);
            
            // G?i welcome message cho client m?i k?t n?i
            await Clients.Caller.SendAsync("Connected", new
            {
                connectionId = connectionId,
                message = "Connected to Order Tracking Hub",
                serverTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
            
            await base.OnConnectedAsync();
        }

        /// <summary>
        /// �u?c g?i khi client ng?t k?t n?i
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
                Console.WriteLine($"? Client disconnected with error: {connectionId} - {exception.Message}");
            }
            else
            {
                Console.WriteLine($"?? Client disconnected: {connectionId} (Total: {ConnectedUsers.Count})");
            }
            
            // Th�ng b�o s? ngu?i online m?i
            await Clients.All.SendAsync("UserCountChanged", ConnectedUsers.Count);
            
            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// Client g?i d? th�ng b�o d� qu�t barcode
        /// </summary>
        /// <param name="odrno">M� MX</param>
        /// <param name="status">Tr?ng th�i (Received/Lack/NOT FOUND)</param>
        /// <param name="note">Ghi ch� (n?u c�)</param>
        public async Task NotifyOrderUpdate(string odrno, string status, string note = "")
        {
            Console.WriteLine($"?? Broadcasting order update: {odrno} ? {status}");
            
            // G?i d?n T?T C? client (bao g?m c? ngu?i g?i)
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
        /// Client g?i d? y�u c?u refresh to�n b? dashboard
        /// </summary>
        public async Task RequestRefresh()
        {
            Console.WriteLine($"?? Broadcasting refresh request from {Context.ConnectionId}");
            
            // G?i d?n t?t c? client tr? ngu?i g?i
            await Clients.Others.SendAsync("RefreshRequested", new
            {
                requestedBy = Context.ConnectionId,
                time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }

        /// <summary>
        /// Server g?i d? th�ng b�o c� MX m?i t? file sync
        /// </summary>
        /// <param name="date">Ng�y c?a file</param>
        /// <param name="fileType">Lo?i file (Console Lid / Other)</param>
        /// <param name="orderCount">S? lu?ng MX m?i</param>
        public async Task NotifyNewOrders(string date, string fileType, int orderCount)
        {
            Console.WriteLine($"?? Broadcasting new orders: {date} - {fileType} ({orderCount} orders)");
            
            await Clients.All.SendAsync("NewOrdersAdded", new
            {
                date = date,
                fileType = fileType,
                orderCount = orderCount,
                time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }

        /// <summary>
        /// Client g?i d? g?i tin nh?n chat (bonus feature)
        /// </summary>
        public async Task SendMessage(string user, string message)
        {
            Console.WriteLine($"?? Chat: [{user}] {message}");
            
            await Clients.All.SendAsync("ReceiveMessage", new
            {
                user = user,
                message = message,
                time = DateTime.Now.ToString("HH:mm:ss")
            });
        }

        /// <summary>
        /// L?y s? ngu?i dang online
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
        /// L?y danh s�ch connection IDs dang online
        /// </summary>
        public List<string> GetOnlineUsers()
        {
            lock (LockObject)
            {
                return ConnectedUsers.Keys.ToList();
            }
        }
        /// <summary>
        /// Cho client join v�o group theo MachineId (d�ng cho BlowFill)
        /// </summary>
        public async Task JoinGroup(string machineId)
        {
            if (!string.IsNullOrWhiteSpace(machineId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, machineId.Trim());
                Console.WriteLine($"?? Connection {Context.ConnectionId} joined group '{machineId.Trim()}'");
            }
        }

        /// <summary>
        /// Nh?n d? li?u c�n t? BlowFillClient v� broadcast cho c�c tr�nh duy?t.
        /// </summary>
        public async Task PushWeightFromClient(string machineId, double weight)
        {
            if (string.IsNullOrWhiteSpace(machineId)) return;
            var machine = machineId.Trim();
            await Clients.Group(machine).SendAsync("ReceiveScaleData", weight);
        }

        /// <summary>
        /// Broadcast context BlowFill (MO, Fiber kit, Target weight, s? step)
        /// cho t?t c? client trong group MachineId.
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

            // 1. Broadcast cho t?t c? client trong group MachineId
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

            // 2. Luu tr?ng th�i hi?n t?i v�o DB
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
                existing.CurrentStep = currentStep;
                existing.CurrentPartIndex = currentPartIndex;
                existing.LastUpdate = DateTime.Now;

                await _blowDb.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error saving BlowFillContext: {ex.Message}");
            }

            Console.WriteLine($"?? BlowFillContextUpdated ? Machine={machine}, MO={mo}, FiberKit={fiberKit}, Target={targetWeight}, Steps={totalSteps}");
        }
    }
}
