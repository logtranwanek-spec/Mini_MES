
using ClosedXML.Excel;
using ExcelDataReader;
using System.Text.Json;
using System.Text;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using OrderTrackingWeb.Hubs;
using System.Data.Odbc;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
// ===== SETUP DATABASE (ENTITY FRAMEWORK CORE) =====
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddSignalR();
builder.Services.AddHostedService<As400ScanPollingService>();

var app = builder.Build();
// ===== REGISTER ENCODING PROVIDER =====
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
// ===== CONFIGURATION =====
string vDrivePath = @"V:\UPH Support\Public\B2\Data\nhung\LEADTIME B2\RUN KIT - NHẬN KIT";
string rootMssPath = @"V:\Prod & Inv Control\Public\P&IC UPH\01.MSS for UPH\2026";
string schedulePath = @"V:\UPH Support\Public\B2\Data\nhung\LEADTIME B2\LEADTIME UPH SUPPORT";
string localData = @"D:\logtran\1. Project\CI Project\OrderTrackingWeb\Data";
if (!Directory.Exists(localData))
    Directory.CreateDirectory(localData);
// Tự động tạo Database nếu chưa có
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    Console.WriteLine("✅ Database is ready at Data/OrderTracking.db");
}
// ===== HELPER: GET CELL VALUE SAFELY =====
string GetCellValue(DataRow row, int columnIndex)
{
    try
    {
        if (columnIndex >= row.Table.Columns.Count) return "";
        var value = row[columnIndex];
        if (value == null || value == DBNull.Value) return "";
        
        if (value is DateTime dt)
        {
            if (dt.TimeOfDay.TotalSeconds > 0) return dt.ToString("HH:mm");
            else return dt.ToString("dd/MM/yyyy");
        }
        
        if (value is double || value is float || value is decimal)
        {
            double numValue = Convert.ToDouble(value);
            if (numValue == Math.Floor(numValue)) return ((long)numValue).ToString();
            else return numValue.ToString("0.##");
        }
        return value.ToString()?.Trim() ?? "";
    }
    catch { return ""; }
}
// ===== FUNCTION: READ EXCEL FILE (XLSX/XLSB) =====
List<Order> ReadExcelFile(string filePath, string fileType, string dateKey)
{
    var result = new List<Order>();
    try
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration() { LeaveOpen = false, FallbackEncoding = Encoding.GetEncoding(1252) });
        var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration() { ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = false } });
        
        if (dataSet.Tables.Count == 0) return result;
        var table = dataSet.Tables[0];
        
        for (int i = 4; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            if (table.Columns.Count < 4) continue;
            
            string odrno = GetCellValue(row, 3);
            if (string.IsNullOrWhiteSpace(odrno)) continue;
            
            result.Add(new Order
            {
                OdrNo = odrno,
                FItem = GetCellValue(row, 4),
                Mw = GetCellValue(row, 5),
                Qty = GetCellValue(row, 9),
                DeliveryDate = GetCellValue(row, 7),
                DeliveryTime = GetCellValue(row, 8),
                FileType = fileType,
                DateKey = dateKey,
                Status = "Pending", // Mặc định là Pending
                Time = ""
            });
        }
    }
    catch (Exception ex) { Console.WriteLine($"❌ Error reading file: {ex.Message}"); }
    return result;
}
// ==================== API ENDPOINTS ====================
// SYNC ENDPOINT (Đọc từ V Drive và lưu vào Database)
app.MapGet("/sync", async (AppDbContext db, IHubContext<OrderHub> hubContext) =>
{
    try
    {
        Console.WriteLine("🔄 Starting sync to Database...");
        var files = Directory.GetFiles(vDrivePath)
            .Where(f => f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xlsb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).StartsWith("~"))
            .Select(f => new FileInfo(f))
            .ToList();
        var fileGroups = new Dictionary<string, FileInfo>();
        foreach (var fileInfo in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(fileInfo.Name);
            var match = System.Text.RegularExpressions.Regex.Match(fileName, @"(\d{2}[\.\-]\d{2})");
            if (!match.Success) continue;
            string dateKey = match.Value.Replace("-", ".");
            string fileType = fileName.Contains("Console Lid", StringComparison.OrdinalIgnoreCase) ? "Console Lid" : "Other";
            string groupKey = $"{dateKey}_{fileType}";
            if (!fileGroups.ContainsKey(groupKey) || fileInfo.LastWriteTime > fileGroups[groupKey].LastWriteTime)
            {
                fileGroups[groupKey] = fileInfo; // Chỉ giữ lại file mới nhất
            }
        }
        var allNewOrders = new List<Order>();
        foreach (var group in fileGroups)
        {
            var parts = group.Key.Split('_');
            var dateKey = parts[0];
            var fileType = parts[1];
            var fileData = ReadExcelFile(group.Value.FullName, fileType, dateKey);
            allNewOrders.AddRange(fileData);
        }
        // Xử lý lưu vào DB: Giữ nguyên trạng thái (Status) và Ghi chú (Note) của các Order đã tồn tại
        using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            foreach (var newOrder in allNewOrders)
            {
                var existingOrder = await db.Orders.FirstOrDefaultAsync(o => o.OdrNo == newOrder.OdrNo && o.DateKey == newOrder.DateKey);
                if (existingOrder != null)
                {
                    existingOrder.FItem = newOrder.FItem;
                    existingOrder.Mw = newOrder.Mw;
                    existingOrder.Qty = newOrder.Qty;
                    existingOrder.DeliveryDate = newOrder.DeliveryDate;
                    existingOrder.DeliveryTime = newOrder.DeliveryTime;
                    existingOrder.FileType = newOrder.FileType;
                }
                else
                {
                    db.Orders.Add(newOrder);
                }
            }
            var processedDates = allNewOrders.Select(o => o.DateKey).Distinct().ToList();
            foreach (var date in processedDates)
            {
                // Lấy tất cả Order đang có trong DB của ngày này
                var existingOrdersInDb = await db.Orders.Where(o => o.DateKey == date).ToListAsync();
                // Lấy các Order mới đọc từ file Excel của ngày này
                var newOrdersForDate = allNewOrders.Where(o => o.DateKey == date).ToList();
                // Tìm những MX có trong DB nhưng KHÔNG CÓ trong file Excel mới
                var ordersToDelete = existingOrdersInDb
                    .Where(dbOrder => !newOrdersForDate.Any(newO => newO.OdrNo == dbOrder.OdrNo && newO.FileType == dbOrder.FileType))
                    .ToList();
                if (ordersToDelete.Any())
                {
                    // Xóa khỏi bảng Orders
                    db.Orders.RemoveRange(ordersToDelete);
                    // Xóa luôn chi tiết của các MX này khỏi bảng MxDetails (để tránh rác Database)
                    var mxToDelete = ordersToDelete.Select(o => o.OdrNo).ToList();
                    var detailsToDelete = await db.MxDetails.Where(d => mxToDelete.Contains(d.OdrNo)).ToListAsync();
                    db.MxDetails.RemoveRange(detailsToDelete);
                    Console.WriteLine($"  🗑️ ĐÃ DỌN DẸP: Xóa {ordersToDelete.Count} MX không còn tồn tại trong file Excel của ngày {date}");
                }
            }
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            Console.WriteLine($"❌ Sync error: {ex.Message}");
            throw;
        }
        Console.WriteLine($"✅ Synced Orders to Database!");
        // 🚀 Tra cứu chi tiết MX dựa trên NGÀY CỦA FILE DANH SÁCH (DateKey)
        Console.WriteLine("📊 Parsing MX details based on List File Date (DateKey)...");
        
        // Nhóm các đơn hàng theo Ngày của File danh sách (VD: "20.05")
        var ordersByFileDate = allNewOrders.GroupBy(o => o.DateKey);
        foreach (var group in ordersByFileDate)
        {
            string dateKey = group.Key; // VD: "20.05"
            var odrnos = group.Select(o => o.OdrNo).Distinct().ToList();
            Console.WriteLine($"  📅 Đang xử lý file danh sách ngày: {dateKey} → Có {odrnos.Count} MX");
            DateTime parsedDate;
            try 
            {
                // Chuyển "20.05" thành DateTime
                var parts = dateKey.Split('.');
                parsedDate = new DateTime(DateTime.Now.Year, int.Parse(parts[1]), int.Parse(parts[0]));
            } 
            catch 
            { 
                Console.WriteLine($"    ⚠️ Không thể parse ngày từ DateKey: {dateKey}");
                continue; 
            }
            // SỬ DỤNG HÀM TÌM THƯ MỤC THÔNG MINH
            string? exactInhousePath = FindInhouseFolder(parsedDate, rootMssPath);
            
            if (exactInhousePath == null) 
            {
                Console.WriteLine($"    ⚠️ Bỏ qua ngày {dateKey} vì không tìm thấy folder INHOUSE.");
                continue;
            }
            // Tạo pattern tìm file XLSB (VD: "May 20")
            string monthName = parsedDate.ToString("MMM", new System.Globalization.CultureInfo("en-US"));
            var searchPatterns = new[] { $"{monthName} {parsedDate.Day}", $"{monthName} {parsedDate.Day:D2}", $"{monthName}{parsedDate.Day}" };
            
            FileInfo? xlsbFile = null;
            foreach (var pattern in searchPatterns)
            {
                var foundFiles = Directory.GetFiles(exactInhousePath)
                    .Where(f => f.EndsWith(".xlsb", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).StartsWith("~") && Path.GetFileName(f).Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTime).ToList();
                
                if (foundFiles.Count > 0)
                {
                    xlsbFile = foundFiles.First();
                    break;
                }
            }
            if (xlsbFile == null) 
            {
                Console.WriteLine($"    ⚠️ Không tìm thấy file XLSB nào có chữ '{searchPatterns[0]}' trong folder {exactInhousePath}");
                continue;
            }
            Console.WriteLine($"    ✅ Tìm thấy file chi tiết: {xlsbFile.Name}");
            var details = await ParseMxDetailsFromXlsb(xlsbFile.FullName, odrnos);
            
            // Xóa cache cũ của các MX này và lưu cache mới
            var oldDetails = db.MxDetails.Where(m => odrnos.Contains(m.OdrNo));
            db.MxDetails.RemoveRange(oldDetails);
            db.MxDetails.AddRange(details);
            await db.SaveChangesAsync();
        }
        await hubContext.Clients.All.SendAsync("MasterFileSynced", new
        {
            message = "Master file has been updated",
            time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        });
        
        Console.WriteLine("📡 Broadcasted sync completion to all clients");
        // =====================================================================
        // 🧹 TỰ ĐỘNG DỌN DẸP DỮ LIỆU CŨ (LƯU LỊCH SỬ 21 NGÀY)
        // =====================================================================
        Console.WriteLine("🧹 Đang dọn dẹp dữ liệu cũ hơn 21 ngày...");
        try
        {
            DateTime cutoffDate = DateTime.Now.Date.AddDays(-21);
            
            // 1. Dọn dẹp bảng Orders & MxDetails
            var allDbOrders = await db.Orders.ToListAsync();
            var ordersToDelete = new List<Order>();
            foreach (var o in allDbOrders)
            {
                try
                {
                    // Chuyển DateKey (VD: "18.05") thành DateTime để so sánh
                    var dateParts = o.DateKey.Split('.');
                    int day = int.Parse(dateParts[0]);
                    int month = int.Parse(dateParts[1]);
                    int year = DateTime.Now.Year;
                    // Xử lý chuyển giao năm (VD: Đang là tháng 1, nhưng data là tháng 12 năm ngoái)
                    if (DateTime.Now.Month < 6 && month > 6) year--;
                    DateTime orderDate = new DateTime(year, month, day);
                    // Nếu ngày của Order cũ hơn 21 ngày -> Đưa vào danh sách xóa
                    if (orderDate < cutoffDate)
                    {
                        ordersToDelete.Add(o);
                    }
                }
                catch { }
            }
            if (ordersToDelete.Any())
            {
                // Xóa chi tiết MX trước
                var mxToDelete = ordersToDelete.Select(o => o.OdrNo).ToList();
                var detailsToDelete = await db.MxDetails.Where(d => mxToDelete.Contains(d.OdrNo)).ToListAsync();
                db.MxDetails.RemoveRange(detailsToDelete);
                // Xóa Orders
                db.Orders.RemoveRange(ordersToDelete);
                
                Console.WriteLine($"   🗑️ Đã xóa {ordersToDelete.Count} MX và {detailsToDelete.Count} chi tiết cũ.");
            }
            // 2. Dọn dẹp bảng Kho 2 (Chỉ xóa những xe ĐÃ XUẤT KHO quá 21 ngày)
            // (Những xe chưa xuất kho - Status "In" - dù để lâu quá 21 ngày vẫn giữ lại vì thực tế nó vẫn đang nằm trong kho)
            var kho2ToDelete = await db.Kho2_Inventory
                .Where(k => k.Status == "Out" && k.OutTime != null && k.OutTime < cutoffDate)
                .ToListAsync();
            if (kho2ToDelete.Any())
            {
                db.Kho2_Inventory.RemoveRange(kho2ToDelete);
                Console.WriteLine($"   🗑️ Đã xóa lịch sử {kho2ToDelete.Count} xe xuất khỏi Kho 2 cũ.");
            }
            await db.SaveChangesAsync();
            Console.WriteLine("✅ Dọn dẹp hoàn tất!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Lỗi khi dọn dẹp dữ liệu cũ: {ex.Message}");
        }
        // =====================================================================
        return Results.Ok(new { message = "✅ Đồng bộ Database thành công" });
        
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Sync error: {ex.Message}");
        return Results.Problem(ex.Message);
    }
});
// GET ORDERS ENDPOINT (Đọc từ Database)
app.MapGet("/orders", async (string date, string fileType, AppDbContext db) =>
{
    try
    {
        if (string.IsNullOrEmpty(date)) return Results.BadRequest("Missing date parameter");
        var query = db.Orders.Where(o => o.DateKey == date);
        
        if (!string.IsNullOrEmpty(fileType))
        {
            query = query.Where(o => o.FileType == fileType);
        }
        var list = await query.Select(o => new {
            odrno = o.OdrNo,
            fitem = o.FItem,
            mw = o.Mw,
            qty = o.Qty,
            deliveryDate = o.DeliveryDate,
            deliveryTime = o.DeliveryTime,
            status = o.Status,
            time = o.Time,
            note = o.Note
        }).ToListAsync();
        return Results.Ok(list);
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});
// UPDATE STATUS ENDPOINT (Cập nhật trực tiếp vào Database & Bắn SignalR)
app.MapPost("/update", async (UpdateRequest data, AppDbContext db, IHubContext<OrderHub> hubContext) =>
{
    try
    {
        if (data == null || string.IsNullOrEmpty(data.Odrno)) return Results.BadRequest("Invalid request");
        
        var order = await db.Orders.FirstOrDefaultAsync(o => o.OdrNo.ToUpper() == data.Odrno.ToUpper());
        
        if (order != null)
        {
            order.Status = data.Status;
            order.Note = data.Note ?? "";
            order.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
        else if (data.Status == "NOT FOUND")
        {
            db.Orders.Add(new Order {
                OdrNo = data.Odrno.ToUpper(),
                Status = "NOT FOUND",
                Note = data.Note ?? "",
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                DateKey = DateTime.Now.ToString("dd.MM"),
                FileType = "Other"
            });
        }
        await db.SaveChangesAsync();
        // 🚀 BẮN TÍN HIỆU REAL-TIME CHO TẤT CẢ CÁC TRANG WEB ĐANG MỞ
        await hubContext.Clients.All.SendAsync("OrderUpdated", new { 
            odrno = data.Odrno, 
            status = data.Status, 
            note = data.Note ?? "" 
        });
        return Results.Ok(new { message = "✅ Updated successfully" });
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});
// UPLOAD FILE ENDPOINT (NATIVE C#)
app.MapPost("/upload", async (HttpContext ctx, AppDbContext db) =>
{
    try
    {
        var form = await ctx.Request.ReadFormAsync();
        var file = form.Files.GetFile("file");
        if (file == null || file.Length == 0) return Results.BadRequest("No file uploaded");
        Console.WriteLine($"📥 Received upload: {file.FileName} ({file.Length / 1024}KB)");
        using var processStream = file.OpenReadStream();
        
        var fileName = Path.GetFileNameWithoutExtension(file.FileName);
        var match = System.Text.RegularExpressions.Regex.Match(fileName, @"(\d{2}[\.\-]\d{2})");
        string dateKey = match.Success ? match.Value.Replace("-", ".") : DateTime.Now.ToString("dd.MM");
        string fileType = fileName.Contains("Console Lid", StringComparison.OrdinalIgnoreCase) ? "Console Lid" : "Other";
        using var reader = ExcelReaderFactory.CreateReader(processStream, new ExcelReaderConfiguration() { FallbackEncoding = Encoding.GetEncoding(1252) });
        var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration() { ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = false } });
        
        if (dataSet.Tables.Count == 0) return Results.BadRequest("File Excel trống");
        var table = dataSet.Tables[0];
        int count = 0;
        // ✨ ĐÃ SỬA LỖI 1: Khai báo biến uploadedOdrNos ở đây
        var uploadedOdrNos = new List<string>(); 
        for (int i = 4; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            if (table.Columns.Count < 4) continue;
            string odrno = GetCellValue(row, 3); 
            if (string.IsNullOrWhiteSpace(odrno)) continue;
            // Đưa mã MX vào danh sách vừa upload
            uploadedOdrNos.Add(odrno);
            var existingOrder = await db.Orders.FirstOrDefaultAsync(o => o.OdrNo == odrno && o.DateKey == dateKey);
            if (existingOrder == null)
            {
                db.Orders.Add(new Order
                {
                    OdrNo = odrno,
                    FItem = GetCellValue(row, 4), 
                    Mw = GetCellValue(row, 5),    
                    Qty = GetCellValue(row, 9),   
                    DeliveryDate = GetCellValue(row, 7), 
                    DeliveryTime = GetCellValue(row, 8), 
                    FileType = fileType,
                    DateKey = dateKey,
                    Status = "Pending",
                    Time = "",
                    Note = ""
                });
                count++;
            }
        }
        // Đoạn dọn dẹp MX cũ
        var oldOrdersInDb = await db.Orders.Where(o => o.DateKey == dateKey && o.FileType == fileType).ToListAsync();
        var ordersToRemove = oldOrdersInDb.Where(o => !uploadedOdrNos.Contains(o.OdrNo)).ToList();
        if (ordersToRemove.Any())
        {
            db.Orders.RemoveRange(ordersToRemove);
            
            var mxToRemove = ordersToRemove.Select(o => o.OdrNo).ToList();
            var detailsToRemove = await db.MxDetails.Where(d => mxToRemove.Contains(d.OdrNo)).ToListAsync();
            db.MxDetails.RemoveRange(detailsToRemove);
            
            // ✨ ĐÃ SỬA LỖI 2: Thêm dấu () vào chữ Count()
            Console.WriteLine($"  🗑️ Upload Dọn dẹp: Đã xóa {ordersToRemove.Count()} MX cũ.");
        }
        await db.SaveChangesAsync();
        return Results.Ok(new { message = "✅ Upload thành công", date = dateKey, fileType = fileType, orderCount = count });
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});
// GET MX DETAIL FROM DB CACHE
app.MapGet("/mx-detail", async (string odrno, string date, AppDbContext db) =>
{
    try
    {
        Console.WriteLine($"\n🔍 Đang tìm chi tiết cho MX: {odrno}");
        
        // SỬA LỖI: So sánh không phân biệt hoa thường bằng .ToUpper()
        var details = await db.MxDetails
            .Where(m => m.OdrNo.ToUpper() == odrno.ToUpper())
            .ToListAsync();
        
        Console.WriteLine($"📊 Tìm thấy {details.Count} dòng dữ liệu trong Database");
        if (details.Count == 0)
        {
            // Kiểm tra xem trong DB có bất kỳ dữ liệu detail nào không để báo lỗi cho chuẩn
            var totalDetailsInDb = await db.MxDetails.CountAsync();
            Console.WriteLine($"⚠️ Tổng số dòng detail trong toàn bộ DB hiện tại: {totalDetailsInDb}");
            
            if (totalDetailsInDb == 0)
            {
                return Results.NotFound($"Database chưa có dữ liệu chi tiết. Vui lòng bấm nút 'Cập Nhật Master File' để đồng bộ.");
            }
            return Results.NotFound($"Không tìm thấy chi tiết cho MX {odrno} trong Database.");
        }
        var itemsList = details.GroupBy(d => d.ItemCode)
                               .Select(g => new MxItemData { ItemCode = g.Key, Quantity = g.First().ItemQty })
                               .ToList();
        var partsList = details.GroupBy(d => new { d.PartName, d.PartOrder })
                               .Select(g => new PartDetailData { 
                                   PartName = g.Key.PartName, 
                                   Order = g.Key.PartOrder, 
                                   Quantity = g.Sum(x => x.PartQty) 
                               })
                               .OrderBy(p => p.Order)
                               .ToList();
        Console.WriteLine($"✅ Đã đóng gói xong: {itemsList.Count} Items và {partsList.Count} Parts");
        return Results.Ok(new { odrno = odrno, items = itemsList, parts = partsList });
    }
    catch (Exception ex) 
    { 
        Console.WriteLine($"❌ Lỗi API mx-detail: {ex.Message}");
        return Results.Problem(ex.Message); 
    }
});
// ==================== EXPORT REPORT ENDPOINT (XUẤT XLSB BẰNG LATE BINDING) ====================
app.MapPost("/export", async (ExportRequest request) =>
{
    try
    {
        if (request == null || request.Orders == null || request.Orders.Count == 0)
            return Results.BadRequest("No data to export");
        Console.WriteLine($"\n📊 Bắt đầu xuất báo cáo XLSB cho ngày: {request.Date}");
        var receivedOdrnos = request.Orders
            .Where(o => o.ContainsKey("status") && o["status"]?.ToString() == "Received")
            .Select(o => o["odrno"]?.ToString()?.ToUpper())
            .Where(o => !string.IsNullOrEmpty(o))
            .ToHashSet();
        // 1. Tìm file danh sách gốc trong V Drive
        string dateKey = request.Date; 
        string dateKeyDash = dateKey.Replace(".", "-"); 
        string fileType = request.FileType ?? "";
        var allFilesInDrive = Directory.GetFiles(vDrivePath)
            .Where(f => f.EndsWith(".xlsb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).StartsWith("~"))
            .ToList();
        var matchedFiles = allFilesInDrive
            .Where(f => Path.GetFileName(f).Contains(dateKey) || Path.GetFileName(f).Contains(dateKeyDash))
            .Select(f => new FileInfo(f))
            .ToList();
        FileInfo? originalFile = null;
        if (matchedFiles.Count > 0)
        {
            if (fileType.Equals("Console Lid", StringComparison.OrdinalIgnoreCase))
                originalFile = matchedFiles.Where(f => f.Name.Contains("Console Lid", StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            else
                originalFile = matchedFiles.Where(f => !f.Name.Contains("Console Lid", StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            if (originalFile == null) originalFile = matchedFiles.OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
        }
        if (originalFile == null) 
            return Results.BadRequest($"Không tìm thấy file danh sách gốc của ngày {request.Date} trong V Drive.");
        Console.WriteLine($"  📄 Đã chọn file gốc: {originalFile.Name}");
        string tempDir = Path.Combine(Path.GetTempPath(), "MES_Exports");
        Directory.CreateDirectory(tempDir);
        string outputPath = Path.Combine(tempDir, $"BaoCao_GiaoNhan_{request.Date}_{DateTime.Now:HHmmss}.xlsb");
        // 2. TUYỆT CHIÊU LATE BINDING: Gọi Excel ngầm mà không cần thư viện Interop
        Type? excelType = Type.GetTypeFromProgID("Excel.Application");
        if (excelType == null)
        {
            return Results.BadRequest("Máy chủ không cài đặt Microsoft Excel. Không thể xuất file .xlsb!");
        }
        Console.WriteLine("  ⚙️ Đang mở Excel ngầm để tạo file .xlsb...");
        dynamic excelApp = Activator.CreateInstance(excelType)!;
        
        try
        {
            excelApp.Visible = false;
            excelApp.DisplayAlerts = false;
            excelApp.AskToUpdateLinks = false;
            dynamic workbooks = excelApp.Workbooks;
            // Mở file gốc (0 = Không update link, true = ReadOnly)
            dynamic wb = workbooks.Open(originalFile.FullName, 0, true); 
            dynamic ws = wb.Sheets[1];
            dynamic cells = ws.Cells;
            // Tìm dòng cuối cùng ở cột D (Cột 4). -4162 là mã của xlUp trong Excel
            dynamic lastCell = cells[ws.Rows.Count, 4];
            int lastRow = lastCell.End(-4162).Row;
            Console.WriteLine($"  ✍️ Đang điền chữ OK từ dòng 5 đến {lastRow}...");
            for (int r = 5; r <= lastRow; r++)
            {
                dynamic mxCell = cells[r, 4];
                var cellValue = mxCell.Value;
                
                if (cellValue != null)
                {
                    string mxCode = cellValue.ToString().Trim().ToUpper();
                    if (receivedOdrnos.Contains(mxCode))
                    {
                        dynamic cellK = cells[r, 11]; // Cột K
                        cellK.Value = "OK";
                        cellK.Font.Bold = true;
                        cellK.Font.Color = 32768; // Mã màu xanh lá cây (Green) trong Excel
                    }
                }
            }
            Console.WriteLine("  💾 Đang lưu file .xlsb...");
            // Lưu file với định dạng .xlsb (Mã format 50 = xlExcel12)
            wb.SaveAs(outputPath, 50); 
            wb.Close(false);
            excelApp.Quit();
        }
        catch (Exception ex)
        {
            excelApp.Quit();
            throw new Exception("Lỗi khi điều khiển Excel: " + ex.Message);
        }
        // 3. Trả file .xlsb về cho trình duyệt
        if (File.Exists(outputPath))
        {
            var fileBytes = File.ReadAllBytes(outputPath);
            File.Delete(outputPath); 
            
            Console.WriteLine($"  ✅ Xuất báo cáo XLSB thành công!");
            return Results.File(
                fileBytes,
                "application/vnd.ms-excel.sheet.binary.macroEnabled.12",
                Path.GetFileName(outputPath)
            );
        }
        
        return Results.Problem("Lỗi không xác định khi lưu file.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Export error: {ex.Message}");
        return Results.Problem(ex.Message);
    }
});
// ==================== HELPER: TÌM THƯ MỤC INHOUSE THÔNG MINH ====================
string? FindInhouseFolder(DateTime targetDate, string rootPath)
{
    Console.WriteLine($"\n  🔍 Đang tìm folder INHOUSE cho ngày: {targetDate:dd/MM/yyyy}");
    if (!Directory.Exists(rootPath))
    {
        Console.WriteLine($"  ❌ Không tìm thấy thư mục gốc: {rootPath}");
        return null;
    }
    // 1. Lấy danh sách các folder MSS (VD: MSS0513, MSS0520, MSS0527...)
    var mssDirs = Directory.GetDirectories(rootPath, "MSS*");
    string? selectedMssDir = null;
    DateTime? closestDate = null;
    foreach (var dir in mssDirs)
    {
        string folderName = new DirectoryInfo(dir).Name; // "MSS0527"
        if (folderName.Length >= 7)
        {
            string monthStr = folderName.Substring(3, 2);
            string dayStr = folderName.Substring(5, 2);
            if (int.TryParse(monthStr, out int m) && int.TryParse(dayStr, out int d))
            {
                try
                {
                    DateTime folderDate = new DateTime(targetDate.Year, m, d);
                    
                    // Tìm folder có ngày LỚN HƠN HOẶC BẰNG ngày cần tìm, và gần nhất
                    // VD: Tìm ngày 22/05 -> Sẽ chọn MSS0527 (27/05)
                    if (folderDate >= targetDate.Date)
                    {
                        if (closestDate == null || folderDate < closestDate)
                        {
                            closestDate = folderDate;
                            selectedMssDir = dir;
                        }
                    }
                }
                catch { }
            }
        }
    }
    if (selectedMssDir == null)
    {
        Console.WriteLine($"  ❌ Không tìm thấy folder MSS nào bao phủ ngày {targetDate:dd/MM/yyyy}");
        return null;
    }
    Console.WriteLine($"  ✅ Đã chọn folder tuần: {new DirectoryInfo(selectedMssDir).Name}");
    // 2. Tìm folder INHOUSE bên trong
    string path1 = Path.Combine(selectedMssDir, @"Sub Schedule\kit Schedule\WIP\2.KIT STACK OUT\INHOUSE");
    string path2 = Path.Combine(selectedMssDir, @"kit Schedule\WIP\2.KIT STACK OUT\INHOUSE");
    if (Directory.Exists(path1))
    {
        Console.WriteLine($"  ✅ Tìm thấy INHOUSE tại cấu trúc 1.");
        return path1;
    }
    else if (Directory.Exists(path2))
    {
        Console.WriteLine($"  ✅ Tìm thấy INHOUSE tại cấu trúc 2.");
        return path2;
    }
    else
    {
        Console.WriteLine($"  ⚠️ Không thấy cấu trúc chuẩn, đang quét sâu toàn bộ folder {new DirectoryInfo(selectedMssDir).Name}...");
        try
        {
            // Quét dự phòng tất cả thư mục con nếu ai đó đổi tên folder
            var fallbackDirs = Directory.GetDirectories(selectedMssDir, "INHOUSE", SearchOption.AllDirectories);
            if (fallbackDirs.Length > 0)
            {
                Console.WriteLine($"  ✅ Đã tìm thấy INHOUSE bằng cách quét sâu.");
                return fallbackDirs[0];
            }
        }
        catch { }
    }
    Console.WriteLine("  ❌ Không tìm thấy thư mục INHOUSE nào trong tuần này!");
    return null;
}
// ==================== FUNCTION: PARSE MX DETAILS NATIVELY (NO PYTHON) ====================
async Task<List<MxDetail>> ParseMxDetailsFromXlsb(string xlsbFile, List<string> odrnos)
{
    var result = new List<MxDetail>();
    
    string CleanStr(string? input) {
        if (string.IsNullOrWhiteSpace(input)) return "";
        return input.Replace("*", "").Replace("\u00A0", "").Replace("\u200B", "").Trim().ToUpper();
    }
    try
    {
        Console.WriteLine($"  ⚡ Parsing MX details NATIVELY from: {Path.GetFileName(xlsbFile)}");
        
        // ĐỌC TRỰC TIẾP FILE XLSB BẰNG C# (KHÔNG CẦN PYTHON)
        using var stream = File.Open(xlsbFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration() { FallbackEncoding = Encoding.GetEncoding(1252) });
        
        var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration() { 
            ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = false } 
        });
        if (!dataSet.Tables.Contains("Print") || !dataSet.Tables.Contains("4") || 
            !dataSet.Tables.Contains("13") || !dataSet.Tables.Contains("17")) 
        {
            Console.WriteLine("  ❌ Missing required sheets (Print, 4, 13, or 17)");
            return result;
        }
        var sheetPrint = dataSet.Tables["Print"];
        var sheet4 = dataSet.Tables["4"];
        var sheet13 = dataSet.Tables["13"];
        var sheet17 = dataSet.Tables["17"];
        var partMapping = GetPartMapping();
        // Hàm lấy dữ liệu an toàn (Index bắt đầu từ 0)
        string GetCell(DataTable dt, int rowIdx, int colIdx) {
            if (rowIdx >= dt.Rows.Count || colIdx >= dt.Columns.Count) return "";
            return dt.Rows[rowIdx][colIdx]?.ToString() ?? "";
        }
        // 1. Đọc Sheet Print (Dòng 6 -> index 5, Cột S(19) -> index 18)
        var mxInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int r = 5; r < sheetPrint.Rows.Count; r++)
        {
            var mx = CleanStr(GetCell(sheetPrint, r, 18));
            if (!string.IsNullOrEmpty(mx)) mxInFile.Add(mx);
        }
        var mxToProcess = odrnos.Where(mx => mxInFile.Contains(CleanStr(mx))).Select(m => CleanStr(m)).ToList();
        // 2. Đọc Sheet 17 (Dòng 2 -> index 1, Cột B(2) -> index 1, Cột AY(51) -> index 50)
        var fallbackItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int r = 1; r < sheet17.Rows.Count; r++)
        {
            string mx = CleanStr(GetCell(sheet17, r, 1));
            string fallbackItem = CleanStr(GetCell(sheet17, r, 50));
            if (!string.IsNullOrEmpty(mx) && !string.IsNullOrEmpty(fallbackItem)) fallbackItems[mx] = fallbackItem;
        }
        // 3. Đọc Sheet 13 (Dòng 1 -> index 0, Cột A(1) -> index 0)
        var sheet13Rows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // Lưu lại index dòng
        for (int r = 0; r < sheet13.Rows.Count; r++)
        {
            string item = CleanStr(GetCell(sheet13, r, 0));
            if (!string.IsNullOrEmpty(item) && !sheet13Rows.ContainsKey(item)) sheet13Rows[item] = r;
        }
        // 4. Đọc Sheet 4 (Dòng 2 -> index 1, Cột A(1)->0, K(11)->10, R(18)->17)
        var sheet4Items = new Dictionary<string, List<(string ItemCode, int Qty)>>(StringComparer.OrdinalIgnoreCase);
        string lastValidItemCode = "";
        int lastValidQty = 0;
        for (int r = 1; r < sheet4.Rows.Count; r++)
        {
            string rawMx = CleanStr(GetCell(sheet4, r, 0));
            if (string.IsNullOrWhiteSpace(rawMx)) continue;
            var mxList = rawMx.Split(new[] { '/', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(m => CleanStr(m)).Where(m => !string.IsNullOrEmpty(m));
            string currentItemCode = CleanStr(GetCell(sheet4, r, 10));
            string qtyStr = CleanStr(GetCell(sheet4, r, 17));
            
            int currentQty = 0;
            if (int.TryParse(qtyStr, out int q)) currentQty = q;
            else if (double.TryParse(qtyStr, out double qd)) currentQty = (int)qd;
            if (string.IsNullOrEmpty(currentItemCode)) currentItemCode = lastValidItemCode;
            else lastValidItemCode = currentItemCode;
            if (currentQty <= 0) currentQty = lastValidQty;
            else lastValidQty = currentQty;
            if (!string.IsNullOrEmpty(currentItemCode) && currentQty > 0) {
                foreach (var mx in mxList) {
                    if (!sheet4Items.ContainsKey(mx)) sheet4Items[mx] = new List<(string, int)>();
                    sheet4Items[mx].Add((currentItemCode, currentQty));
                }
            }
        }
        // XỬ LÝ TỪNG MX
        foreach (var odrno in mxToProcess)
        {
            if (!sheet4Items.TryGetValue(odrno, out var items)) continue;
            
            foreach (var (originalItemCode, itemQty) in items)
            {
                int itemRowIdx = -1;
                string finalItemCode = originalItemCode;
                if (sheet13Rows.TryGetValue(originalItemCode, out int row13)) {
                    itemRowIdx = row13;
                } else {
                    if (fallbackItems.TryGetValue(odrno, out var fallbackItemCode)) {
                        finalItemCode = fallbackItemCode;
                        if (sheet13Rows.TryGetValue(fallbackItemCode, out int fallbackRow13)) itemRowIdx = fallbackRow13;
                    }
                }
                
                if (itemRowIdx == -1) continue;
                
                foreach (var part in partMapping)
                {
                    int partQtyPerItem = 0;
                    try {
                        // Cột trong partMapping là index 1-based (Excel), nên trừ 1 để ra 0-based
                        var cellVal = CleanStr(GetCell(sheet13, itemRowIdx, part.ColumnIndex - 1));
                        if (int.TryParse(cellVal, out int p)) partQtyPerItem = p;
                        else if (double.TryParse(cellVal, out double pd)) partQtyPerItem = (int)pd;
                    } catch { }
                    
                    if (partQtyPerItem > 0)
                    {
                        result.Add(new MxDetail {
                            OdrNo = odrno, ItemCode = finalItemCode, ItemQty = itemQty,
                            PartName = part.PartName, PartQty = partQtyPerItem * itemQty,
                            PartOrder = part.Order, LastUpdate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        });
                    }
                }
            }
        }
        Console.WriteLine($"  ✅ Đã xử lý xong {result.Count} dòng chi tiết Part (Native C# Mode).");
    }
    catch (Exception ex) { Console.WriteLine($"❌ Error ParseMxDetailsFromXlsb: {ex.Message}"); }
    return result;
}
List<PartMappingData> GetPartMapping()
{
    return new List<PartMappingData> {
        new PartMappingData { Order = 1, PartName = "Inside Arm", ColumnIndex = 2 },
        new PartMappingData { Order = 2, PartName = "Outside Arm", ColumnIndex = 3 },
        new PartMappingData { Order = 3, PartName = "Front Arm panel", ColumnIndex = 4 },
        new PartMappingData { Order = 4, PartName = "Wing Panel", ColumnIndex = 5 },
        new PartMappingData { Order = 5, PartName = "Arm Fiber", ColumnIndex = 6 },
        new PartMappingData { Order = 6, PartName = "Wing Fiber", ColumnIndex = 7 },
        new PartMappingData { Order = 7, PartName = "Wing Cover", ColumnIndex = 8 },
        new PartMappingData { Order = 8, PartName = "Outside Arm Flap", ColumnIndex = 9 },
        new PartMappingData { Order = 9, PartName = "Back Fiber", ColumnIndex = 10 },
        new PartMappingData { Order = 10, PartName = "Inside Back", ColumnIndex = 11 },
        new PartMappingData { Order = 11, PartName = "Outside Back", ColumnIndex = 12 },
        new PartMappingData { Order = 12, PartName = "Seat", ColumnIndex = 14 },
        new PartMappingData { Order = 13, PartName = "Seat band", ColumnIndex = 15 },
        new PartMappingData { Order = 14, PartName = "Ottoman Cover", ColumnIndex = 16 },
        new PartMappingData { Order = 15, PartName = "Small Ottoman 1", ColumnIndex = 17 },
        new PartMappingData { Order = 16, PartName = "Small Ottoman 2", ColumnIndex = 17 },
        new PartMappingData { Order = 17, PartName = "Arm Console Cover", ColumnIndex = 18 },
        new PartMappingData { Order = 18, PartName = "No Sew (Fiber Arm)", ColumnIndex = 19 },
        new PartMappingData { Order = 19, PartName = "No Sew (Fiber Back)", ColumnIndex = 20 },
        new PartMappingData { Order = 20, PartName = "No Sew (Fiber Seat)", ColumnIndex = 21 },
        new PartMappingData { Order = 21, PartName = "No Sew (Fiber Seatband)", ColumnIndex = 44 },
        new PartMappingData { Order = 22, PartName = "Fibercusshion", ColumnIndex = 45 },
        new PartMappingData { Order = 23, PartName = "No Sew (Fibercusshion)", ColumnIndex = 46 },
        new PartMappingData { Order = 24, PartName = "Black Bottom", ColumnIndex = 22 },
        new PartMappingData { Order = 25, PartName = "Elite White UN Kit", ColumnIndex = 23 },
        new PartMappingData { Order = 26, PartName = "Elite UPH", ColumnIndex = 24 },
        new PartMappingData { Order = 27, PartName = "OE Cover", ColumnIndex = 25 },
        new PartMappingData { Order = 28, PartName = "Handle (Strap)", ColumnIndex = 26 },
        new PartMappingData { Order = 29, PartName = "Typar UN Kit", ColumnIndex = 27 },
        new PartMappingData { Order = 30, PartName = "Typar UPH & COOL CLOTH", ColumnIndex = 28 },
        new PartMappingData { Order = 31, PartName = "Seat Hair", ColumnIndex = 31 },
        new PartMappingData { Order = 32, PartName = "Bottom Flap", ColumnIndex = 42 },
        new PartMappingData { Order = 33, PartName = "Seat Fiber", ColumnIndex = 43 },
        new PartMappingData { Order = 34, PartName = "Pillow", ColumnIndex = 13 },
        new PartMappingData { Order = 35, PartName = "Pillow Sack", ColumnIndex = 37 },
        new PartMappingData { Order = 36, PartName = "Back (Fill)", ColumnIndex = 32 },
        new PartMappingData { Order = 37, PartName = "Back Console", ColumnIndex = 33 },
        new PartMappingData { Order = 38, PartName = "Blacksack", ColumnIndex = 35 },
        new PartMappingData { Order = 39, PartName = "Left/Right Arm", ColumnIndex = 39 },
        new PartMappingData { Order = 40, PartName = "Arm console", ColumnIndex = 40 },
        new PartMappingData { Order = 41, PartName = "Arm sack", ColumnIndex = 34 },
        new PartMappingData { Order = 42, PartName = "Cushion", ColumnIndex = 41 },
        new PartMappingData { Order = 43, PartName = "Cushion Sack", ColumnIndex = 38 },
        new PartMappingData { Order = 44, PartName = "Typar B2", ColumnIndex = 28 },
    };
}
// ==================== HELPER: PHÂN LOẠI XE (VEHICLE MAPPING) ====================
// Bạn hãy chỉnh sửa logic hàm này cho đúng với quy định của xưởng nhé!
string AssignVehicle(string partName)
{
    var p = partName.ToLower();
    
    // XE 1: Arm, Back, Panel, Flap...
    if (p.Contains("arm") || p.Contains("back") || p.Contains("panel") || p.Contains("flap")) 
        return "Xe 1";
        
    // XE 2: Pillow, Cushion, Seat, Ottoman...
    if (p.Contains("pillow") || p.Contains("cushion") || p.Contains("seat") || p.Contains("ottoman")) 
        return "Xe 2";
        
    // XE 3: Fiber, Cotton, Sack...
    if (p.Contains("fiber") || p.Contains("sack") || p.Contains("hair")) 
        return "Xe 3";
        
    // Mặc định nếu không thuộc 3 loại trên
    return "Xe 1"; 
}
// ==================== DASHBOARD API ENDPOINT ====================
app.MapGet("/api/dashboard", async (string date, AppDbContext db) =>
{
    try
    {
        if (string.IsNullOrEmpty(date)) return Results.BadRequest("Missing date");
        // Lấy tất cả Orders của ngày đó
        var orders = await db.Orders.Where(o => o.DateKey == date).ToListAsync();
        
        // Lấy tất cả Details của ngày đó
        var odrnos = orders.Select(o => o.OdrNo).ToList();
        var allDetails = await db.MxDetails.Where(d => odrnos.Contains(d.OdrNo)).ToListAsync();
        var dashboardData = new List<object>();
        foreach (var order in orders)
        {
            var mxDetails = allDetails.Where(d => d.OdrNo == order.OdrNo).ToList();
            
            // Nhóm các Part theo Xe
            var xe1Parts = new List<string>();
            var xe2Parts = new List<string>();
            var xe3Parts = new List<string>();
            foreach (var detail in mxDetails)
            {
                string vehicle = AssignVehicle(detail.PartName);
                if (vehicle == "Xe 1" && !xe1Parts.Contains(detail.PartName)) xe1Parts.Add(detail.PartName);
                else if (vehicle == "Xe 2" && !xe2Parts.Contains(detail.PartName)) xe2Parts.Add(detail.PartName);
                else if (vehicle == "Xe 3" && !xe3Parts.Contains(detail.PartName)) xe3Parts.Add(detail.PartName);
            }
            // Phân tích trạng thái từng xe dựa vào Ghi chú (Note) của Order
            string xe1Status = order.Status == "Pending" ? "Pending" : "Received";
            string xe2Status = order.Status == "Pending" ? "Pending" : "Received";
            string xe3Status = order.Status == "Pending" ? "Pending" : "Received";
            
            string xe1Note = ""; string xe2Note = ""; string xe3Note = "";
            if (order.Status == "Lack" && !string.IsNullOrEmpty(order.Note))
            {
                // Note có dạng: "Seat (Thiếu 5) | Fiber (Thiếu 2)"
                var lackItems = order.Note.Split('|', StringSplitOptions.RemoveEmptyEntries);
                foreach (var lack in lackItems)
                {
                    var cleanLack = lack.Trim();
                    var partNameOnly = cleanLack.Split('(')[0].Trim(); // Lấy tên part
                    
                    string v = AssignVehicle(partNameOnly);
                    if (v == "Xe 1") { xe1Status = "Lack"; xe1Note += cleanLack + " "; }
                    if (v == "Xe 2") { xe2Status = "Lack"; xe2Note += cleanLack + " "; }
                    if (v == "Xe 3") { xe3Status = "Lack"; xe3Note += cleanLack + " "; }
                }
            }
            // Nếu xe không có hàng, set status là N/A
            if (xe1Parts.Count == 0) xe1Status = "N/A";
            if (xe2Parts.Count == 0) xe2Status = "N/A";
            if (xe3Parts.Count == 0) xe3Status = "N/A";
            dashboardData.Add(new {
                odrno = order.OdrNo,
                fitem = order.FItem,
                mw = order.Mw,
                deliveryDate = order.DeliveryDate,
                timeWindow = order.DeliveryTime,
                status = order.Status,
                updateTime = order.Time,
                
                note = order.Note,
                xe1 = new { parts = string.Join(", ", xe1Parts), status = xe1Status, note = xe1Note },
                xe2 = new { parts = string.Join(", ", xe2Parts), status = xe2Status, note = xe2Note },
                xe3 = new { parts = string.Join(", ", xe3Parts), status = xe3Status, note = xe3Note }
            });
        }
        return Results.Ok(dashboardData);
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});
// ==================== KHO 2 API ENDPOINTS ====================
app.MapPost("/api/kits-inv/scan", async (Kho2ScanRequest req, AppDbContext db) =>
{
    try {
        if (string.IsNullOrEmpty(req.Odrno) || string.IsNullOrEmpty(req.ZoneCode)) return Results.BadRequest("Thiếu mã MX hoặc mã Ô");
        string mx = req.Odrno.ToUpper().Trim();
        string zone = req.ZoneCode.ToUpper().Trim();
        var existing = await db.Kho2_Inventory.FirstOrDefaultAsync(x => x.OdrNo == mx && x.Status == "In");
        if (existing != null) {
            string oldZone = existing.ZoneCode;
            existing.ZoneCode = zone;
            existing.UpdateTime = DateTime.Now;
            await db.SaveChangesAsync();
            return Results.Ok(new { message = $"🔄 Đã dời {mx} từ ô {oldZone} sang ô {zone}" });
        } else {
            db.Kho2_Inventory.Add(new Kho2_Inventory { OdrNo = mx, ZoneCode = zone, InTime = DateTime.Now, UpdateTime = DateTime.Now, Status = "In" });
            await db.SaveChangesAsync();
            return Results.Ok(new { message = $"✅ Đã cất {mx} vào ô {zone}" });
        }
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});
app.MapGet("/api/kits-inv/find", async (string odrno, AppDbContext db) =>
{
    string mx = odrno.ToUpper().Trim();
    var item = await db.Kho2_Inventory.FirstOrDefaultAsync(x => x.OdrNo == mx && x.Status == "In");
    if (item == null) return Results.NotFound($"❌ MX {mx} không có trong Kho 2");
    return Results.Ok(item);
});
app.MapPost("/api/kits-inv/out", async (Kho2ScanRequest req, AppDbContext db) =>
{
    string mx = req.Odrno.ToUpper().Trim();
    var item = await db.Kho2_Inventory.FirstOrDefaultAsync(x => x.OdrNo == mx && x.Status == "In");
    if (item == null) return Results.BadRequest("Không tìm thấy hàng trong kho");
    item.Status = "Out"; item.OutTime = DateTime.Now;
    await db.SaveChangesAsync();
    return Results.Ok(new { message = $"📤 Đã xuất kho thành công {mx}" });
});
app.MapGet("/api/kits-inv/inventory", async (AppDbContext db) =>
{
    var list = await db.Kho2_Inventory.Where(x => x.Status == "In").OrderByDescending(x => x.UpdateTime).ToListAsync();
    return Results.Ok(list);
});
// ===== STATIC FILES & ROUTING =====
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<OrderHub>("/orderHub");

// ==================== TRACKING API ====================
app.MapGet("/api/tracking/journey", async (string date, AppDbContext db) =>
{
    try
    {
        // 1. Tìm đúng file Excel theo ngày
        DateTime targetDate;
        if (!DateTime.TryParse(date, out targetDate)) 
            return Results.BadRequest("Invalid date format. Use yyyy-MM-dd.");

        string fileName = $"UPH Support Schedule {targetDate:ddMMyyyy}.xlsx";
        string filePath = Path.Combine(schedulePath, fileName);

        if (!File.Exists(filePath))
            return Results.NotFound($"Không tìm thấy file kế hoạch: {fileName}");

        Console.WriteLine($"🔍 Đang đọc file tracking: {fileName}");

        // 2. Đọc file và xử lý
        var dataByMx = new Dictionary<string, List<WorkCenterStep>>();

        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration() { 
            ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = false } 
        });

        // 3. Lặp qua tất cả các Sheet (Work Centers)
        foreach (DataTable table in dataSet.Tables)
        {
            string workCenterName = table.TableName;
            
            // Bỏ qua các sheet không phải là Work Center
            if (workCenterName.ToLower().Contains("pivot") || workCenterName.ToLower().Contains("summary")) continue;

            // Đọc từ hàng thứ 2 (bỏ qua tiêu đề)
            for (int i = 1; i < table.Rows.Count; i++)
            {
                var row = table.Rows[i];
                
                // Cột B(1), F(5), I(8), M(12) - index bắt đầu từ 0
                string mx = GetCellValue(row, 1); 
                if (string.IsNullOrWhiteSpace(mx)) continue;

                string mo = GetCellValue(row, 5);
                string qty = GetCellValue(row, 8);
                string leadtime = GetCellValue(row, 12);
                
                // Gộp dữ liệu theo từng mã MX
                if (!dataByMx.ContainsKey(mx))
                    dataByMx[mx] = new List<WorkCenterStep>();
                
                dataByMx[mx].Add(new WorkCenterStep(workCenterName, mo, qty, leadtime));
            }
        }
        
        // 4. Chuyển đổi sang định dạng để trả về
        var result = dataByMx.Select(kvp => new TrackingData(kvp.Key, kvp.Value))
                             .OrderBy(t => t.Mx) // Sắp xếp theo tên MX
                             .ToList();

        Console.WriteLine($"✅ Đã xử lý xong {result.Count} mã MX.");
        // 🚀 TẠO/CẬP NHẬT MoProgress TỪ DỮ LIỆU KẾ HOẠCH
        try
        {
            var allSteps = result.SelectMany(r => r.Steps.Select(s => new { Mx = r.Mx, Step = s })).ToList();
            
            // Lấy tất cả MoProgress hiện có để so sánh
            var existingProgress = await db.MoProgresses.ToListAsync();

            foreach (var item in allSteps)
            {
                var existing = existingProgress.FirstOrDefault(p => p.MO == item.Step.Mo && p.WorkCenter == item.Step.WorkCenter);
                if (existing == null)
                {
                    // Nếu chưa có, tạo mới
                    db.MoProgresses.Add(new MoProgress
                    {
                        MO = item.Step.Mo,
                        MX = item.Mx,
                        WorkCenter = item.Step.WorkCenter,
                        PlannedQty = int.TryParse(item.Step.Qty, out int q) ? q : 0,
                        ActualQty = 0,
                        Status = "Pending",
                        LeadtimeString = item.Step.Leadtime
                    });
                }
                else
                {
                    // Nếu có, cập nhật kế hoạch (nếu thay đổi)
                    existing.MX = item.Mx;
                    existing.PlannedQty = int.TryParse(item.Step.Qty, out int q) ? q : 0;
                }
            }
            await db.SaveChangesAsync();
            Console.WriteLine($"✅ Đã cập nhật {allSteps.Count} MO vào bảng MoProgress.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Lỗi khi cập nhật MoProgress: {ex.Message}");
        }

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Lỗi đọc file Tracking: {ex.Message}");
        return Results.Problem(ex.Message);
    }
});

// ==================== API DEBUG: LẤY DANH SÁCH WORK CENTER ĐÃ ĐỌC ====================
app.MapGet("/api/debug/workcenters", (string date) =>
{
    try
    {
        // 1. Tìm đúng file Excel theo ngày
        DateTime targetDate;
        if (!DateTime.TryParse(date, out targetDate)) 
            return Results.BadRequest("Invalid date format. Use yyyy-MM-dd.");

        string fileName = $"UPH Support Schedule {targetDate:ddMMyyyy}.xlsx";
        string filePath = Path.Combine(schedulePath, fileName);

        if (!File.Exists(filePath))
            return Results.NotFound($"Không tìm thấy file kế hoạch: {fileName}");

        // 2. Đọc file và chỉ lấy tên Sheet
        var workCenterNames = new List<string>();

        using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = ExcelReaderFactory.CreateReader(stream))
        {
            var dataSet = reader.AsDataSet();
            
            foreach (DataTable table in dataSet.Tables)
            {
                string workCenterName = table.TableName;
                
                // Bỏ qua các sheet không phải là Work Center (giống logic cũ)
                if (workCenterName.ToLower().Contains("pivot") || workCenterName.ToLower().Contains("summary"))
                {
                    continue;
                }
                
                workCenterNames.Add(workCenterName);
            }
        }
        
        // 3. Sắp xếp và trả về
        workCenterNames.Sort();
        return Results.Ok(new {
            file = fileName,
            count = workCenterNames.Count,
            workCenters = workCenterNames
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// ==================== API LẤY TIẾN ĐỘ THẬT TỪ DB SQLITE ====================
app.MapGet("/api/tracking/kit-progress", async (string date, AppDbContext db) =>
{
    try
    {
        // TODO: sau này cần lọc theo ngày
        var progressData = await db.MoProgresses
            .Select(p => new
            {
                mo = p.MO,
                mx = p.MX,
                workCenter = p.WorkCenter,
                plannedQty = p.PlannedQty,
                currentQty = p.ActualQty,
                leadtime = "", // TODO: Lấy leadtime từ file kế hoạch
                status = p.Status,
                progress = $"{p.ActualQty}/{p.PlannedQty}"
            })
            .ToListAsync();

        return Results.Ok(progressData);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// ==================== API LẤY CHI TIẾT QUÉT CỦA 1 MO TỪ DB SQLITE ====================
app.MapGet("/api/tracking/mo-scan-detail", async (string mo, AppDbContext db) =>
{
    try
    {
        if (string.IsNullOrEmpty(mo)) return Results.BadRequest("Missing MO");

        var scans = await db.ScanLogs
            .Where(s => s.MO == mo)
            .OrderBy(s => s.ScanTime)
            .ToListAsync();

        // TÍNH TỔNG SỐ LƯỢT ĐÃ QUÉT
        int totalScannedQty = scans.Sum(s => s.Qty);

        return Results.Ok(new { 
            mo = mo, 
            scans = scans,
            totalScannedQty = totalScannedQty // ← TRẢ VỀ THÊM TỔNG SỐ LƯỢNG
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// ==================== API TEST KẾT NỐI AS400 ====================
app.MapGet("/api/test-as400", async () =>
{
    try
    {
        // Sử dụng connection string giống như trong Macro và Service
        using var conn = new OdbcConnection("DSN=WFVNPROD;UID=WNKRND;PWD=wnkrnd@112;");
        await conn.OpenAsync();

        // Câu SQL đơn giản để lấy 10 dòng đầu tiên từ bảng scan
        string sql = "SELECT TRIM(ODORDR) AS ODORDR, TRIM(ODPN) AS ODPN, ODQTYC, TRIM(ODWKCN) AS ODWKCN, CHAR(ODTSTP) AS ODTSTP " +
                     "FROM WWDCF.GRPORDH FETCH FIRST 10 ROWS ONLY";

        using var cmd = new OdbcCommand(sql, conn);
        using var reader = await cmd.ExecuteReaderAsync();

        var list = new List<object>();
        while (await reader.ReadAsync())
        {
            list.Add(new
            {
                mo   = reader.GetString(0),
                item = reader.IsDBNull(1) ? "" : reader.GetString(1),
                qty  = reader.IsDBNull(2) ? 0 : (int)reader.GetDecimal(2), // Dùng GetDecimal cho an toàn
                wc   = reader.IsDBNull(3) ? "" : reader.GetString(3),
                ts   = reader.IsDBNull(4) ? "" : reader.GetString(4)
            });
        }

        return Results.Ok(list);
    }
    catch (Exception ex)
    {
        // Nếu có lỗi, trả về chi tiết lỗi để dễ dàng debug
        return Results.Problem(new ProblemDetails
        {
            Title = "Lỗi kết nối AS/400",
            Detail = ex.ToString(), // Trả về đầy đủ stack trace
            Status = 500
        });
    }
});

// ==================== WEB ROUTES ====================
// 1. Trang chủ (Bản đồ 2D)
app.MapGet("/", async ctx => {
    ctx.Response.ContentType = "text/html";
    await ctx.Response.SendFileAsync("wwwroot/index.html"); // Lát nữa ta sẽ tạo file index.html mới
});
// 2. Trang WIP WNK3 (Đã đổi tên từ index cũ)
app.MapGet("/wip-wnk3", async ctx => {
    ctx.Response.ContentType = "text/html";
    await ctx.Response.SendFileAsync("wwwroot/wip-wnk3.html");
});
// 3. Trang Kho 2
app.MapGet("/kits-inv", async ctx => {
    ctx.Response.ContentType = "text/html";
    await ctx.Response.SendFileAsync("wwwroot/kits-inv.html");
});
// 4. Trang Tracking Hành trình
app.MapGet("/tracking", async ctx => {
    ctx.Response.ContentType = "text/html";
    await ctx.Response.SendFileAsync("wwwroot/tracking.html"); // Lát nữa ta sẽ tạo file tracking.html
});
// 5. Trang Dashboard
app.MapGet("/dashboard", async ctx => {
    ctx.Response.ContentType = "text/html";
    await ctx.Response.SendFileAsync("wwwroot/dashboard.html");
});
// Các trang trống (Placeholder cho tương lai)
app.MapGet("/kho1", async ctx => { await ctx.Response.WriteAsync("<h1>KHO 1 - Dang xay dung...</h1><a href='/'>Quay lai</a>"); });
app.MapGet("/kho3", async ctx => { await ctx.Response.WriteAsync("<h1>KHO 3 - Dang xay dung...</h1><a href='/'>Quay lai</a>"); });
app.MapGet("/assemble", async ctx => { await ctx.Response.WriteAsync("<h1>ASSEMBLE - Dang xay dung...</h1><a href='/'>Quay lai</a>"); });

app.MapGet("/cnc-go", async ctx => { await ctx.Response.WriteAsync("<h1>CNC GO - Dang xay dung...</h1><a href='/'>Quay lai</a>"); });
app.MapGet("/ban-khung-go", async ctx => { await ctx.Response.WriteAsync("<h1>BAN KHUNG GO - Dang xay dung...</h1><a href='/'>Quay lai</a>"); });
app.MapGet("/blow-fill", async ctx => { await ctx.Response.WriteAsync("<h1>BLOW FILL - Dang xay dung...</h1><a href='/'>Quay lai</a>"); });
app.MapGet("/glue-line", async ctx => { await ctx.Response.WriteAsync("<h1>GLUE LINE - Dang xay dung...</h1><a href='/'>Quay lai</a>"); });
app.MapGet("/sorting-foam", async ctx => { await ctx.Response.WriteAsync("<h1>SORTING FOAM - Dang xay dung...</h1><a href='/'>Quay lai</a>"); });
app.MapGet("/hand-fill", async ctx => { await ctx.Response.WriteAsync("<h1>HAND FILL - Dang xay dung...</h1><a href='/'>Quay lai</a>"); });
app.MapGet("/feather-fill", async ctx => { await ctx.Response.WriteAsync("<h1>FEATHER FILL - Dang xay dung...</h1><a href='/'>Quay lai</a>"); });

app.Run("http://0.0.0.0:5050");
// ==================== DATABASE MODELS ====================
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<Order> Orders { get; set; }
    public DbSet<MxDetail> MxDetails { get; set; }
    public DbSet<Kho2_Inventory> Kho2_Inventory { get; set; }
    public DbSet<ScanLog> ScanLogs { get; set; }    
    public DbSet<MoProgress> MoProgresses { get; set; }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // 🚀 INDEXES ĐỂ TĂNG TỐC QUERY
        modelBuilder.Entity<Order>()
            .HasIndex(o => new { o.OdrNo, o.DateKey });
        
        modelBuilder.Entity<Order>()
            .HasIndex(o => o.Status);
        
        modelBuilder.Entity<MxDetail>()
            .HasIndex(m => m.OdrNo);
            
        modelBuilder.Entity<ScanLog>()
            .HasIndex(s => new { s.MO, s.WorkCenter, s.ScanTime });
        
        modelBuilder.Entity<MoProgress>()
            .HasIndex(m => new { m.MO, m.WorkCenter });
    }
}

public class Order
{
    public int Id { get; set; }
    public string OdrNo { get; set; } = "";
    public string FItem { get; set; } = "";
    public string Mw { get; set; } = "";
    public string Qty { get; set; } = "";
    public string DeliveryDate { get; set; } = "";
    public string DeliveryTime { get; set; } = "";
    public string FileType { get; set; } = ""; // Console Lid / Other
    public string DateKey { get; set; } = "";  // VD: "18.05"
    public string Status { get; set; } = "Pending";
    public string Time { get; set; } = "";
    public string Note { get; set; } = "";
}
public class MxDetail
{
    public int Id { get; set; }
    public string OdrNo { get; set; } = "";
    public string ItemCode { get; set; } = "";
    public int ItemQty { get; set; }
    public string PartName { get; set; } = "";
    public int PartQty { get; set; }
    public int PartOrder { get; set; }
    public string LastUpdate { get; set; } = "";
}

public class As400ScanPollingService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<As400ScanPollingService> _logger;
    private DateTime _lastScanTime = DateTime.UtcNow.AddMinutes(-10);

    public As400ScanPollingService(IServiceProvider services, ILogger<As400ScanPollingService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AS400 Scan Polling Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                await PollOnceAsync(scope, stoppingToken); // Sửa ở đây
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while polling AS400 scan data");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task PollOnceAsync(IServiceScope scope, CancellationToken token) // Sửa ở đây
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<OrderHub>>();
        var _logger = scope.ServiceProvider.GetRequiredService<ILogger<As400ScanPollingService>>();

        var moList = await db.MoProgresses
            .Select(m => new { m.MO, m.WorkCenter })
            .Distinct()
            .ToListAsync(token);
        
        if (moList.Count == 0) return;

        var moInList = string.Join(",", moList.Select(m => $"'{m.MO}'").Distinct());
        var wcInList = string.Join(",", moList.Select(m => $"'{m.WorkCenter}'").Distinct());

        var newRows = new List<(string MO, string Item, string Wc, int Qty, DateTime ScanTime)>();

        try
        {
            using (var conn = new OdbcConnection("DSN=WFVNPROD;UID=WNKRND;PWD=wnkrnd@112;"))
            {
                await conn.OpenAsync(token);

                string sql = $@"
                    SELECT TRIM(A.ODORDR) AS ODORDR, TRIM(A.ODPN) AS ODPN, A.ODQTYC,
                           TRIM(A.ODWKCN) AS ODWKCN, A.ODTSTP
                    FROM WWDCF.GRPORDH A
                    WHERE A.ODORDR IN ({moInList})
                      AND A.ODWKCN IN ({wcInList})
                      AND A.ODTSTP > ?
                    ORDER BY A.ODTSTP";

                using var cmd = new OdbcCommand(sql, conn);
                cmd.Parameters.Add("?", OdbcType.VarChar).Value = _lastScanTime.ToString("yyyy-MM-dd-HH.mm.ss.ffffff");

                using var reader = await cmd.ExecuteReaderAsync(token);

                while (await reader.ReadAsync(token))
                {
                    string mo = reader.GetString(0);
                    string item = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    int qty = reader.IsDBNull(2) ? 0 : (int)reader.GetDecimal(2);
                    string wc = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    DateTime scanTime = reader.GetDateTime(4);

                    newRows.Add((mo, item, wc, qty, scanTime));

                    if (scanTime > _lastScanTime)
                        _lastScanTime = scanTime;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AS400 Polling] Exception during DB2 query.");
            return;
        }

        if (newRows.Count == 0) return;

        foreach (var row in newRows)
        {
            db.ScanLogs.Add(new ScanLog { MO = row.MO, Item = row.Item, WorkCenter = row.Wc, Qty = row.Qty, ScanTime = row.ScanTime });
            var mp = await db.MoProgresses.FirstOrDefaultAsync(m => m.MO == row.MO && m.WorkCenter == row.Wc, token);
            if (mp != null)
            {
                mp.ActualQty += row.Qty;
                mp.LastScanTime = row.ScanTime;
                mp.Status = ComputeStatus(mp);
            }
        }

        await db.SaveChangesAsync(token);

        var updatedMoList = newRows.Select(r => new { r.MO, r.Wc }).Distinct();
        _logger.LogInformation($"[AS400 Polling] Found {updatedMoList.Count()} updated MOs. Broadcasting via SignalR...");

        foreach (var item in updatedMoList)
        {
            var mp = await db.MoProgresses.FirstOrDefaultAsync(m => m.MO == item.MO && m.WorkCenter == item.Wc, token);
            if (mp != null)
            {
                await hubContext.Clients.All.SendAsync("MoProgressUpdated", new
                {
                    mo = mp.MO, mx = mp.MX, workCenter = mp.WorkCenter,
                    planned = mp.PlannedQty, actual = mp.ActualQty, status = mp.Status,
                    lastScanTime = mp.LastScanTime?.ToString("yyyy-MM-dd HH:mm:ss")
                }, token);
            }
        }
    }
    
    private string ComputeStatus(MoProgress mp)
    {
        if (mp.ActualQty <= 0) return "pending";
        if (mp.ActualQty < mp.PlannedQty) return "in-progress";

        // Khi đã quét đủ (ActualQty >= PlannedQty)
        if (mp.LastScanTime.HasValue)
        {
            try
            {
                if (string.IsNullOrEmpty(mp.LeadtimeString) || !mp.LeadtimeString.Contains('-'))
                {
                    return "done"; // Nếu không có leadtime, mặc định là Done
                }

                var parts = mp.LeadtimeString.Split('-');
                var endStr = parts[1].Trim();
                var endParts = endStr.Split(':');
                int endHour = int.Parse(endParts[0]);
                int endMin = int.Parse(endParts[1]);

                // Lấy ngày của lần quét cuối cùng
                DateTime targetDate = mp.LastScanTime.Value.Date;
                DateTime leadtimeEnd = targetDate.AddHours(endHour).AddMinutes(endMin);

                // Xử lý ca đêm (VD: 20:00 - 05:00)
                var startParts = parts[0].Trim().Split(':');
                int startHour = int.Parse(startParts[0]);
                if (endHour < startHour)
                {
                    // Nếu giờ kết thúc nhỏ hơn giờ bắt đầu, có thể là ngày hôm sau
                    // Giả định nếu quét sau nửa đêm nhưng trước giờ kết thúc, nó vẫn thuộc ca hôm trước
                    if(mp.LastScanTime.Value.TimeOfDay.TotalHours < endHour)
                    {
                        // Đã sang ngày mới
                    }
                    else
                    {
                        // Vẫn trong ngày cũ, nhưng leadtime end là ngày mai
                        leadtimeEnd = leadtimeEnd.AddDays(1);
                    }
                }

                if (mp.LastScanTime.Value <= leadtimeEnd)
                {
                    return "done"; // Hoàn thành đúng hạn
                }
                else
                {
                    return "late"; // Hoàn thành trễ
                }
            }
            catch
            {
                return "done"; // Lỗi parse leadtime, mặc định là Done
            }
        }

        return "done"; // Mặc định nếu không có LastScanTime
    }
}

// ==================== DTO MODELS ====================
record UpdateRequest(string Odrno, string Status, string Note);
record ExportRequest(string Date, string FileType, List<Dictionary<string, object>> Orders);
record MxItemData { public string ItemCode { get; set; } = ""; public int Quantity { get; set; } }
record PartDetailData { public string PartName { get; set; } = ""; public int Quantity { get; set; } public int Order { get; set; } }
record PartMappingData { public int Order { get; set; } public string PartName { get; set; } = ""; public int ColumnIndex { get; set; } }
record TrackingData(string Mx, List<WorkCenterStep> Steps);
record WorkCenterStep(string WorkCenter, string Mo, string Qty, string Leadtime);
public class Kho2_Inventory {
    public int Id { get; set; }
    public string OdrNo { get; set; } = "";        
    public string ZoneCode { get; set; } = "";     
    public DateTime InTime { get; set; }           
    public DateTime UpdateTime { get; set; }       
    public DateTime? OutTime { get; set; }         
    public string Status { get; set; } = "In";     
}
record Kho2ScanRequest(string Odrno, string ZoneCode);

public class ScanLog
{
    public int Id { get; set; }
    public string MO { get; set; } = "";
    public string Item { get; set; } = "";        // ODPN
    public string WorkCenter { get; set; } = "";  // ODWKCN
    public int Qty { get; set; }                  // ODQTYC
    public DateTime ScanTime { get; set; }        // ODTSTP
    public string Source { get; set; } = "AS400";
}

public class MoProgress
{
    public int Id { get; set; }
    public string MO { get; set; } = "";
    public string MX { get; set; } = "";
    public string WorkCenter { get; set; } = "";
    public int PlannedQty { get; set; }
    public int ActualQty { get; set; }
    public DateTime? LastScanTime { get; set; }
    public string Status { get; set; } = "Pending";
    public string LeadtimeString { get; set; } = ""; 
}

