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
using System.ComponentModel.DataAnnotations;
using Serilog;
System.IO.Ports.SerialPort? _scaleSerialPort = null;

// ==================== SERILOG SETUP ====================
var logDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
if (!Directory.Exists(logDir))
{
    Directory.CreateDirectory(logDir);
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Warning()
    .WriteTo.File(
        Path.Combine(logDir, "server.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,      // giữ 30 ngày log
        shared: true)
    .WriteTo.Console()                  // vẫn log ra console
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog(); 

    // DB chính
    builder.Services.AddDbContext<AppDbContext>(options =>
    {
        var cs = builder.Configuration.GetConnectionString("DefaultConnection");
        options.UseSqlite(cs);
    });

    // DB Blow-Fill (mới)
    builder.Services.AddDbContext<BlowFillDbContext>(options =>
    {
        var cs = builder.Configuration.GetConnectionString("BlowFillConnection");
        options.UseSqlite(cs);
    });

    builder.Services.AddDbContext<ToolManagementDbContext>(options =>
    {
        var cs = builder.Configuration.GetConnectionString("ToolManagementConnection");
        options.UseSqlite(cs);
    });

    builder.Services.AddSignalR();
    builder.Services.AddHostedService<As400ScanPollingService>();
    // builder.Services.AddHostedService<ScaleReaderService>();
    var app = builder.Build();


    // ===== REGISTER ENCODING PROVIDER =====
    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    // ===== CONFIGURATION =====
    string vDrivePath = @"V:\UPH Support\Public\B2\Data\nhung\LEADTIME B2\RUN KIT - NHẬN KIT";
    string rootMssPath = @"V:\Prod & Inv Control\Public\P&IC UPH\01.MSS for UPH\2026";
    string schedulePath = @"V:\UPH Support\Public\B2\Data\nhung\LEADTIME B2\LEADTIME UPH SUPPORT";
    string localData = @"D:\logtran\1. Project\CI Project\OrderTrackingWeb\Data";
    builder.Configuration["SchedulePath"] = schedulePath;
    string glueLinePath = @"V:\UPH Support\Public\B2\Data\nhung\LEADTIME B2\GLUE LINE";
    builder.Configuration["GlueLinePath"] = glueLinePath;

    if (!Directory.Exists(localData))
        Directory.CreateDirectory(localData);

    // ===== KHỞI TẠO DATABASE, ENABLE WAL MODE VÀ INITIAL LOAD =====
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<OrderHub>>();

        // Tạo database nếu chưa có
        db.Database.EnsureCreated();

        // ✅ ENABLE WAL MODE - QUAN TRỌNG!
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
        Console.WriteLine("✅ Database is ready at Data/OrderTracking.db with WAL mode enabled");

        // DB Blow-Fill
        try
        {
            var blowDb = scope.ServiceProvider.GetRequiredService<BlowFillDbContext>();
            blowDb.Database.EnsureCreated();
            Log.Information("✅ BlowFill DB is ready at Data/BlowFillWeigh.db");
        }
        catch (Exception ex)
        {
            Log.Error("❌ Sync error: {Message}", ex.Message);
        }

        // ✅ DB Tool Management
        var toolDb = scope.ServiceProvider.GetRequiredService<ToolManagementDbContext>();
        toolDb.Database.EnsureCreated();
        toolDb.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        toolDb.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
        Console.WriteLine("✅ Tool Management DB is ready at Data/ToolManagement.db");

        // 🔁 INITIAL LOAD: chạy sync + load kế hoạch một lần
        Console.WriteLine("🔁 Initial load: Sync RUN KIT + MX details + Schedule plan hôm nay...");

        // Gọi 2 hàm local (đã định nghĩa ở trên): SyncRunKitAndMxDetails & LoadSchedulePlan
        SyncRunKitAndMxDetails(db, hub, CancellationToken.None).GetAwaiter().GetResult();
        LoadSchedulePlan(DateTime.Today, db, CancellationToken.None).GetAwaiter().GetResult();

        Console.WriteLine("✅ Initial load hoàn tất.");
    }

    static string NormalizeWcForAs400(string wcFromExcel)
    {
        if (string.IsNullOrWhiteSpace(wcFromExcel)) return wcFromExcel;

        wcFromExcel = wcFromExcel.Trim().ToUpper();

        int underscoreIndex = wcFromExcel.IndexOf('_');
        if (underscoreIndex > 0)
        {
            return wcFromExcel.Substring(0, underscoreIndex);
        }

        return wcFromExcel;
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

                string odrno = ExcelHelpers.GetCellValue(row, 3);
                if (string.IsNullOrWhiteSpace(odrno)) continue;

                result.Add(new Order
                {
                    OdrNo = odrno,
                    FItem = ExcelHelpers.GetCellValue(row, 4),
                    Mw = ExcelHelpers.GetCellValue(row, 5),
                    Qty = ExcelHelpers.GetCellValue(row, 9),
                    DeliveryDate = ExcelHelpers.GetCellValue(row, 7),
                    DeliveryTime = ExcelHelpers.GetCellValue(row, 8),
                    FileType = fileType,
                    DateKey = dateKey,
                    Status = "Pending",
                    Time = ""
                });
            }
        }
        catch (Exception ex) { Console.WriteLine($"❌ Error reading file: {ex.Message}"); }
        return result;
    }

    // ==================== API ENDPOINTS ====================
    async Task SyncRunKitAndMxDetails(AppDbContext db, IHubContext<OrderHub> hubContext, CancellationToken token)
    {
        // Copy toàn bộ thân của /sync vào đây (từ Console.WriteLine("🔄 Starting sync...") 
        // đến return Results.Ok(...) — NHƯNG bỏ phần return, thay bằng chỉ log).

        // 1. ĐỌC VÀ GOM FILE RUN KIT
        Console.WriteLine("🔄 Starting sync to Database...");

        var files = Directory.GetFiles(vDrivePath)
            .Where(f => f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".xlsb", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
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
                var existingOrder = await db.Orders
                    .FirstOrDefaultAsync(o => o.OdrNo == newOrder.OdrNo && o.DateKey == newOrder.DateKey);

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
                var existingOrdersInDb = await db.Orders.Where(o => o.DateKey == date).ToListAsync();
                var newOrdersForDate = allNewOrders.Where(o => o.DateKey == date).ToList();

                var ordersToDelete = existingOrdersInDb
                    .Where(dbOrder => !newOrdersForDate.Any(newO =>
                        newO.OdrNo == dbOrder.OdrNo && newO.FileType == dbOrder.FileType))
                    .ToList();

                if (ordersToDelete.Any())
                {
                    db.Orders.RemoveRange(ordersToDelete);

                    var mxToDelete = ordersToDelete.Select(o => o.OdrNo).ToList();
                    var detailsToDelete = await db.MxDetails
                        .Where(d => mxToDelete.Contains(d.OdrNo))
                        .ToListAsync();
                    db.MxDetails.RemoveRange(detailsToDelete);

                    Console.WriteLine($"  🗑️ ĐÃ DỌN DẸP: Xóa {ordersToDelete.Count} MX không còn trong file Excel ngày {date}");
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

        var ordersByFileDate = allNewOrders.GroupBy(o => o.DateKey);
        foreach (var group in ordersByFileDate)
        {
            string dateKey = group.Key;
            var odrnos = group.Select(o => o.OdrNo).Distinct().ToList();
            Console.WriteLine($"  📅 Đang xử lý danh sách ngày: {dateKey} → {odrnos.Count} MX");

            DateTime parsedDate;
            try
            {
                var parts = dateKey.Split('.');
                parsedDate = new DateTime(DateTime.Now.Year, int.Parse(parts[1]), int.Parse(parts[0]));
            }
            catch
            {
                Console.WriteLine($"    ⚠️ Không parse được ngày từ DateKey: {dateKey}");
                continue;
            }

            string? exactInhousePath = FindInhouseFolder(parsedDate, rootMssPath);
            if (exactInhousePath == null)
            {
                Console.WriteLine($"    ⚠️ Bỏ qua ngày {dateKey} vì không tìm thấy folder INHOUSE.");
                continue;
            }

            string monthName = parsedDate.ToString("MMM", new System.Globalization.CultureInfo("en-US"));
            if (monthName == "Jun" && parsedDate.Month == 6) monthName = "June";
            if (monthName == "Jul" && parsedDate.Month == 7) monthName = "July";
            var searchPatterns = new[]
            {
                $"{monthName} {parsedDate.Day}",
                $"{monthName} {parsedDate.Day:D2}",
                $"{monthName}{parsedDate.Day}"
            };

            FileInfo? xlsbFile = null;
            foreach (var pattern in searchPatterns)
            {
                var foundFiles = Directory.GetFiles(exactInhousePath)
                    .Where(f => f.EndsWith(".xlsb", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).StartsWith("~") &&
                                Path.GetFileName(f).Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();

                if (foundFiles.Count > 0)
                {
                    xlsbFile = foundFiles.First();
                    break;
                }
            }

            if (xlsbFile == null)
            {
                Console.WriteLine($"    ⚠️ Không tìm thấy file XLSB có '{searchPatterns[0]}' trong {exactInhousePath}");
                continue;
            }

            Console.WriteLine($"    ✅ Tìm thấy file chi tiết: {xlsbFile.Name}");
            var details = await ParseMxDetailsFromXlsb(xlsbFile.FullName, odrnos);
            var oldDetails = db.MxDetails.Where(m => odrnos.Contains(m.OdrNo));
            db.MxDetails.RemoveRange(oldDetails);
            db.MxDetails.AddRange(details);

            var headers = await ParseMxHeadersFromXlsb(xlsbFile.FullName, odrnos, dateKey);
            var oldHeaders = db.MxHeaders.Where(h => odrnos.Contains(h.OdrNo) && h.DateKey == dateKey);
            db.MxHeaders.RemoveRange(oldHeaders);
            db.MxHeaders.AddRange(headers);

            await db.SaveChangesAsync();
        }

        await hubContext.Clients.All.SendAsync("MasterFileSynced", new
        {
            message = "Master file has been updated",
            time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        });

        Console.WriteLine("📡 Broadcasted sync completion to all clients");

        // =====================================================================
        // TỰ ĐỘNG DỌN DẸP DỮ LIỆU CŨ (LƯU 21 NGÀY)
        // =====================================================================
        Console.WriteLine("Đang dọn dẹp dữ liệu cũ hơn 21 ngày...");
        try
        {
            DateTime cutoffDate = DateTime.Now.Date.AddDays(-21);

            var allDbOrders = await db.Orders.ToListAsync();
            var ordersOld = new List<Order>();

            foreach (var o in allDbOrders)
            {
                try
                {
                    var dateParts = o.DateKey.Split('.');
                    int day = int.Parse(dateParts[0]);
                    int month = int.Parse(dateParts[1]);
                    int year = DateTime.Now.Year;
                    if (DateTime.Now.Month < 6 && month > 6) year--;

                    DateTime orderDate = new DateTime(year, month, day);
                    if (orderDate < cutoffDate)
                    {
                        ordersOld.Add(o);
                    }
                }
                catch { }
            }

            if (ordersOld.Any())
            {
                var mxToDelete = ordersOld.Select(o => o.OdrNo).ToList();
                var detailsToDelete = await db.MxDetails.Where(d => mxToDelete.Contains(d.OdrNo)).ToListAsync();
                db.MxDetails.RemoveRange(detailsToDelete);
                db.Orders.RemoveRange(ordersOld);

                Console.WriteLine($"   🗑️ Đã xóa {ordersOld.Count} MX và {detailsToDelete.Count} chi tiết cũ.");
            }

            var kho2ToDelete = await db.Kho2_Inventory
                .Where(k => k.Status == "Out" && k.OutTime != null && k.OutTime < cutoffDate)
                .ToListAsync();

            if (kho2ToDelete.Any())
            {
                db.Kho2_Inventory.RemoveRange(kho2ToDelete);
                Console.WriteLine($"Đã xóa lịch sử {kho2ToDelete.Count} xe xuất Kho 2 cũ.");
            }

            await db.SaveChangesAsync();
            Console.WriteLine("Dọn dẹp hoàn tất!");

            var oldMoProgressToDelete = await db.MoProgresses
                .Where(mp => mp.PlannedDate < DateTime.Now.Date.AddDays(-7))
                .ToListAsync();

            if (oldMoProgressToDelete.Any())
            {
                db.MoProgresses.RemoveRange(oldMoProgressToDelete);
                Console.WriteLine($"Đã xóa {oldMoProgressToDelete.Count} dòng MoProgress cũ.");
            }

            await db.SaveChangesAsync();
            Console.WriteLine("Dọn dẹp hoàn tất!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Lỗi khi dọn dẹp dữ liệu cũ: {ex.Message}");
        }
    }

    app.MapGet("/sync", async (AppDbContext db, IHubContext<OrderHub> hubContext) =>
    {
        try
        {
            await SyncRunKitAndMxDetails(db, hubContext, CancellationToken.None);
            return Results.Ok(new { message = "Đồng bộ Database thành công" });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Sync error: {ex.Message}");
            return Results.Problem(ex.Message);
        }
    });

    // // SYNC ENDPOINT (Đọc từ V Drive và lưu vào Database)
    // app.MapGet("/sync", async (AppDbContext db, IHubContext<OrderHub> hubContext) =>
    // {
    //     try
    //     {
    //         Console.WriteLine("🔄 Starting sync to Database...");

    //         var files = Directory.GetFiles(vDrivePath)
    //             .Where(f => f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
    //                         f.EndsWith(".xlsb", StringComparison.OrdinalIgnoreCase) ||
    //                         f.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
    //             .Where(f => !Path.GetFileName(f).StartsWith("~"))
    //             .Select(f => new FileInfo(f))
    //             .ToList();

    //         var fileGroups = new Dictionary<string, FileInfo>();
    //         foreach (var fileInfo in files)
    //         {
    //             var fileName = Path.GetFileNameWithoutExtension(fileInfo.Name);
    //             var match = System.Text.RegularExpressions.Regex.Match(fileName, @"(\d{2}[\.\-]\d{2})");
    //             if (!match.Success) continue;

    //             string dateKey = match.Value.Replace("-", ".");
    //             string fileType = fileName.Contains("Console Lid", StringComparison.OrdinalIgnoreCase) ? "Console Lid" : "Other";
    //             string groupKey = $"{dateKey}_{fileType}";

    //             if (!fileGroups.ContainsKey(groupKey) || fileInfo.LastWriteTime > fileGroups[groupKey].LastWriteTime)
    //             {
    //                 fileGroups[groupKey] = fileInfo; // Chỉ giữ lại file mới nhất
    //             }
    //         }

    //         var allNewOrders = new List<Order>();
    //         foreach (var group in fileGroups)
    //         {
    //             var parts = group.Key.Split('_');
    //             var dateKey = parts[0];
    //             var fileType = parts[1];
    //             var fileData = ReadExcelFile(group.Value.FullName, fileType, dateKey);
    //             allNewOrders.AddRange(fileData);
    //         }

    //         // Xử lý lưu vào DB: Giữ nguyên trạng thái (Status) và Ghi chú (Note) của các Order đã tồn tại
    //         using var transaction = await db.Database.BeginTransactionAsync();
    //         try
    //         {
    //             foreach (var newOrder in allNewOrders)
    //             {
    //                 var existingOrder = await db.Orders
    //                     .FirstOrDefaultAsync(o => o.OdrNo == newOrder.OdrNo && o.DateKey == newOrder.DateKey);

    //                 if (existingOrder != null)
    //                 {
    //                     existingOrder.FItem = newOrder.FItem;
    //                     existingOrder.Mw = newOrder.Mw;
    //                     existingOrder.Qty = newOrder.Qty;
    //                     existingOrder.DeliveryDate = newOrder.DeliveryDate;
    //                     existingOrder.DeliveryTime = newOrder.DeliveryTime;
    //                     existingOrder.FileType = newOrder.FileType;
    //                 }
    //                 else
    //                 {
    //                     db.Orders.Add(newOrder);
    //                 }
    //             }

    //             var processedDates = allNewOrders.Select(o => o.DateKey).Distinct().ToList();
    //             foreach (var date in processedDates)
    //             {
    //                 var existingOrdersInDb = await db.Orders.Where(o => o.DateKey == date).ToListAsync();
    //                 var newOrdersForDate = allNewOrders.Where(o => o.DateKey == date).ToList();

    //                 var ordersToDelete = existingOrdersInDb
    //                     .Where(dbOrder => !newOrdersForDate.Any(newO =>
    //                         newO.OdrNo == dbOrder.OdrNo && newO.FileType == dbOrder.FileType))
    //                     .ToList();

    //                 if (ordersToDelete.Any())
    //                 {
    //                     db.Orders.RemoveRange(ordersToDelete);

    //                     var mxToDelete = ordersToDelete.Select(o => o.OdrNo).ToList();
    //                     var detailsToDelete = await db.MxDetails
    //                         .Where(d => mxToDelete.Contains(d.OdrNo))
    //                         .ToListAsync();
    //                     db.MxDetails.RemoveRange(detailsToDelete);

    //                     Console.WriteLine($"  🗑️ ĐÃ DỌN DẸP: Xóa {ordersToDelete.Count} MX không còn trong file Excel ngày {date}");
    //                 }
    //             }

    //             await db.SaveChangesAsync();
    //             await transaction.CommitAsync();
    //         }
    //         catch (Exception ex)
    //         {
    //             await transaction.RollbackAsync();
    //             Console.WriteLine($"❌ Sync error: {ex.Message}");
    //             throw;
    //         }

    //         Console.WriteLine($"✅ Synced Orders to Database!");

    //         // 🚀 Tra cứu chi tiết MX dựa trên NGÀY CỦA FILE DANH SÁCH (DateKey)
    //         Console.WriteLine("📊 Parsing MX details based on List File Date (DateKey)...");

    //         var ordersByFileDate = allNewOrders.GroupBy(o => o.DateKey);
    //         foreach (var group in ordersByFileDate)
    //         {
    //             string dateKey = group.Key;
    //             var odrnos = group.Select(o => o.OdrNo).Distinct().ToList();
    //             Console.WriteLine($"  📅 Đang xử lý danh sách ngày: {dateKey} → {odrnos.Count} MX");

    //             DateTime parsedDate;
    //             try
    //             {
    //                 var parts = dateKey.Split('.');
    //                 parsedDate = new DateTime(DateTime.Now.Year, int.Parse(parts[1]), int.Parse(parts[0]));
    //             }
    //             catch
    //             {
    //                 Console.WriteLine($"    ⚠️ Không parse được ngày từ DateKey: {dateKey}");
    //                 continue;
    //             }

    //             string? exactInhousePath = FindInhouseFolder(parsedDate, rootMssPath);
    //             if (exactInhousePath == null)
    //             {
    //                 Console.WriteLine($"    ⚠️ Bỏ qua ngày {dateKey} vì không tìm thấy folder INHOUSE.");
    //                 continue;
    //             }

    //             string monthName = parsedDate.ToString("MMM", new System.Globalization.CultureInfo("en-US"));
    //             if (monthName == "Jun" && parsedDate.Month == 6) monthName = "June";
    //             if (monthName == "Jul" && parsedDate.Month == 7) monthName = "July";
    //             var searchPatterns = new[]
    //             {
    //                 $"{monthName} {parsedDate.Day}",
    //                 $"{monthName} {parsedDate.Day:D2}",
    //                 $"{monthName}{parsedDate.Day}"
    //             };

    //             FileInfo? xlsbFile = null;
    //             foreach (var pattern in searchPatterns)
    //             {
    //                 var foundFiles = Directory.GetFiles(exactInhousePath)
    //                     .Where(f => f.EndsWith(".xlsb", StringComparison.OrdinalIgnoreCase) &&
    //                                 !Path.GetFileName(f).StartsWith("~") &&
    //                                 Path.GetFileName(f).Contains(pattern, StringComparison.OrdinalIgnoreCase))
    //                     .Select(f => new FileInfo(f))
    //                     .OrderByDescending(f => f.LastWriteTime)
    //                     .ToList();

    //                 if (foundFiles.Count > 0)
    //                 {
    //                     xlsbFile = foundFiles.First();
    //                     break;
    //                 }
    //             }

    //             if (xlsbFile == null)
    //             {
    //                 Console.WriteLine($"    ⚠️ Không tìm thấy file XLSB có '{searchPatterns[0]}' trong {exactInhousePath}");
    //                 continue;
    //             }

    //             Console.WriteLine($"    ✅ Tìm thấy file chi tiết: {xlsbFile.Name}");
    //             var details = await ParseMxDetailsFromXlsb(xlsbFile.FullName, odrnos);

    //             var oldDetails = db.MxDetails.Where(m => odrnos.Contains(m.OdrNo));
    //             db.MxDetails.RemoveRange(oldDetails);
    //             db.MxDetails.AddRange(details);
    //             await db.SaveChangesAsync();
    //         }

    //         await hubContext.Clients.All.SendAsync("MasterFileSynced", new
    //         {
    //             message = "Master file has been updated",
    //             time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
    //         });

    //         Console.WriteLine("📡 Broadcasted sync completion to all clients");

    //         // =====================================================================
    //         // TỰ ĐỘNG DỌN DẸP DỮ LIỆU CŨ (LƯU 21 NGÀY)
    //         // =====================================================================
    //         Console.WriteLine("Đang dọn dẹp dữ liệu cũ hơn 21 ngày...");
    //         try
    //         {
    //             DateTime cutoffDate = DateTime.Now.Date.AddDays(-21);

    //             var allDbOrders = await db.Orders.ToListAsync();
    //             var ordersOld = new List<Order>();

    //             foreach (var o in allDbOrders)
    //             {
    //                 try
    //                 {
    //                     var dateParts = o.DateKey.Split('.');
    //                     int day = int.Parse(dateParts[0]);
    //                     int month = int.Parse(dateParts[1]);
    //                     int year = DateTime.Now.Year;
    //                     if (DateTime.Now.Month < 6 && month > 6) year--;

    //                     DateTime orderDate = new DateTime(year, month, day);
    //                     if (orderDate < cutoffDate)
    //                     {
    //                         ordersOld.Add(o);
    //                     }
    //                 }
    //                 catch { }
    //             }

    //             if (ordersOld.Any())
    //             {
    //                 var mxToDelete = ordersOld.Select(o => o.OdrNo).ToList();
    //                 var detailsToDelete = await db.MxDetails.Where(d => mxToDelete.Contains(d.OdrNo)).ToListAsync();
    //                 db.MxDetails.RemoveRange(detailsToDelete);
    //                 db.Orders.RemoveRange(ordersOld);

    //                 Console.WriteLine($"   🗑️ Đã xóa {ordersOld.Count} MX và {detailsToDelete.Count} chi tiết cũ.");
    //             }

    //             var kho2ToDelete = await db.Kho2_Inventory
    //                 .Where(k => k.Status == "Out" && k.OutTime != null && k.OutTime < cutoffDate)
    //                 .ToListAsync();

    //             if (kho2ToDelete.Any())
    //             {
    //                 db.Kho2_Inventory.RemoveRange(kho2ToDelete);
    //                 Console.WriteLine($"Đã xóa lịch sử {kho2ToDelete.Count} xe xuất Kho 2 cũ.");
    //             }

    //             await db.SaveChangesAsync();
    //             Console.WriteLine("Dọn dẹp hoàn tất!");

    //             var oldMoProgressToDelete = await db.MoProgresses
    //                 .Where(mp => mp.PlannedDate < DateTime.Now.Date.AddDays(-7))
    //                 .ToListAsync();

    //             if (oldMoProgressToDelete.Any())
    //             {
    //                 db.MoProgresses.RemoveRange(oldMoProgressToDelete);
    //                 Console.WriteLine($"Đã xóa {oldMoProgressToDelete.Count} dòng MoProgress cũ.");
    //             }

    //             await db.SaveChangesAsync();
    //             Console.WriteLine("Dọn dẹp hoàn tất!");
    //         }
    //         catch (Exception ex)
    //         {
    //             Console.WriteLine($"Lỗi khi dọn dẹp dữ liệu cũ: {ex.Message}");
    //         }

    //         return Results.Ok(new { message = "Đồng bộ Database thành công" });
    //     }
    //     catch (Exception ex)
    //     {
    //         Console.WriteLine($"Sync error: {ex.Message}");
    //         return Results.Problem(ex.Message);
    //     }
    // });

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

            var list = await query.Select(o => new
            {
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

    // UPDATE STATUS ENDPOINT
    app.MapPost("/update", async (UpdateRequest data, AppDbContext db, IHubContext<OrderHub> hubContext) =>
    {
        try
        {
            if (data == null || string.IsNullOrEmpty(data.Odrno))
                return Results.BadRequest("Invalid request");

            var order = await db.Orders
                .FirstOrDefaultAsync(o => o.OdrNo.ToUpper() == data.Odrno.ToUpper());

            if (order != null)
            {
                order.Status = data.Status;
                order.Note = data.Note ?? "";
                order.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
            else if (data.Status == "NOT FOUND")
            {
                db.Orders.Add(new Order
                {
                    OdrNo = data.Odrno.ToUpper(),
                    Status = "NOT FOUND",
                    Note = data.Note ?? "",
                    Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    DateKey = DateTime.Now.ToString("dd.MM"),
                    FileType = "Other"
                });
            }

            await db.SaveChangesAsync();

            await hubContext.Clients.All.SendAsync("OrderUpdated", new
            {
                odrno = data.Odrno,
                status = data.Status,
                note = data.Note ?? ""
            });

            return Results.Ok(new { message = "✅ Updated successfully" });
        }
        catch (Exception ex) { return Results.Problem(ex.Message); }
    });

    // UPLOAD FILE ENDPOINT
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
            var uploadedOdrNos = new List<string>();

            for (int i = 4; i < table.Rows.Count; i++)
            {
                var row = table.Rows[i];
                if (table.Columns.Count < 4) continue;

                string odrno = ExcelHelpers.GetCellValue(row, 3);
                if (string.IsNullOrWhiteSpace(odrno)) continue;

                uploadedOdrNos.Add(odrno);

                var existingOrder = await db.Orders
                    .FirstOrDefaultAsync(o => o.OdrNo == odrno && o.DateKey == dateKey);

                if (existingOrder == null)
                {
                    db.Orders.Add(new Order
                    {
                        OdrNo = odrno,
                        FItem = ExcelHelpers.GetCellValue(row, 4),
                        Mw = ExcelHelpers.GetCellValue(row, 5),
                        Qty = ExcelHelpers.GetCellValue(row, 9),
                        DeliveryDate = ExcelHelpers.GetCellValue(row, 7),
                        DeliveryTime = ExcelHelpers.GetCellValue(row, 8),
                        FileType = fileType,
                        DateKey = dateKey,
                        Status = "Pending",
                        Time = "",
                        Note = ""
                    });
                    count++;
                }
            }

            var oldOrdersInDb = await db.Orders
                .Where(o => o.DateKey == dateKey && o.FileType == fileType)
                .ToListAsync();

            var ordersToRemove = oldOrdersInDb
                .Where(o => !uploadedOdrNos.Contains(o.OdrNo))
                .ToList();

            if (ordersToRemove.Any())
            {
                db.Orders.RemoveRange(ordersToRemove);

                var mxToRemove = ordersToRemove.Select(o => o.OdrNo).ToList();
                var detailsToRemove = await db.MxDetails
                    .Where(d => mxToRemove.Contains(d.OdrNo))
                    .ToListAsync();
                db.MxDetails.RemoveRange(detailsToRemove);

                Console.WriteLine($"  🗑️ Upload dọn dẹp: Đã xóa {ordersToRemove.Count} MX cũ.");
            }

            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                message = "✅ Upload thành công",
                date = dateKey,
                fileType = fileType,
                orderCount = count
            });
        }
        catch (Exception ex) { return Results.Problem(ex.Message); }
    });

    // GET MX DETAIL FROM DB CACHE
    app.MapGet("/mx-detail", async (string odrno, string date, AppDbContext db) =>
    {
        try
        {
            Console.WriteLine($"\n🔍 Đang tìm chi tiết cho MX: {odrno}");

            // 1. Lấy FItem từ bảng Orders (nguồn dữ liệu chính xác)
            var orderInfo = await db.Orders
                .FirstOrDefaultAsync(o => o.OdrNo.ToUpper() == odrno.ToUpper() && o.DateKey == date);
            
            string fItemToShow = orderInfo?.FItem ?? ""; // Lấy FItem từ Order, nếu không có thì để trống
            var mxHeader = await db.MxHeaders.FirstOrDefaultAsync(h => h.OdrNo.ToUpper() == odrno.ToUpper() && h.DateKey == date);
            string uphLine = mxHeader?.UphLine ?? "";
            string expValue = mxHeader?.ExpValue ?? "";

            // 2. Lấy chi tiết Parts từ bảng MxDetails (giữ nguyên)
            var details = await db.MxDetails
                .Where(m => m.OdrNo.ToUpper() == odrno.ToUpper())
                .ToListAsync();

            Console.WriteLine($"📊 Tìm thấy {details.Count} dòng dữ liệu trong Database");

            if (details.Count == 0 && string.IsNullOrEmpty(fItemToShow))
            {
                return Results.NotFound($"Không tìm thấy chi tiết cho MX {odrno}.");
            }

            // 3. Gom nhóm dữ liệu Parts (giữ nguyên)
            var partsList = details
                .GroupBy(d => new { d.PartName, d.PartNameVN, d.PartOrder }) // ✅ Thêm PartNameVN vào GroupBy
                .Select(g => new
                {
                    PartName = g.Key.PartName,
                    PartNameVN = g.Key.PartNameVN, // ✅ Trả về PartNameVN
                    Order = g.Key.PartOrder,
                    Quantity = g.Sum(x => x.PartQty)
                })
                .OrderBy(p => p.Order)
                .ToList();

            // 4. Lấy số lượng từ bảng Orders
            int quantity = 0;
            if (orderInfo != null && int.TryParse(orderInfo.Qty, out int qtyValue))
            {
                quantity = qtyValue;
            }

            // 5. Đóng gói kết quả trả về
            var itemsList = new List<object>();
            if (!string.IsNullOrEmpty(fItemToShow))
            {
                itemsList.Add(new { ItemCode = fItemToShow, Quantity = quantity });
            }

            Console.WriteLine($"✅ Đã đóng gói: {itemsList.Count} Items, {partsList.Count} Parts");

            return Results.Ok(new
            {
                odrno = odrno,
                items = itemsList,
                parts = partsList,
                uphLine = uphLine,
                expValue = expValue 
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Lỗi API mx-detail: {ex.Message}");
            return Results.Problem(ex.Message);
        }
    });

    // ==================== EXPORT REPORT ENDPOINT ====================
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

            string dateKey = request.Date;
            string dateKeyDash = dateKey.Replace(".", "-");
            string fileType = request.FileType ?? "";

            var allFilesInDrive = Directory.GetFiles(vDrivePath)
                .Where(f => f.EndsWith(".xlsb", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).StartsWith("~"))
                .ToList();

            var matchedFiles = allFilesInDrive
                .Where(f => Path.GetFileName(f).Contains(dateKey) ||
                            Path.GetFileName(f).Contains(dateKeyDash))
                .Select(f => new FileInfo(f))
                .ToList();

            FileInfo? originalFile = null;

            if (matchedFiles.Count > 0)
            {
                if (fileType.Equals("Console Lid", StringComparison.OrdinalIgnoreCase))
                    originalFile = matchedFiles
                        .Where(f => f.Name.Contains("Console Lid", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();
                else
                    originalFile = matchedFiles
                        .Where(f => !f.Name.Contains("Console Lid", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();

                if (originalFile == null)
                    originalFile = matchedFiles.OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            }

            if (originalFile == null)
                return Results.BadRequest($"Không tìm thấy file danh sách gốc ngày {request.Date}.");

            Console.WriteLine($"  📄 Đã chọn file gốc: {originalFile.Name}");

            string tempDir = Path.Combine(Path.GetTempPath(), "MES_Exports");
            Directory.CreateDirectory(tempDir);
            string outputPath = Path.Combine(tempDir, $"BaoCao_GiaoNhan_{request.Date}_{DateTime.Now:HHmmss}.xlsb");

            Type? excelType = Type.GetTypeFromProgID("Excel.Application");
            if (excelType == null)
            {
                return Results.BadRequest("Máy chủ không cài Microsoft Excel. Không thể xuất .xlsb!");
            }

            Console.WriteLine("  ⚙️ Đang mở Excel ngầm...");

            dynamic excelApp = Activator.CreateInstance(excelType)!;

            try
            {
                excelApp.Visible = false;
                excelApp.DisplayAlerts = false;
                excelApp.AskToUpdateLinks = false;

                dynamic workbooks = excelApp.Workbooks;
                dynamic wb = workbooks.Open(originalFile.FullName, 0, true);
                dynamic ws = wb.Sheets[1];
                dynamic cells = ws.Cells;
                dynamic lastCell = cells[ws.Rows.Count, 4];
                int lastRow = lastCell.End(-4162).Row;

                Console.WriteLine($"  ✍️ Ghi 'OK' từ dòng 5 đến {lastRow}...");

                for (int r = 5; r <= lastRow; r++)
                {
                    dynamic mxCell = cells[r, 4];
                    var cellValue = mxCell.Value;

                    if (cellValue != null)
                    {
                        string mxCode = cellValue.ToString().Trim().ToUpper();
                        if (receivedOdrnos.Contains(mxCode))
                        {
                            dynamic cellK = cells[r, 11];
                            cellK.Value = "OK";
                            cellK.Font.Bold = true;
                            cellK.Font.Color = 32768;
                        }
                    }
                }

                Console.WriteLine("  💾 Đang lưu file .xlsb...");
                wb.SaveAs(outputPath, 50);
                wb.Close(false);
                excelApp.Quit();
            }
            catch (Exception ex)
            {
                excelApp.Quit();
                throw new Exception("Lỗi khi điều khiển Excel: " + ex.Message);
            }

            if (File.Exists(outputPath))
            {
                var fileBytes = File.ReadAllBytes(outputPath);
                File.Delete(outputPath);

                Console.WriteLine("  ✅ Xuất báo cáo XLSB thành công!");
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

    // ==================== HELPER: TÌM THƯ MỤC INHOUSE ====================
    string? FindInhouseFolder(DateTime targetDate, string rootPath)
    {
        Console.WriteLine($"\n  🔍 Đang tìm folder INHOUSE cho ngày: {targetDate:dd/MM/yyyy}");

        if (!Directory.Exists(rootPath))
        {
            Console.WriteLine($"  ❌ Không tìm thấy thư mục gốc: {rootPath}");
            return null;
        }

        var mssDirs = Directory.GetDirectories(rootPath, "MSS*");
        string? selectedMssDir = null;
        DateTime? closestDate = null;

        foreach (var dir in mssDirs)
        {
            string folderName = new DirectoryInfo(dir).Name; // MSS0527
            if (folderName.Length >= 7)
            {
                string monthStr = folderName.Substring(3, 2);
                string dayStr = folderName.Substring(5, 2);

                if (int.TryParse(monthStr, out int m) && int.TryParse(dayStr, out int d))
                {
                    try
                    {
                        DateTime folderDate = new DateTime(targetDate.Year, m, d);
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
            Console.WriteLine($"  ❌ Không tìm thấy folder MSS bao phủ ngày {targetDate:dd/MM/yyyy}");
            return null;
        }

        Console.WriteLine($"  ✅ Folder tuần: {new DirectoryInfo(selectedMssDir).Name}");

        string path1 = Path.Combine(selectedMssDir, @"Sub Schedule\kit Schedule\WIP\2.KIT STACK OUT\INHOUSE");
        string path2 = Path.Combine(selectedMssDir, @"kit Schedule\WIP\2.KIT STACK OUT\INHOUSE");

        if (Directory.Exists(path1))
        {
            Console.WriteLine("  ✅ INHOUSE tại cấu trúc 1.");
            return path1;
        }
        else if (Directory.Exists(path2))
        {
            Console.WriteLine("  ✅ INHOUSE tại cấu trúc 2.");
            return path2;
        }
        else
        {
            Console.WriteLine($"  ⚠️ Không thấy cấu trúc chuẩn, đang quét sâu {new DirectoryInfo(selectedMssDir).Name}...");
            try
            {
                var fallbackDirs = Directory.GetDirectories(selectedMssDir, "INHOUSE", SearchOption.AllDirectories);
                if (fallbackDirs.Length > 0)
                {
                    Console.WriteLine("  ✅ Tìm thấy INHOUSE bằng quét sâu.");
                    return fallbackDirs[0];
                }
            }
            catch { }
        }

        Console.WriteLine("  ❌ Không tìm thấy INHOUSE trong tuần này!");
        return null;
    }

    // ==================== FUNCTION: PARSE MX DETAILS NATIVELY ====================
    async Task<List<MxDetail>> ParseMxDetailsFromXlsb(string xlsbFile, List<string> odrnos)
    {
        var result = new List<MxDetail>();

        string CleanStr(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            return input.Replace("*", "").Replace("\u00A0", "").Replace("\u200B", "").Trim().ToUpper();
        }

        try
        {
            Console.WriteLine($"  ⚡ Parsing MX details from: {Path.GetFileName(xlsbFile)}");

            using var stream = File.Open(xlsbFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration() { FallbackEncoding = Encoding.GetEncoding(1252) });

            var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration()
            {
                ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = false }
            });

            if (!dataSet.Tables.Contains("Print") || !dataSet.Tables.Contains("4") || !dataSet.Tables.Contains("13") || !dataSet.Tables.Contains("17") || !dataSet.Tables.Contains("6"))
            {
                Console.WriteLine("  ❌ Missing required sheets (Print, 4, 6, 13, or 17)");
                return result;
            }

            var sheetPrint = dataSet.Tables["Print"];
            var sheet4 = dataSet.Tables["4"];
            var sheet13 = dataSet.Tables["13"];
            var sheet17 = dataSet.Tables["17"];
            var sheet6 = dataSet.Tables["6"];

            var partMapping = GetPartMapping();

            string GetCell(DataTable dt, int rowIdx, int colIdx)
            {
                if (rowIdx >= dt.Rows.Count || colIdx >= dt.Columns.Count) return "";
                return dt.Rows[rowIdx][colIdx]?.ToString() ?? "";
            }

            var mxInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int r = 5; r < sheetPrint.Rows.Count; r++) { var mx = CleanStr(GetCell(sheetPrint, r, 18)); if (!string.IsNullOrEmpty(mx)) mxInFile.Add(mx); }
            var mxToProcess = odrnos.Where(mx => mxInFile.Contains(CleanStr(mx))).Select(m => CleanStr(m)).ToList();
            var fallbackItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int r = 1; r < sheet17.Rows.Count; r++) { string mx = CleanStr(GetCell(sheet17, r, 1)); string fallbackItem = CleanStr(GetCell(sheet17, r, 50)); if (!string.IsNullOrEmpty(mx) && !string.IsNullOrEmpty(fallbackItem)) fallbackItems[mx] = fallbackItem; }
            
            var sheet13Rows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int r = 0; r < sheet13.Rows.Count; r++) { 
                string item = CleanStr(GetCell(sheet13, r, 0));
                if (!string.IsNullOrEmpty(item) && !sheet13Rows.ContainsKey(item))
                {
                    sheet13Rows[item] = r;
                }
            }
            
            var sheet4Items = new Dictionary<string, List<(string ItemCode, int Qty)>>(StringComparer.OrdinalIgnoreCase);
            string lastValidItemCode = ""; int lastValidQty = 0;
            for (int r = 1; r < sheet4.Rows.Count; r++) { string rawMx = CleanStr(GetCell(sheet4, r, 0)); if (string.IsNullOrWhiteSpace(rawMx)) continue; var mxList = rawMx.Split(new[] { '/', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(m => CleanStr(m)).Where(m => !string.IsNullOrEmpty(m)); string currentItemCode = CleanStr(GetCell(sheet4, r, 10)); string qtyStr = CleanStr(GetCell(sheet4, r, 17)); int currentQty = 0; if (int.TryParse(qtyStr, out int q)) currentQty = q; else if (double.TryParse(qtyStr, out double qd)) currentQty = (int)qd; if (string.IsNullOrEmpty(currentItemCode)) currentItemCode = lastValidItemCode; else lastValidItemCode = currentItemCode; if (currentQty <= 0) currentQty = lastValidQty; else lastValidQty = currentQty; if (!string.IsNullOrEmpty(currentItemCode) && currentQty > 0) { foreach (var mx in mxList) { if (!sheet4Items.ContainsKey(mx)) sheet4Items[mx] = new List<(string, int)>(); sheet4Items[mx].Add((currentItemCode, currentQty)); } } }
            
            foreach (var odrno in mxToProcess)
            {
                if (!sheet4Items.TryGetValue(odrno, out var items)) continue;

                foreach (var (originalItemCode, itemQty) in items)
                {
                    int itemRowIdx = -1;
                    string finalItemCode = originalItemCode;

                    if (sheet13Rows.TryGetValue(originalItemCode, out int row13)) { itemRowIdx = row13; }
                    else { if (fallbackItems.TryGetValue(odrno, out var fallbackItemCode)) { finalItemCode = fallbackItemCode; if (sheet13Rows.TryGetValue(fallbackItemCode, out int fallbackRow13)) itemRowIdx = fallbackRow13; } }

                    if (itemRowIdx == -1) continue;

                    // Xử lý các part thông thường từ danh sách mapping
                    foreach (var part in partMapping)
                    {
                        int partQtyPerItem = 0;
                        try 
                        { 
                            var cellVal = CleanStr(GetCell(sheet13, itemRowIdx, part.ColumnIndex - 1)); 
                            if (int.TryParse(cellVal, out int p)) partQtyPerItem = p; 
                            else if (double.TryParse(cellVal, out double pd)) partQtyPerItem = (int)pd;
                        } 
                        catch { }

                        if (partQtyPerItem > 0) 
                        { 
                            result.Add(new MxDetail 
                            { 
                                OdrNo = odrno, 
                                ItemCode = finalItemCode, 
                                ItemQty = itemQty, 
                                PartName = part.PartName,       // Lấy tên tiếng Anh từ mapping
                                PartNameVN = part.PartNameVN,   // Lấy tên tiếng Việt từ mapping
                                PartQty = partQtyPerItem * itemQty, 
                                PartOrder = part.Order, 
                                LastUpdate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") 
                            }); 
                        }
                    }

                    // Xử lý các part đặc biệt từ Sheet '6'
                    int sumCoolCloth_Wc192 = 0, sumCoolCloth_Wc193 = 0;
                    int sumTyparB2_Wc192 = 0, sumTyparB2_Wc193 = 0;

                    for (int r = 1; r < sheet6.Rows.Count; r++)
                    {
                        var row = sheet6.Rows[r];
                        string mxInSheet6 = CleanStr(GetCell(sheet6, r, 3));
                        if (mxInSheet6 == odrno)
                        {
                            string adCol = CleanStr(GetCell(sheet6, r, 29));
                            string wc = CleanStr(GetCell(sheet6, r, 19));
                            string qtyStr = GetCell(sheet6, r, 14);
                            int.TryParse(qtyStr, out int qty);

                            if (adCol == "-")
                            {
                                if (wc == "WC192") sumCoolCloth_Wc192 += qty;
                                if (wc == "WC193") sumCoolCloth_Wc193 += qty;
                            }
                            else if (adCol == "YES")
                            {
                                if (wc == "WC192") sumTyparB2_Wc192 += qty;
                                if (wc == "WC193") sumTyparB2_Wc193 += qty;
                            }
                        }
                    }

                    int totalCoolClothQty = sumCoolCloth_Wc192 + sumCoolCloth_Wc193;
                    if (totalCoolClothQty > 0)
                    {
                        result.Add(new MxDetail { OdrNo = odrno, ItemCode = finalItemCode, ItemQty = itemQty, PartName = "Typar UPH & COOL CLOTH", PartNameVN = "Lót Typar UPH", PartQty = totalCoolClothQty, PartOrder = 30, LastUpdate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
                    }

                    int totalTyparB2Qty = sumTyparB2_Wc192 + sumTyparB2_Wc193;
                    if (totalTyparB2Qty > 0)
                    {
                        result.Add(new MxDetail { OdrNo = odrno, ItemCode = finalItemCode, ItemQty = itemQty, PartName = "Typar B2", PartNameVN = "Lót Typar B2", PartQty = totalTyparB2Qty, PartOrder = 44, LastUpdate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
                    }
                }
            }

            Console.WriteLine($"  ✅ Đã xử lý xong {result.Count} dòng chi tiết Part.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error ParseMxDetailsFromXlsb: {ex.Message}");
        }
        return result;
    }

    async Task<List<MxHeader>> ParseMxHeadersFromXlsb(string xlsbFile, List<string> odrnos, string dateKey)
    {
        var result = new List<MxHeader>();
        var mxToUphLineMap = new Dictionary<string, string>();
        var mxToExpValueMap = new Dictionary<string, string>(); // ✅ Đổi tên map

        try
        {
            using var stream = File.Open(xlsbFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration { ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = false } });

            // Lấy UPH Line từ Sheet '4' (giữ nguyên)
            if (dataSet.Tables.Contains("4"))
            {
                var sheet4 = dataSet.Tables["4"];
                for (int r = 1; r < sheet4.Rows.Count; r++)
                {
                    var row = sheet4.Rows[r];
                    string mx = ExcelHelpers.GetCellValue(row, 4)?.Trim().ToUpper() ?? ""; // Cột E
                    string uphLine = ExcelHelpers.GetCellValue(row, 20)?.Trim() ?? "";    // Cột U
                    if (!string.IsNullOrEmpty(mx) && !string.IsNullOrEmpty(uphLine))
                    {
                        mxToUphLineMap[mx] = uphLine;
                    }
                }
            }
            
            // ✅ LẤY #EXP TỪ SHEET '15'
            if (dataSet.Tables.Contains("15"))
            {
                var sheet15 = dataSet.Tables["15"];
                for (int r = 1; r < sheet15.Rows.Count; r++)
                {
                    var row = sheet15.Rows[r];
                    string mx = ExcelHelpers.GetCellValue(row, 5)?.Trim().ToUpper() ?? ""; // Cột F
                    string expValue = ExcelHelpers.GetCellValue(row, 19)?.Trim() ?? "";    // Cột T
                    if (!string.IsNullOrEmpty(mx) && !string.IsNullOrEmpty(expValue))
                    {
                        mxToExpValueMap[mx] = expValue;
                    }
                }
            }

            // Tạo danh sách MxHeader
            foreach (var odrno in odrnos)
            {
                var odrnoUpper = odrno.ToUpper();
                mxToUphLineMap.TryGetValue(odrnoUpper, out var uphLine);
                mxToExpValueMap.TryGetValue(odrnoUpper, out var expValue); // ✅ Lấy ExpValue

                if (!string.IsNullOrEmpty(uphLine) || !string.IsNullOrEmpty(expValue))
                {
                    result.Add(new MxHeader
                    {
                        OdrNo = odrno,
                        DateKey = dateKey,
                        UphLine = uphLine ?? "",
                        ExpValue = expValue ?? "" // ✅ Gán ExpValue
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error parsing UPH/#EXP Lines: {ex.Message}");
        }
        return result;
    }

    List<PartMappingData> GetPartMapping()
    {
        return new List<PartMappingData> {
            new PartMappingData { Order = 1, PartName = "Inside Arm", PartNameVN = "Tay ghế trong", ColumnIndex = 2 },
            new PartMappingData { Order = 2, PartName = "Outside Arm", PartNameVN = "Tay ghế ngoài", ColumnIndex = 3 },
            new PartMappingData { Order = 3, PartName = "Front Arm panel", PartNameVN = "Ốp trước tay ghế", ColumnIndex = 4 },
            new PartMappingData { Order = 4, PartName = "Wing Panel", PartNameVN = "Ốp Wing", ColumnIndex = 5 },
            new PartMappingData { Order = 5, PartName = "Arm Fiber", PartNameVN = "Gòn tay", ColumnIndex = 6 },
            new PartMappingData { Order = 6, PartName = "Wing Fiber", PartNameVN = "Gòn Wing", ColumnIndex = 7 },
            new PartMappingData { Order = 7, PartName = "Wing Cover", PartNameVN = "Vải Bọc Wing", ColumnIndex = 8 },
            new PartMappingData { Order = 8, PartName = "Outside Arm Flap", PartNameVN = "Ốp tay ghế ngoài", ColumnIndex = 9 },
            new PartMappingData { Order = 9, PartName = "Back Fiber", PartNameVN = "Gòn lưng", ColumnIndex = 10 },
            new PartMappingData { Order = 10, PartName = "Inside Back", PartNameVN = "Lưng ghế Trong", ColumnIndex = 11 },
            new PartMappingData { Order = 11, PartName = "Outside Back", PartNameVN = "Lưng ghế Ngoài", ColumnIndex = 12 },
            new PartMappingData { Order = 12, PartName = "Seat", PartNameVN = "Đáy ghế", ColumnIndex = 14 },
            new PartMappingData { Order = 13, PartName = "Seat band", PartNameVN = "Miếng ốp mặt trước chỗ ngồi", ColumnIndex = 15 },
            new PartMappingData { Order = 14, PartName = "Ottoman Cover", PartNameVN = "Vải bọc tấm gỗ kê", ColumnIndex = 16 },
            new PartMappingData { Order = 15, PartName = "Small Ottoman 1", PartNameVN = "Tấm kê nhỏ", ColumnIndex = 17 },
            new PartMappingData { Order = 16, PartName = "Small Ottoman 2", PartNameVN = "Tấm kê nhỏ", ColumnIndex = 17 },
            new PartMappingData { Order = 17, PartName = "Arm Console Cover", PartNameVN = "Vỏ bọc tay ghế giữa", ColumnIndex = 18 },
            new PartMappingData { Order = 18, PartName = "No Sew (Fiber Arm)", PartNameVN = "Chi tiết Gòn Tay không may", ColumnIndex = 19 },
            new PartMappingData { Order = 19, PartName = "No Sew (Fiber Back)", PartNameVN = "Chi tiết Gòn Lưng không may", ColumnIndex = 20 },
            new PartMappingData { Order = 20, PartName = "No Sew (Fiber Seat)", PartNameVN = "Chi tiết Gòn Đáy không may", ColumnIndex = 21 },
            new PartMappingData { Order = 21, PartName = "No Sew (Fiber Seatband)", PartNameVN = "Chi tiết Gòn miếng ốp không may", ColumnIndex = 44 },
            new PartMappingData { Order = 22, PartName = "Fibercusshion", PartNameVN = "Chi tiết Gòn Nệm", ColumnIndex = 45 },
            new PartMappingData { Order = 23, PartName = "No Sew (Fibercusshion)", PartNameVN = "Chi tiết Gòn Nệm không may", ColumnIndex = 46 },
            new PartMappingData { Order = 24, PartName = "Black Bottom", PartNameVN = "Miếng dựng Đen", ColumnIndex = 22 },
            new PartMappingData { Order = 25, PartName = "Elite White UN Kit", PartNameVN = "Miếng dựng Trắng trong Kit", ColumnIndex = 23 },
            new PartMappingData { Order = 26, PartName = "Elite UPH", PartNameVN = "Miếng dựng Trắng UPH", ColumnIndex = 24 },
            new PartMappingData { Order = 27, PartName = "OE Cover", PartNameVN = "Chi tiết vải rời", ColumnIndex = 25 },
            new PartMappingData { Order = 28, PartName = "Handle (Strap)", PartNameVN = "Tay kéo có may da", ColumnIndex = 26 },
            new PartMappingData { Order = 29, PartName = "Typar UN Kit", PartNameVN = "Lót Typar UN KIT", ColumnIndex = 27 },
            // Typar UPH & COOL CLOTH và Typar B2 đã được xử lý riêng
            new PartMappingData { Order = 31, PartName = "Seat Hair", PartNameVN = "Gòn nén lót đáy", ColumnIndex = 31 },
            new PartMappingData { Order = 32, PartName = "Bottom Flap", PartNameVN = "Tấm chắn đáy ghế", ColumnIndex = 42 },
            new PartMappingData { Order = 33, PartName = "Seat Fiber", PartNameVN = "Gòn đáy ghế", ColumnIndex = 43 },
            new PartMappingData { Order = 34, PartName = "Pillow", PartNameVN = "Gối", ColumnIndex = 13 },
            new PartMappingData { Order = 35, PartName = "Pillow sack", PartNameVN = "Túi trắng gối", ColumnIndex = 37 },
            new PartMappingData { Order = 36, PartName = "Back (Fill)", PartNameVN = "Lưng ghế (Nhồi gòn)", ColumnIndex = 32 },
            new PartMappingData { Order = 37, PartName = "Back Console", PartNameVN = "Lưng ghề giữa", ColumnIndex = 33 },
            new PartMappingData { Order = 38, PartName = "Backsack", PartNameVN = "Túi trắng bao Lưng", ColumnIndex = 35 },
            new PartMappingData { Order = 39, PartName = "Left/Right Arm", PartNameVN = "Tay ghế trái/phải", ColumnIndex = 39 },
            new PartMappingData { Order = 40, PartName = "Arm console", PartNameVN = "Tay ghế giữa", ColumnIndex = 40 },
            new PartMappingData { Order = 41, PartName = "Arm sack", PartNameVN = "Túi trắng bao tay", ColumnIndex = 34 },
            new PartMappingData { Order = 42, PartName = "Cushion", PartNameVN = "Đệm ghế", ColumnIndex = 41 },
            new PartMappingData { Order = 43, PartName = "Cushion Sack", PartNameVN = "Túi trắng bao nệm", ColumnIndex = 38 },
        };
    }

    // ==================== HELPER: PHÂN LOẠI XE ====================
    string AssignVehicle(string partName)
    {
        var p = partName.ToLower();

        if (p.Contains("arm") || p.Contains("back") || p.Contains("panel") || p.Contains("flap"))
            return "Xe 1";

        if (p.Contains("pillow") || p.Contains("cushion") || p.Contains("seat") || p.Contains("ottoman"))
            return "Xe 2";

        if (p.Contains("fiber") || p.Contains("sack") || p.Contains("hair"))
            return "Xe 3";

        return "Xe 1";
    }

    // ==================== DASHBOARD API ENDPOINT ====================
    app.MapGet("/api/dashboard", async (string date, AppDbContext db) =>
    {
        try
        {
            if (string.IsNullOrEmpty(date)) return Results.BadRequest("Missing date");

            var orders = await db.Orders.Where(o => o.DateKey == date).ToListAsync();

            var odrnos = orders.Select(o => o.OdrNo).ToList();
            var allDetails = await db.MxDetails.Where(d => odrnos.Contains(d.OdrNo)).ToListAsync();

            var dashboardData = new List<object>();

            foreach (var order in orders)
            {
                var mxDetails = allDetails.Where(d => d.OdrNo == order.OdrNo).ToList();

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

                string xe1Status = order.Status == "Pending" ? "Pending" : "Received";
                string xe2Status = order.Status == "Pending" ? "Pending" : "Received";
                string xe3Status = order.Status == "Pending" ? "Pending" : "Received";

                string xe1Note = ""; string xe2Note = ""; string xe3Note = "";
                if (order.Status == "Lack" && !string.IsNullOrEmpty(order.Note))
                {
                    var lackItems = order.Note.Split('|', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var lack in lackItems)
                    {
                        var cleanLack = lack.Trim();
                        var partNameOnly = cleanLack.Split('(')[0].Trim();

                        string v = AssignVehicle(partNameOnly);
                        if (v == "Xe 1") { xe1Status = "Lack"; xe1Note += cleanLack + " "; }
                        if (v == "Xe 2") { xe2Status = "Lack"; xe2Note += cleanLack + " "; }
                        if (v == "Xe 3") { xe3Status = "Lack"; xe3Note += cleanLack + " "; }
                    }
                }

                if (xe1Parts.Count == 0) xe1Status = "N/A";
                if (xe2Parts.Count == 0) xe2Status = "N/A";
                if (xe3Parts.Count == 0) xe3Status = "N/A";

                dashboardData.Add(new
                {
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
        try
        {
            if (string.IsNullOrEmpty(req.Odrno) || string.IsNullOrEmpty(req.ZoneCode))
                return Results.BadRequest("Thiếu mã MX hoặc mã Ô");

            string mx = req.Odrno.ToUpper().Trim();
            string zone = req.ZoneCode.ToUpper().Trim();

            var existing = await db.Kho2_Inventory
                .FirstOrDefaultAsync(x => x.OdrNo == mx && x.Status == "In");

            if (existing != null)
            {
                string oldZone = existing.ZoneCode;
                existing.ZoneCode = zone;
                existing.UpdateTime = DateTime.Now;
                await db.SaveChangesAsync();
                return Results.Ok(new { message = $"🔄 Đã dời {mx} từ ô {oldZone} sang ô {zone}" });
            }
            else
            {
                db.Kho2_Inventory.Add(new Kho2_Inventory
                {
                    OdrNo = mx,
                    ZoneCode = zone,
                    InTime = DateTime.Now,
                    UpdateTime = DateTime.Now,
                    Status = "In"
                });
                await db.SaveChangesAsync();
                return Results.Ok(new { message = $"✅ Đã cất {mx} vào ô {zone}" });
            }
        }
        catch (Exception ex) { return Results.Problem(ex.Message); }
    });

    app.MapGet("/api/kits-inv/find", async (string odrno, AppDbContext db) =>
    {
        string mx = odrno.ToUpper().Trim();
        var item = await db.Kho2_Inventory
            .FirstOrDefaultAsync(x => x.OdrNo == mx && x.Status == "In");
        if (item == null) return Results.NotFound($"❌ MX {mx} không có trong Kho 2");
        return Results.Ok(item);
    });

    app.MapPost("/api/kits-inv/out", async (Kho2ScanRequest req, AppDbContext db) =>
    {
        string mx = req.Odrno.ToUpper().Trim();
        var item = await db.Kho2_Inventory
            .FirstOrDefaultAsync(x => x.OdrNo == mx && x.Status == "In");
        if (item == null) return Results.BadRequest("Không tìm thấy hàng trong kho");
        item.Status = "Out";
        item.OutTime = DateTime.Now;
        await db.SaveChangesAsync();
        return Results.Ok(new { message = $"📤 Đã xuất kho thành công {mx}" });
    });

    app.MapGet("/api/kits-inv/inventory", async (AppDbContext db) =>
    {
        var list = await db.Kho2_Inventory
            .Where(x => x.Status == "In")
            .OrderByDescending(x => x.UpdateTime)
            .ToListAsync();
        return Results.Ok(list);
    });

    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapHub<OrderHub>("/orderHub");
    app.MapHub<ScaleTestHub>("/scaleTestHub");

    async Task<List<(string MX, string FgItem, string MO, string FiberKit, string WC, string Ex, string PlannedQty, string Leadtime)>> LoadSchedulePlan(DateTime targetDate, AppDbContext db, CancellationToken token)
    {
        Console.WriteLine($"🔄 Loading schedule plan for {targetDate:yyyy-MM-dd}...");

        string? filePath = FileHelpers.FindLatestScheduleFile(targetDate, schedulePath);
        if (filePath == null)
        {
            Console.WriteLine($"⚠️ Không tìm thấy file kế hoạch cho ngày {targetDate:dd/MM/yyyy}");
            return new List<(string MX, string FgItem, string MO, string FiberKit, string WC, string Ex, string PlannedQty, string Leadtime)>();
        }

        Console.WriteLine($"🔍 Đang đọc file tracking mới nhất: {Path.GetFileName(filePath)}");

        // =================================================================
        // 1. ĐỌC KẾ HOẠCH TỪ EXCEL (LOGIC TỪ /manager-dashboard)
        // =================================================================
        var rawPlanData = new List<(string MX, string FgItem, string MO, string FiberKit, string WC, string Ex, string PlannedQty, string Leadtime)>();

        using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = ExcelReaderFactory.CreateReader(stream))
        {
            var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration() { ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = false } });
            foreach (DataTable table in dataSet.Tables)
            {
                string workCenterName = table.TableName;
                if (workCenterName.ToLower().Contains("pivot") || workCenterName.ToLower().Contains("summary")) continue;

                for (int i = 1; i < table.Rows.Count; i++)
                {
                    var row = table.Rows[i];
                    string mx = ExcelHelpers.GetCellValue(row, 1);
                    if (string.IsNullOrWhiteSpace(mx)) continue;

                    rawPlanData.Add((
                        MX: mx,
                        FgItem: ExcelHelpers.GetCellValue(row, 2),
                        MO: ExcelHelpers.GetCellValue(row, 5),
                        FiberKit: ExcelHelpers.GetCellValue(row, 6), // Cột G
                        WC: workCenterName,
                        Ex: ExcelHelpers.GetCellValue(row, 11),
                        PlannedQty: ExcelHelpers.GetCellValue(row, 8),
                        Leadtime: ExcelHelpers.GetCellValue(row, 12)
                    ));
                }
            }
        }

        // Gộp GLUE FOAM (giữ nguyên)
        try
        {
            string glueFolder = builder.Configuration["GlueLinePath"] ?? glueLinePath;
            string glueFileName = $"GLUE FOAM {targetDate:ddMMyyyy}.xlsx";
            string glueFilePath = Path.Combine(glueFolder, glueFileName);

            if (File.Exists(glueFilePath))
            {
                var existingPairs = rawPlanData.Select(d => (d.MO.Trim().ToUpper(), NormalizeWcForAs400(d.WC))).ToHashSet();
                using var stream = File.Open(glueFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = ExcelReaderFactory.CreateReader(stream);
                var glueDataSet = reader.AsDataSet(new ExcelDataSetConfiguration() { ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = false } });
                var glueTable = glueDataSet.Tables["GLUE"] ?? glueDataSet.Tables[0];

                for (int i = 2; i < glueTable.Rows.Count; i++)
                {
                    var row = glueTable.Rows[i];
                    string mo = ExcelHelpers.GetCellValue(row, 1);
                    string mx = ExcelHelpers.GetCellValue(row, 11);
                    string wc = ExcelHelpers.GetCellValue(row, 16);
                    if (string.IsNullOrWhiteSpace(mo) || string.IsNullOrWhiteSpace(mx) || string.IsNullOrWhiteSpace(wc)) continue;

                    var key = (MO: mo.Trim().ToUpper(), WCBase: NormalizeWcForAs400(wc));
                    if (existingPairs.Contains(key)) continue;

                    rawPlanData.Add((
                        MX: mx, FgItem: ExcelHelpers.GetCellValue(row, 12), MO: mo, FiberKit: "", WC: wc, Ex: "",
                        PlannedQty: ExcelHelpers.GetCellValue(row, 4), Leadtime: ExcelHelpers.GetCellValue(row, 23)
                    ));
                    existingPairs.Add(key);
                }
            }
        }
        catch (Exception exGlue) { Console.WriteLine($"❌ Lỗi khi đọc GLUE FOAM: {exGlue.Message}"); }

        // =================================================================
        // 2. CẬP NHẬT BẢNG MoPlans (LOGIC BẠN VỪA CẮT)
        // =================================================================
        try
        {
            var existingPlans = await db.MoPlans.Where(p => p.PlanDate.Date == targetDate.Date).ToListAsync(token);
            if (existingPlans.Any())
            {
                db.MoPlans.RemoveRange(existingPlans);
                await db.SaveChangesAsync(token);
            }

            var newPlans = rawPlanData
                .Where(d => !string.IsNullOrWhiteSpace(d.MO) && !string.IsNullOrWhiteSpace(d.WC))
                .Select(d => new MoPlan
                {
                    PlanDate = targetDate.Date,
                    MO = d.MO.Trim().ToUpper(),
                    WorkCenter = NormalizeWcForAs400(d.WC).Trim().ToUpper(),
                    FiberKit = (d.FiberKit ?? "").Trim().ToUpper(),
                    PlannedQty = int.TryParse(d.PlannedQty, out int p) ? p : 0
                }).ToList();

            if (newPlans.Any())
            {
                db.MoPlans.AddRange(newPlans);
                await db.SaveChangesAsync(token);
                Console.WriteLine($"✅ MoPlans: Đã lưu {newPlans.Count} dòng kế hoạch cho ngày {targetDate:yyyy-MM-dd}");
            }
        }
        catch (Exception exPlans) { Console.WriteLine($"❌ Lỗi cập nhật MoPlans: {exPlans.Message}"); }

        // =================================================================
        // 3. CẬP NHẬT BẢNG MoProgresses (LOGIC TỪ /tracking/journey)
        // =================================================================
        try
        {
            var existingProgress = await db.MoProgresses.Where(p => p.PlannedDate.Date == targetDate.Date).ToListAsync(token);
            var existingMap = existingProgress.ToDictionary(p => (p.MO.ToUpper(), p.WorkCenter.ToUpper()), p => p);

            var mergedPlan = rawPlanData
                .GroupBy(d => (MO: d.MO.Trim().ToUpper(), WC: NormalizeWcForAs400(d.WC).Trim().ToUpper()))
                .ToDictionary(
                    g => g.Key,
                    g => new {
                        PlannedQty = g.Sum(x => int.TryParse(x.PlannedQty, out int q) ? q : 0),
                        Leadtime = g.Last().Leadtime,
                        Mx = g.Last().MX
                    });

            foreach (var kvp in mergedPlan)
            {
                if (existingMap.TryGetValue(kvp.Key, out var mp))
                {
                    bool changed = mp.PlannedQty != kvp.Value.PlannedQty;
                    mp.PlannedQty = kvp.Value.PlannedQty;
                    mp.LeadtimeString = kvp.Value.Leadtime;
                    mp.MX = kvp.Value.Mx;
                    if (changed) mp.Status = AppHelpers.ComputeStatus(mp);
                }
                else
                {
                    db.MoProgresses.Add(new MoProgress
                    {
                        PlannedDate = targetDate, MO = kvp.Key.MO, MX = kvp.Value.Mx, WorkCenter = kvp.Key.WC,
                        PlannedQty = kvp.Value.PlannedQty, ActualQty = 0, Status = "pending", LeadtimeString = kvp.Value.Leadtime
                    });
                }
            }
            await db.SaveChangesAsync(token);
            Console.WriteLine($"✅ MoProgresses: Đã cập nhật cho ngày {targetDate:yyyy-MM-dd}");
        }
        catch (Exception ex) { Console.WriteLine($"❌ Lỗi cập nhật MoProgress: {ex.Message}"); }
        return rawPlanData;
    }

    app.MapGet("/api/tracking/journey", async (string date, AppDbContext db) =>
    {
        try
        {
            if (!DateTime.TryParse(date, out var targetDate))
                return Results.BadRequest("Invalid date format. Use yyyy-MM-dd.");

            // 1. Gọi hàm LoadSchedulePlan, nó vừa cập nhật DB, vừa trả về dữ liệu thô
            var rawPlanData = await LoadSchedulePlan(targetDate, db, CancellationToken.None);

            if (rawPlanData == null || !rawPlanData.Any())
            {
                return Results.Ok(new List<TrackingData>()); // Trả về mảng rỗng
            }

            // 2. Gom nhóm dữ liệu thô lại theo cấu trúc mà frontend cần
            //    (MX -> List<WorkCenterStep>)
            var result = rawPlanData
                .GroupBy(d => d.MX)
                .Select(g => new TrackingData(
                    g.Key, // Đây là MX
                    g.Select(step => new WorkCenterStep(
                        step.MX,
                        step.WC, // ✅ DÙNG TÊN WC GỐC TỪ EXCEL
                        step.FgItem,
                        step.MO,
                        step.PlannedQty,
                        step.Leadtime
                    )).ToList()
                ))
                .OrderBy(t => t.Mx)
                .ToList();

            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Lỗi API Tracking: {ex.Message}");
            return Results.Problem(ex.Message);
        }
    });

    // // ==================== TRACKING API ====================
    // app.MapGet("/api/tracking/journey", async (string date, AppDbContext db) =>
    // {
    //     try
    //     {
    //         DateTime targetDate;
    //         if (!DateTime.TryParse(date, out targetDate))
    //             return Results.BadRequest("Invalid date format. Use yyyy-MM-dd.");

    //         // ------- 1. Đọc file kế hoạch chính -------
    //         string? filePath = FileHelpers.FindLatestScheduleFile(targetDate, schedulePath);

    //         if (filePath == null)
    //         {
    //             return Results.NotFound($"Không tìm thấy file kế hoạch nào cho ngày {targetDate:dd/MM/yyyy}.");
    //         }

    //         Console.WriteLine($"🔍 Đang đọc file tracking mới nhất: {Path.GetFileName(filePath)}");

    //         // dataByMx: MX -> list WorkCenterStep
    //         var dataByMx = new Dictionary<string, List<WorkCenterStep>>();

    //         using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    //         using (var reader = ExcelReaderFactory.CreateReader(stream))
    //         {
    //             var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration()
    //             {
    //                 ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = false }
    //             });

    //             foreach (DataTable table in dataSet.Tables)
    //             {
    //                 string workCenterName = table.TableName;
    //                 if (workCenterName.ToLower().Contains("pivot") ||
    //                     workCenterName.ToLower().Contains("summary"))
    //                     continue;

    //                 for (int i = 1; i < table.Rows.Count; i++)
    //                 {
    //                     var row = table.Rows[i];
    //                     string mx = ExcelHelpers.GetCellValue(row, 1);
    //                     if (string.IsNullOrWhiteSpace(mx)) continue;
    //                     string fg_item = ExcelHelpers.GetCellValue(row, 2);
    //                     string mo = ExcelHelpers.GetCellValue(row, 5);
    //                     string fiberKit = ExcelHelpers.GetCellValue(row, 6);
    //                     string qty = ExcelHelpers.GetCellValue(row, 8);
    //                     string leadtime = ExcelHelpers.GetCellValue(row, 12);

    //                     if (!dataByMx.ContainsKey(mx))
    //                         dataByMx[mx] = new List<WorkCenterStep>();

    //                     var alreadyExists = dataByMx[mx].Any(step =>
    //                         step.WorkCenter == workCenterName &&
    //                         step.Mo == mo &&
    //                         step.Qty == qty &&
    //                         step.Leadtime == leadtime);

    //                     if (!alreadyExists)
    //                     {
    //                         dataByMx[mx].Add(new WorkCenterStep(mx, workCenterName, fg_item, mo, qty, leadtime));
    //                     }
    //                 }
    //             }
    //         }

    //         // ------- 2. Đọc & gộp kế hoạch từ GLUE FOAM -------
    //         try
    //         {
    //             string glueFolder = builder.Configuration["GlueLinePath"] ?? @"V:\UPH Support\Public\B2\Data\nhung\LEADTIME B2\GLUE LINE";
    //             string glueFileName = $"GLUE FOAM {targetDate:ddMMyyyy}.xlsx";
    //             string glueFilePath = Path.Combine(glueFolder, glueFileName);

    //             if (File.Exists(glueFilePath))
    //             {
    //                 Console.WriteLine($"🔗 Đang đọc thêm kế hoạch GLUE FOAM: {glueFileName}");

    //                 // Tập (MO, WC gốc) đã có sẵn từ kế hoạch chính -> để ưu tiên file chính
    //                 var existingPlanKeys = new HashSet<(string MO, string WCBase)>();
    //                 foreach (var kv in dataByMx)
    //                 {
    //                     foreach (var step in kv.Value)
    //                     {
    //                         var baseWc = NormalizeWcForAs400(step.WorkCenter);
    //                         existingPlanKeys.Add((step.Mo.Trim().ToUpper(), baseWc));
    //                     }
    //                 }

    //                 using (var stream = File.Open(glueFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    //                 using (var reader = ExcelReaderFactory.CreateReader(stream))
    //                 {
    //                     var glueDataSet = reader.AsDataSet(new ExcelDataSetConfiguration()
    //                     {
    //                         ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = false }
    //                     });

    //                     var glueTable = glueDataSet.Tables["GLUE"] ?? glueDataSet.Tables[0];

    //                     for (int i = 2; i < glueTable.Rows.Count; i++)
    //                     {
    //                         var row = glueTable.Rows[i];

    //                         string mo = ExcelHelpers.GetCellValue(row, 1);   // B
    //                         string mx = ExcelHelpers.GetCellValue(row, 11);  // L
    //                         string wc = ExcelHelpers.GetCellValue(row, 16);  // Q
    //                         if (string.IsNullOrWhiteSpace(mo) ||
    //                             string.IsNullOrWhiteSpace(mx) ||
    //                             string.IsNullOrWhiteSpace(wc))
    //                             continue;

    //                         string fgItem   = ExcelHelpers.GetCellValue(row, 12); // M
    //                         string qty      = ExcelHelpers.GetCellValue(row, 4);  // E
    //                         string leadtime = ExcelHelpers.GetCellValue(row, 23); // X

    //                         string baseWc = NormalizeWcForAs400(wc);
    //                         var key = (MO: mo.Trim().ToUpper(), WCBase: baseWc);

    //                         // ƯU TIÊN file kế hoạch chính: nếu đã có (MO, WC gốc) thì bỏ qua GLUE
    //                         if (existingPlanKeys.Contains(key))
    //                             continue;

    //                         if (!dataByMx.ContainsKey(mx))
    //                             dataByMx[mx] = new List<WorkCenterStep>();

    //                         bool existsInGlue = dataByMx[mx].Any(step =>
    //                             NormalizeWcForAs400(step.WorkCenter) == baseWc &&
    //                             step.Mo.Equals(mo, StringComparison.OrdinalIgnoreCase) &&
    //                             step.Qty == qty &&
    //                             step.Leadtime == leadtime);

    //                         if (!existsInGlue)
    //                         {
    //                             dataByMx[mx].Add(new WorkCenterStep(mx, wc, fgItem, mo, qty, leadtime));
    //                             existingPlanKeys.Add(key);
    //                         }
    //                     }
    //                 }

    //                 Console.WriteLine("✅ Đã gộp kế hoạch GLUE FOAM vào Tracking Journey.");
    //             }
    //             else
    //             {
    //                 Console.WriteLine($"⚠️ Không tìm thấy file GLUE FOAM: {glueFileName}");
    //             }
    //         }
    //         catch (Exception exGlue)
    //         {
    //             Console.WriteLine($"❌ Lỗi khi đọc GLUE FOAM: {exGlue.Message}");
    //         }

    //         // ------- 3. Đóng gói kết quả cho frontend -------
    //         var result = dataByMx
    //             .Select(kvp => new TrackingData(kvp.Key, kvp.Value))
    //             .OrderBy(t => t.Mx)
    //             .ToList();

    //         Console.WriteLine($"✅ Đã xử lý xong {result.Count} mã MX.");

    //         // ------- 4. TẠO/CẬP NHẬT MoProgress THEO CẶP (MO, WC GỐC) -------
    //         try
    //         {
    //             var allSteps = result
    //                 .SelectMany(r => r.Steps.Select(s => new
    //                 {
    //                     Mx = r.Mx,
    //                     WcBase = NormalizeWcForAs400(s.WorkCenter),
    //                     Step = s
    //                 }))
    //                 .ToList();

    //             var existing = await db.MoProgresses.ToListAsync();
    //             var existingMap = existing
    //                 .GroupBy(p => (MO: p.MO.ToUpper(), WC: p.WorkCenter.ToUpper()))
    //                 .ToDictionary(
    //                     g => g.Key,
    //                     g => g.OrderByDescending(x => x.Id).First() // Nếu trùng, lấy dòng có Id lớn nhất
    //                 );

    //             // 1. Gom PlannedQty và thông tin khác theo (MO, WC GỐC) trước
    //             var mergedPlan = allSteps
    //                 .GroupBy(item => (MO: item.Step.Mo.Trim().ToUpper(), WC: item.WcBase.Trim().ToUpper()))
    //                 .ToDictionary(
    //                     g => g.Key,
    //                     g => new
    //                     {
    //                         PlannedQty = g.Sum(x => int.TryParse(x.Step.Qty, out int q) ? q : 0),
    //                         Leadtime = g.Last().Step.Leadtime,
    //                         Mx = g.Last().Mx
    //                     }
    //                 );

    //             var addedCount = 0;
    //             var updatedCount = 0;

    //             // 2. Lặp qua kế hoạch đã gom và so sánh với DB
    //             foreach (var kvp in mergedPlan)
    //             {
    //                 var key = kvp.Key;
    //                 var plan = kvp.Value;

    //                 if (existingMap.TryGetValue(key, out var mp))
    //                 {
    //                     // Đã tồn tại -> Cập nhật
    //                     bool plannedQtyChanged = mp.PlannedQty != plan.PlannedQty;
    //                     mp.PlannedDate = targetDate;
    //                     mp.PlannedQty = plan.PlannedQty;
    //                     mp.LeadtimeString = plan.Leadtime;
    //                     mp.MX = plan.Mx;

    //                     // TÍNH LẠI STATUS NẾU PlannedQty THAY ĐỔI
    //                     if (plannedQtyChanged)
    //                     {
    //                         mp.Status = AppHelpers.ComputeStatus(mp);
    //                     }
    //                     updatedCount++;
    //                 }
    //                 else
    //                 {
    //                     // Chưa tồn tại -> Thêm mới
    //                     var newMp = new MoProgress
    //                     {
    //                         PlannedDate = targetDate,
    //                         MO = key.MO,
    //                         MX = plan.Mx,
    //                         WorkCenter = key.WC,
    //                         PlannedQty = plan.PlannedQty,
    //                         ActualQty = 0,
    //                         Status = "pending",
    //                         LeadtimeString = plan.Leadtime
    //                     };
    //                     db.MoProgresses.Add(newMp);
    //                     existingMap[key] = newMp;
    //                     addedCount++;
    //                 }
    //             }

    //             if (addedCount > 0 || updatedCount > 0)
    //             {
    //                 await db.SaveChangesAsync();
    //                 Console.WriteLine($"✅ MoProgress Sync: Thêm {addedCount}, Cập nhật {updatedCount} (MO + WC gốc).");
    //             }
    //         }
    //         catch (Exception ex)
    //         {
    //             Console.WriteLine($"❌ Lỗi khi cập nhật MoProgress: {ex.Message}");
    //         }

    //         return Results.Ok(result);
    //     }
    //     catch (Exception ex)
    //     {
    //         Console.WriteLine($"❌ Lỗi đọc file Tracking: {ex.Message}");
    //         return Results.Problem(ex.Message);
    //     }
    // });

    // ==================== API DEBUG: LẤY DANH SÁCH WORK CENTER ====================
    app.MapGet("/api/debug/workcenters", (string date) =>
    {
        try
        {
            DateTime targetDate;
            if (!DateTime.TryParse(date, out targetDate))
                return Results.BadRequest("Invalid date format. Use yyyy-MM-dd.");

            string? filePath = FileHelpers.FindLatestScheduleFile(targetDate, schedulePath);

            if (filePath == null)
            {
                return Results.NotFound($"Không tìm thấy file kế hoạch nào cho ngày {targetDate:dd/MM/yyyy}.");
            }
            string fileName = Path.GetFileName(filePath); // Lấy tên file thực tế để hiển thị

            var workCenterNames = new List<string>();

            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = ExcelReaderFactory.CreateReader(stream))
            {
                var dataSet = reader.AsDataSet();

                foreach (DataTable table in dataSet.Tables)
                {
                    string workCenterName = table.TableName;
                    if (workCenterName.ToLower().Contains("pivot") ||
                        workCenterName.ToLower().Contains("summary"))
                        continue;

                    workCenterNames.Add(workCenterName);
                }
            }

            workCenterNames.Sort();
            return Results.Ok(new
            {
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
            var progressList = await db.MoProgresses.ToListAsync();
            var progressData = progressList.Select(p => new
            {
                mo = p.MO,
                mx = p.MX,
                workCenter = p.WorkCenter, // Giữ lại WC chi tiết
                baseWorkCenter = NormalizeWcForAs400(p.WorkCenter), 
                plannedQty = p.PlannedQty,
                currentQty = p.ActualQty,
                leadtime = p.LeadtimeString,
                status = p.Status,
                progress = $"{p.ActualQty}/{p.PlannedQty}"
            }).ToList();

            return Results.Ok(progressData);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    // ==================== API LẤY CHI TIẾT QUÉT CỦA 1 MO ====================
    app.MapGet("/api/tracking/mo-scan-detail", async (string mo, string workCenter, AppDbContext db) =>
    {
        try
        {
            if (string.IsNullOrEmpty(mo) || string.IsNullOrEmpty(workCenter)) 
                return Results.BadRequest("Missing MO or WorkCenter");

            // Chuẩn hóa WC chi tiết từ frontend thành WC gốc
            string baseWc = NormalizeWcForAs400(workCenter);

            // Lấy tất cả log của MO đó
            var logsForMo = await db.ScanLogs
                .Where(s => s.MO == mo)
                .ToListAsync();

            // Lọc trong memory theo WC gốc
            var scans = logsForMo
                .Where(s => NormalizeWcForAs400(s.WorkCenter) == baseWc)
                .OrderBy(s => s.ScanTime)
                .ToList();

            int totalScannedQty = scans.Sum(s => s.Qty);

            return Results.Ok(new { 
                mo = mo, 
                scans = scans,
                totalScannedQty = totalScannedQty
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
            using var conn = new OdbcConnection("DSN=WFVNPROD;UID=WNKRND;PWD=wnkrnd@112;");
            await conn.OpenAsync();

            string sql = "SELECT TRIM(ODORDR) AS ODORDR, TRIM(ODPN) AS ODPN, ODQTYC, TRIM(ODWKCN) AS ODWKCN, CHAR(ODTSTP) AS ODTSTP " +
                        "FROM WWDCF.GRPORDH FETCH FIRST 10 ROWS ONLY";

            using var cmd = new OdbcCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();

            var list = new List<object>();
            while (await reader.ReadAsync())
            {
                list.Add(new
                {
                    mo = reader.GetString(0),
                    item = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    qty = reader.IsDBNull(2) ? 0 : (int)reader.GetDecimal(3),
                    wc = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    ts = reader.IsDBNull(4) ? "" : reader.GetString(4)
                });
            }

            return Results.Ok(list);
        }
        catch (Exception ex)
        {
            return Results.Problem(new ProblemDetails
            {
                Title = "Lỗi kết nối AS/400",
                Detail = ex.ToString(),
                Status = 500
            });
        }
    });

    // ===== API TEST WAL MODE =====
    app.MapGet("/api/test-wal", async (AppDbContext db) =>
    {
        try
        {
            var result = await db.Database.SqlQueryRaw<string>("PRAGMA journal_mode").ToListAsync();
            var mode = result.FirstOrDefault() ?? "unknown";
            
            return Results.Ok(new { 
                journalMode = mode,
                isWalEnabled = mode.Equals("wal", StringComparison.OrdinalIgnoreCase),
                message = mode == "wal" ? "✅ WAL mode is enabled!" : "❌ WAL mode is NOT enabled"
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    // ==================== API DEBUG: LẤY DỮ LIỆU THÔ TỪ AS/400 CHO 1 MO ====================
    app.MapGet("/api/debug/mo-scan-raw/{mo}", async (string mo, AppDbContext db) =>
    {
        try
        {
            var moUpper = mo.ToUpper();

            var localMoProgress = await db.MoProgresses
                .FirstOrDefaultAsync(p => p.MO == moUpper);
            var isInSchedule = localMoProgress != null;

            var localScanLogs = await db.ScanLogs
                .Where(l => l.MO == moUpper)
                .ToListAsync();

            var as400RawScans = new List<object>();
            using (var conn = new OdbcConnection("DSN=WFVNPROD;UID=WNKRND;PWD=wnkrnd@112;TRANSLATE=1;"))
            {
                await conn.OpenAsync();

                string sql = $@"
                    SELECT 
                        TRIM(A.ODORDR) AS ODORDR, TRIM(B.REFNO) AS MX_REFNO,
                        TRIM(A.ODPN) AS ODPN, A.ODQTYC,
                        TRIM(A.ODWKCN) AS ODWKCN, A.ODTSTP
                    FROM WWDCF.GRPORDH A
                    LEFT JOIN AMFLIBW.MOMAST B ON A.ODORDR = B.ORDNO
                    WHERE TRIM(A.ODORDR) = ? 
                    AND A.ODWKCN IN ('UPGL1','UPGL2','UPGL3','UPGL4','UCFCM','UCFHS','UCFCS','UCFCO','UCFCH')
                    AND B.OSTAT NOT IN ('99')
                    AND SUBSTR(B.REFNO, 1, 2) = 'MX'
                    ORDER BY A.ODTSTP";

                using var cmd = new OdbcCommand(sql, conn);
                cmd.Parameters.Add("?", OdbcType.VarChar).Value = moUpper;

                using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    as400RawScans.Add(new
                    {
                        mo = reader.GetString(0),
                        mx = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        item = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        qty = reader.IsDBNull(3) ? 0 : (int)reader.GetDecimal(3),
                        wc = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        scanTime = reader.GetDateTime(5)
                    });
                }
            }

            return Results.Ok(new
            {
                moSearched = moUpper,
                isInSchedule = isInSchedule,
                as400RawScans = as400RawScans,
                localScanLogs = localScanLogs,
                localMoProgress = localMoProgress
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.ToString());
        }
    });

    // ==================== API DEBUG: ĐỒNG BỘ LỊCH SỬ QUÉT CHO CÁC MO CHƯA CÓ DỮ LIỆU (TỐI ƯU) ====================
    app.MapPost("/api/debug/sync-historical", async (string date, AppDbContext db) =>
    {
        try
        {
            if (!DateTime.TryParse(date, out var targetDate))
            {
                return Results.BadRequest("Invalid date format. Use yyyy-MM-dd.");
            }

            Console.WriteLine($"\n🔄 SYNC HISTORICAL (OPTIMIZED): Bắt đầu đồng bộ lịch sử quét cho ngày {targetDate:dd/MM/yyyy}...");

            var BYPASS_WCS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "UBF05", "UPHD1", "UBF04", "UCFBP", "WLGL2"
            };

            // 1. Lấy danh sách MoProgress ứng viên
            var candidatesFromDb = await db.MoProgresses
                .Where(mp => mp.PlannedDate.Date == targetDate.Date &&
                            mp.PlannedQty > 0 &&
                            mp.ActualQty == 0)
                .ToListAsync();
            
            var candidates = candidatesFromDb
                .Where(mp => !BYPASS_WCS.Contains(NormalizeWcForAs400(mp.WorkCenter)))
                .ToList();

            if (!candidates.Any())
            {
                return Results.Ok(new { message = "Không có MO nào cần đồng bộ lịch sử." });
            }

            // 2. Gom các MO cần quét theo từng Work Center gốc
            var moGroupsByWc = candidates
                .GroupBy(mp => NormalizeWcForAs400(mp.WorkCenter))
                .ToDictionary(g => g.Key, g => g.Select(mp => mp.MO).Distinct().ToList());

            Console.WriteLine($"📋 Sync Historical: Tìm thấy {moGroupsByWc.Count} Work Center có MO cần kiểm tra.");

            int totalUpdated = 0;
            int totalInsertedLogs = 0;
            
            // Thời gian bắt đầu quét lịch sử (2 ngày trước)
            var historyStartDate = DateTime.Now.Date.AddDays(-2);

            using (var conn = new OdbcConnection("DSN=WFVNPROD;UID=WNKRND;PWD=wnkrnd@112;TRANSLATE=1;"))
            {
                await conn.OpenAsync();

                // 3. Lặp qua từng Work Center để query một lần duy nhất
                foreach (var group in moGroupsByWc)
                {
                    string baseWc = group.Key;
                    var moList = group.Value;

                    Console.WriteLine($"\n🔄 Đang xử lý WC: {baseWc} với {moList.Count} MO...");

                    var as400Rows = new List<ScanLog>();
                    
                    // Tạo câu lệnh IN cho danh sách MO
                    var moInClause = string.Join(",", moList.Select(mo => $"'{mo.Trim().ToUpper()}'"));
                    
                    string sql = $@"
                        SELECT TRIM(A.ODORDR), TRIM(A.ODPN), A.ODQTYC, TRIM(A.ODWKCN), A.ODTSTP
                        FROM WWDCF.GRPORDH A
                        LEFT JOIN AMFLIBW.MOMAST B ON A.ODORDR = B.ORDNO
                        WHERE TRIM(A.ODORDR) IN ({moInClause}) 
                        AND TRIM(A.ODWKCN) = ?
                        AND A.ODTSTP >= ?
                        AND B.OSTAT NOT IN ('99') AND SUBSTR(B.REFNO, 1, 2) = 'MX'";

                    using var cmd = new OdbcCommand(sql, conn);
                    cmd.Parameters.Add("?", OdbcType.VarChar).Value = baseWc;

                    string timestampString = historyStartDate.ToString("yyyy-MM-dd-HH.mm.ss.ffffff");
                    cmd.Parameters.Add("?", OdbcType.VarChar).Value = timestampString;

                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        as400Rows.Add(new ScanLog {
                            MO = reader.GetString(0),
                            Item = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            Qty = reader.IsDBNull(2) ? 0 : (int)reader.GetDecimal(2),
                            WorkCenter = reader.IsDBNull(3) ? "" : reader.GetString(3),
                            ScanTime = reader.GetDateTime(4),
                            Source = "AS400_HISTORICAL"
                        });
                    }

                    if (!as400Rows.Any()) continue;

                    // 4. Ghi tất cả log tìm được vào DB
                    int insertedCount = 0;
                    foreach (var row in as400Rows)
                    {
                        bool exists = await db.ScanLogs.AnyAsync(l => l.MO == row.MO && l.ScanTime == row.ScanTime && l.WorkCenter == row.WorkCenter);
                        if (!exists)
                        {
                            db.ScanLogs.Add(row);
                            insertedCount++;
                        }
                    }
                    if (insertedCount > 0) await db.SaveChangesAsync();
                    totalInsertedLogs += insertedCount;

                    // 5. Cập nhật lại tất cả MoProgress của các MO trong nhóm này
                    var moScans = as400Rows.GroupBy(r => r.MO);
                    foreach (var moGroup in moScans)
                    {
                        string currentMo = moGroup.Key;
                        var relatedMp = candidates.Where(mp => mp.MO == currentMo && NormalizeWcForAs400(mp.WorkCenter) == baseWc).ToList();
                        
                        if (relatedMp.Any())
                        {
                            int totalQty = moGroup.Sum(s => s.Qty);
                            var lastScan = moGroup.OrderByDescending(s => s.ScanTime).FirstOrDefault();

                            foreach (var mp in relatedMp)
                            {
                                mp.ActualQty = totalQty;
                                mp.LastScanTime = lastScan?.ScanTime;
                                mp.Status = AppHelpers.ComputeStatus(mp);
                            }
                            totalUpdated++;
                        }
                    }
                }
            }
            
            await db.SaveChangesAsync();
            Console.WriteLine($"✅ Sync Historical: Đã cập nhật {totalUpdated} cặp MO/WC, chèn {totalInsertedLogs} log mới.");
            return Results.Ok(new { updatedPairs = totalUpdated, newLogs = totalInsertedLogs });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.ToString());
        }
    });

    // ==================== API DEBUG: XEM BẢNG MoProgresses ====================
    app.MapGet("/api/debug/mo-progress", async (AppDbContext db) =>
    {
        var allProgress = await db.MoProgresses
            .OrderBy(p => p.WorkCenter)
            .ThenBy(p => p.MO)
            .ToListAsync();
        return Results.Ok(allProgress);
    });

    // ==================== API DEBUG: BACKFILL DỮ LIỆU SCAN CHO 1 MO + 1 WC ====================
    app.MapPost("/api/debug/backfill-mo", async (string mo, string workCenter, AppDbContext db) =>
    {
        try
        {
            if (string.IsNullOrWhiteSpace(mo) || string.IsNullOrWhiteSpace(workCenter))
                return Results.BadRequest("Thiếu MO hoặc WorkCenter");

            string moUpper = mo.Trim().ToUpper();
            string baseWc = NormalizeWcForAs400(workCenter);

            Console.WriteLine($"\n🔄 BACKFILL MO={moUpper}, WC={workCenter} (base={baseWc})");

            // 1. Lấy dữ liệu thô từ AS400 cho đúng MO + WC gốc
            var as400Rows = new List<(string MO, string MX, string Item, string Wc, int Qty, DateTime ScanTime)>();

            using (var conn = new OdbcConnection("DSN=WFVNPROD;UID=WNKRND;PWD=wnkrnd@112;TRANSLATE=1;"))
            {
                await conn.OpenAsync();

                string sql = @"
                    SELECT 
                        TRIM(A.ODORDR) AS ODORDR, TRIM(B.REFNO) AS MX_REFNO,
                        TRIM(A.ODPN)   AS ODPN,   A.ODQTYC,
                        TRIM(A.ODWKCN) AS ODWKCN, A.ODTSTP
                    FROM WWDCF.GRPORDH A
                    LEFT JOIN AMFLIBW.MOMAST B ON A.ODORDR = B.ORDNO
                    WHERE TRIM(A.ODORDR) = ? 
                    AND TRIM(A.ODWKCN) = ?
                    AND B.OSTAT NOT IN ('99')
                    AND SUBSTR(B.REFNO, 1, 2) = 'MX'
                    ORDER BY A.ODTSTP";

                using var cmd = new OdbcCommand(sql, conn);
                cmd.Parameters.Add("?", OdbcType.VarChar).Value = moUpper;
                cmd.Parameters.Add("?", OdbcType.VarChar).Value = baseWc;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    string moVal = reader.GetString(0);
                    string mx    = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    string item  = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    int qty      = reader.IsDBNull(3) ? 0  : (int)reader.GetDecimal(3);
                    string wc    = reader.IsDBNull(4) ? "" : reader.GetString(4);
                    DateTime ts  = reader.GetDateTime(5);

                    as400Rows.Add((moVal, mx, item, wc, qty, ts));
                }
            }

            if (!as400Rows.Any())
            {
                Console.WriteLine("⚠️ Backfill: Không tìm thấy bản ghi nào trên AS400 cho MO + WC này.");
                return Results.Ok(new
                {
                    mo = moUpper,
                    workCenter = workCenter,
                    message = "Không có dữ liệu scan trên AS400 cho MO + WC này",
                    insertedLogs = 0,
                    updatedMoProgress = false
                });
            }

            Console.WriteLine($"✅ Backfill: tìm được {as400Rows.Count} bản ghi từ AS400.");

            // 2. Ghi vào bảng ScanLogs nếu chưa có
            int insertedCount = 0;
            foreach (var row in as400Rows)
            {
                bool exists = await db.ScanLogs.AnyAsync(l =>
                    l.MO == row.MO &&
                    l.ScanTime == row.ScanTime &&
                    l.WorkCenter == row.Wc);

                if (!exists)
                {
                    db.ScanLogs.Add(new ScanLog
                    {
                        MO = row.MO,
                        Item = row.Item,
                        WorkCenter = row.Wc,
                        Qty = row.Qty,
                        ScanTime = row.ScanTime,
                        Source = "AS400_BACKFILL"
                    });
                    insertedCount++;
                }
            }
            await db.SaveChangesAsync();
            Console.WriteLine($"📝 Backfill: đã chèn thêm {insertedCount} dòng vào ScanLogs.");

            // 3. Tính lại tổng qty & last scan cho MO + WC gốc này
            var allLogsForMo = await db.ScanLogs
                .Where(s => s.MO == moUpper)
                .ToListAsync();

            var logsForMoAndBaseWc = allLogsForMo
                .Where(s => NormalizeWcForAs400(s.WorkCenter) == baseWc)
                .OrderBy(s => s.ScanTime)
                .ToList();

            int totalQty = logsForMoAndBaseWc.Sum(s => s.Qty);
            DateTime? lastScanTime = logsForMoAndBaseWc
                .OrderByDescending(s => s.ScanTime)
                .Select(s => (DateTime?)s.ScanTime)
                .FirstOrDefault();

            Console.WriteLine($"📊 Backfill: tổng Qty = {totalQty}, lastScanTime = {lastScanTime}");

            // 4. Cập nhật MoProgress cho MO + WC gốc
            var allMpForMo = await db.MoProgresses
                .Where(m => m.MO == moUpper)
                .ToListAsync();

            var relatedMp = allMpForMo
                .Where(m => NormalizeWcForAs400(m.WorkCenter) == baseWc)
                .ToList();

            bool createdNewMp = false;

            if (!relatedMp.Any())
            {
                // Nếu chưa có MoProgress cho MO + WC này -> tạo mới
                var firstRow = as400Rows.First();
                var newMp = new MoProgress
                {
                    PlannedDate = DateTime.Now.Date,
                    MO = moUpper,
                    MX = firstRow.MX,
                    WorkCenter = baseWc,
                    PlannedQty = 0,
                    ActualQty = totalQty,
                    LastScanTime = lastScanTime,
                    Status = "pending",
                    LeadtimeString = ""
                };
                db.MoProgresses.Add(newMp);
                await db.SaveChangesAsync();
                relatedMp.Add(newMp);
                createdNewMp = true;

                Console.WriteLine($"➕ Backfill: tạo mới MoProgress cho MO={moUpper}, WC={baseWc}.");
            }

            foreach (var mp in relatedMp)
            {
                mp.ActualQty = totalQty;
                mp.LastScanTime = lastScanTime;
                if (mp.PlannedQty > 0)
                {
                    mp.Status = AppHelpers.ComputeStatus(mp);
                }
            }

            if (relatedMp.Any())
                await db.SaveChangesAsync();

            Console.WriteLine($"✅ Backfill: đã cập nhật MoProgress ({relatedMp.Count} dòng).");

            return Results.Ok(new
            {
                mo = moUpper,
                workCenter = workCenter,
                baseWorkCenter = baseWc,
                as400Rows = as400Rows.Count,
                insertedLogs = insertedCount,
                totalScannedQty = totalQty,
                lastScanTime = lastScanTime,
                updatedMoProgress = relatedMp.Any(),
                createdNewMoProgress = createdNewMp
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Backfill error: {ex}");
            return Results.Problem(ex.ToString());
        }
    });

    // POST /api/blow-fill/log-step
    app.MapPost("/api/blow-fill/log-step", async (WeighLogRequest req, BlowFillDbContext blowDb) =>
    {
        try
        {
            var log = new WeighLog
            {
                MachineId    = req.MachineId ?? "",
                Timestamp    = DateTime.Now,
                WorkCenter   = req.WorkCenter ?? "",
                MO           = req.MO ?? "",
                FiberKit     = req.FiberKit ?? "",
                StepNumber   = req.StepNumber,
                TargetWeight = req.TargetWeight,
                ActualWeight = req.ActualWeight,
                Tolerance    = req.Tolerance,
                Status       = req.Status ?? "",
                Operator     = req.OperatorName ?? ""
            };

            blowDb.WeighLogs.Add(log);
            await blowDb.SaveChangesAsync();

            return Results.Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    // // API: nhận weight từ BlowFillClient và broadcast SignalR theo MachineId
    // app.MapPost("/api/blow-fill/push-weight", async (BlowFillPushRequest req, IHubContext<OrderHub> hub) =>
    // {
    //     try
    //     {
    //         Console.WriteLine($"[API] Received weight {req.Weight} from MachineId '{req.MachineId}'");

    //         // Chỉ gửi số cân (weight) vào group
    //         await hub.Clients.Group(req.MachineId).SendAsync("ReceiveScaleData", req.Weight);

    //         return Results.Ok(new { success = true });
    //     }
    //     catch (Exception ex)
    //     {
    //         Console.WriteLine($"❌ Error in push-weight: {ex.Message}");
    //         return Results.Problem(ex.Message);
    //     }
    // });

    // GET /api/blow-fill/logs?date=2026-07-02
    app.MapGet("/api/blow-fill/logs", async (string? date, string? machineId, BlowFillDbContext blowDb, AppDbContext appDb) =>
    {
        try
        {
            DateTime targetDate = DateTime.Today;
            if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, out var d))
                targetDate = d.Date;

            await LoadSchedulePlan(targetDate, appDb, CancellationToken.None);

            var nextDay = targetDate.AddDays(1);

            // 1. Lấy dữ liệu kế hoạch từ DB chính (giữ nguyên)
            var plansForDate = await appDb.MoPlans
                .Where(p => p.PlanDate.Date == targetDate.Date)
                .ToListAsync();
            
            var planLookup = plansForDate
                .GroupBy(p => (p.MO, p.WorkCenter))
                .ToDictionary(g => g.Key, g => g.Sum(p => p.PlannedQty));

            // 2. Lấy dữ liệu log cân từ DB BlowFill, lọc theo máy nếu có
            var logsQuery = blowDb.WeighLogs
                .Where(w => w.Timestamp >= targetDate && w.Timestamp < nextDay);

            if (!string.IsNullOrWhiteSpace(machineId))
            {
                string m = machineId.Trim().ToUpper();
                logsQuery = logsQuery.Where(w => w.MachineId.ToUpper() == m);
            }

            var logs = await logsQuery
                .OrderBy(w => w.Timestamp)
                .ToListAsync();

            // 3. Tính toán summary, kết hợp dữ liệu từ 2 nguồn
            var summary = logs
                .GroupBy(w => new { w.MachineId, w.WorkCenter, w.MO, w.FiberKit })
                .Select(g => {
                    // Tính số lượng hoàn thành
                    var maxStepNumber = g.Any() ? g.Max(x => x.StepNumber) : 0;
                    var completedQty = g.Count(x => x.StepNumber == maxStepNumber && x.Status == "OK");

                    // Lấy số lượng kế hoạch từ lookup
                    planLookup.TryGetValue((g.Key.MO, NormalizeWcForAs400(g.Key.WorkCenter)), out int plannedQty);

                    return new
                    {
                        machineId = g.Key.MachineId,
                        workCenter = g.Key.WorkCenter,
                        mo = g.Key.MO,
                        fiberKit = g.Key.FiberKit,
                        completedQty = completedQty,
                        plannedQty = plannedQty, // ✅ THÊM SỐ KẾ HOẠCH
                        underCount = g.Count(x => x.Status == "UNDER"),
                        overCount = g.Count(x => x.Status == "OVER"),
                        lastTime = g.Max(x => x.Timestamp).ToString("HH:mm:ss")
                    };
                })
                .OrderBy(x => x.lastTime)
                .ToList();

            return Results.Ok(new { logs, summary, date = targetDate.ToString("yyyy-MM-dd") });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    // ==================== MANAGER DASHBOARD API ====================
    app.MapGet("/api/manager-dashboard", async (string date, AppDbContext db) =>
    {
        try
        {
            // ✅ Danh sách WorkCenter sẽ bị bỏ qua khi tính trạng thái group
            var BYPASS_WCS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "UBF05", "UBF04", "UPHD1", "UCFBP","WLGL2"
            };

            DateTime targetDate;
            if (!DateTime.TryParse(date, out targetDate))
                return Results.BadRequest("Invalid date format. Use yyyy-MM-dd.");

            string? filePath = FileHelpers.FindLatestScheduleFile(targetDate, schedulePath);

            if (filePath == null)
            {
                return Results.NotFound($"Không tìm thấy file kế hoạch nào cho ngày {targetDate:dd/MM/yyyy}.");
            }

            // 1. Định nghĩa các nhóm Work Center
            var wcGroups = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Blow Fill"] = new HashSet<string> { "UBF03_M", "UBF03_S", "UBF05", "UBF06", "UBF12", "UBF13", "UBF13_PREP" },
                ["Glueline"] = new HashSet<string> { "UPGL1", "UPGL3", "UPGL4" },
                ["HandGlue"] = new HashSet<string> { "UPGL2", "UPGL2_I", "UPGL2_II", "UPGL2_III", "UPGL2_IV", "UPGL2_REP", "WLGL2", "UFGL2", "UFGL2_I", "UPGL6", "UPHD1" },
                ["Handfill"] = new HashSet<string> { "UCFHM", "UCFHM_1", "UCFHS", "UCFBF", "UBF04", "UCFBP" },
                ["Cushion"]  = new HashSet<string> { "UCFCT", "UCFCM", "UCFCS", "UCFCH", "UCFCO", "UCFCV" }
            };

            // 2. Đọc dữ liệu thô từ file kế hoạch chính
            var rawPlanData = new List<(string MX, string FgItem, string MO, string FiberKit, string WC, string Ex, string PlannedQty, string Leadtime)>();

            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = ExcelReaderFactory.CreateReader(stream))
            {
                var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration()
                {
                    ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = false }
                });

                foreach (DataTable table in dataSet.Tables)
                {
                    string workCenterName = table.TableName;
                    if (workCenterName.ToLower().Contains("pivot") || workCenterName.ToLower().Contains("summary"))
                        continue;

                    for (int i = 1; i < table.Rows.Count; i++)
                    {
                        var row = table.Rows[i];
                        string mx = ExcelHelpers.GetCellValue(row, 1);
                        if (string.IsNullOrWhiteSpace(mx)) continue;

                        string fgItem   = ExcelHelpers.GetCellValue(row, 2);
                        string mo       = ExcelHelpers.GetCellValue(row, 5);
                        string fiberKit = ExcelHelpers.GetCellValue(row, 6);   // SUB-ITEM (cột G)
                        string qty      = ExcelHelpers.GetCellValue(row, 8);
                        string ex       = ExcelHelpers.GetCellValue(row, 11);
                        string leadtime = ExcelHelpers.GetCellValue(row, 12);

                        rawPlanData.Add((
                            MX: mx,
                            FgItem: fgItem,
                            MO: mo,
                            FiberKit: fiberKit,
                            WC: workCenterName,
                            Ex: ex,
                            PlannedQty: qty,
                            Leadtime: leadtime
                        ));
                    }
                }
            }

            // 2b. Bổ sung kế hoạch GLUE FOAM (ưu tiên kế hoạch chính)
            try
            {
                string glueFolder = builder.Configuration["GlueLinePath"] ?? glueLinePath;
                string glueFileName = $"GLUE FOAM {targetDate:ddMMyyyy}.xlsx";
                string glueFilePath = Path.Combine(glueFolder, glueFileName);

                if (File.Exists(glueFilePath))
                {
                    Console.WriteLine($"🔗 ManagerDashboard: đọc thêm kế hoạch GLUE: {glueFileName}");

                    // Các cặp (MO, WC gốc) đã có trong rawPlanData từ file chính
                    var existingPairs = new HashSet<(string MO, string WCBase)>();
                    foreach (var d in rawPlanData)
                    {
                        string baseWc = NormalizeWcForAs400(d.WC);
                        existingPairs.Add((d.MO.Trim().ToUpper(), baseWc));
                    }

                    using (var stream = File.Open(glueFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = ExcelReaderFactory.CreateReader(stream))
                    {
                        var glueDataSet = reader.AsDataSet(new ExcelDataSetConfiguration()
                        {
                            ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = false }
                        });

                        var glueTable = glueDataSet.Tables["GLUE"] ?? glueDataSet.Tables[0];

                        for (int i = 2; i < glueTable.Rows.Count; i++)
                        {
                            var row = glueTable.Rows[i];

                            string mo = ExcelHelpers.GetCellValue(row, 1);   // B
                            string mx = ExcelHelpers.GetCellValue(row, 11);  // L
                            string wc = ExcelHelpers.GetCellValue(row, 16);  // Q
                            if (string.IsNullOrWhiteSpace(mo) ||
                                string.IsNullOrWhiteSpace(mx) ||
                                string.IsNullOrWhiteSpace(wc))
                                continue;

                            string fgItem   = ExcelHelpers.GetCellValue(row, 12); // M
                            string fiberKit = "";                                 // GLUE FOAM không dùng FiberKit cho Blow Fill
                            string qty      = ExcelHelpers.GetCellValue(row, 4);  // E
                            string ex       = "";                                 // file GLUE không có Ex -> để trống
                            string leadtime = ExcelHelpers.GetCellValue(row, 23); // X

                            string baseWc = NormalizeWcForAs400(wc);
                            var key = (MO: mo.Trim().ToUpper(), WCBase: baseWc);

                            // Ưu tiên kế hoạch chính
                            if (existingPairs.Contains(key))
                                continue;

                            rawPlanData.Add((
                                MX: mx,
                                FgItem: fgItem,
                                MO: mo,
                                FiberKit: fiberKit,
                                WC: wc,
                                Ex: ex,
                                PlannedQty: qty,
                                Leadtime: leadtime
                            ));

                            existingPairs.Add(key);
                        }
                    }

                    Console.WriteLine("✅ ManagerDashboard: đã gộp GLUE FOAM vào rawPlanData.");
                }
                else
                {
                    Console.WriteLine($"⚠️ ManagerDashboard: không tìm thấy GLUE FOAM {glueFileName}");
                }
            }
            catch (Exception exGlue)
            {
                Console.WriteLine($"❌ ManagerDashboard: lỗi khi đọc GLUE FOAM: {exGlue.Message}");
            }

            // 3. Gom dữ liệu theo MX
            var dataByMx = rawPlanData
                .GroupBy(d => d.MX)
                .Select(g => new
                {
                    MX = g.Key,
                    FgItem = g.FirstOrDefault().FgItem,
                    ExValue = g.Select(d => d.Ex)
                            .FirstOrDefault(ex => !string.IsNullOrWhiteSpace(ex) && int.TryParse(ex, out _)) ?? "0",
                    MOs = g.ToList()
                })
                .ToList();

            // 4. Lấy tiến độ từ DB
            var allMoKeys = rawPlanData.Select(d => d.MO).Distinct().ToList();
            var moProgresses = await db.MoProgresses
                .Where(p => allMoKeys.Contains(p.MO))
                .GroupBy(p => new { p.MO, p.WorkCenter })
                .ToDictionaryAsync(
                    g => (g.Key.MO, g.Key.WorkCenter),
                    g => g
                        .OrderByDescending(x => x.LastScanTime ?? DateTime.MinValue)
                        .ThenByDescending(x => x.Id) // fallback
                        .First()
                );

            // 5. Xử lý và trả về kết quả
            var result = dataByMx.Select(mxData =>
            {
                int.TryParse(mxData.ExValue, out int exHour);
                int ltUphSp = exHour - 3;
                bool isPastDeadline = DateTime.Now.Hour >= ltUphSp;

                var groupStatus = new Dictionary<string, object>();
                foreach (var group in wcGroups)
                {
                    string groupName = group.Key;
                    var wcsInGroup = group.Value;

                    var mosInGroup = mxData.MOs.Where(m => wcsInGroup.Contains(m.WC)).ToList();
                    if (!mosInGroup.Any())
                    {
                        groupStatus[groupName] = new { status = "na", tooltip = "Không có MO", details = new List<object>() };
                        continue;
                    }

                    var details = mosInGroup.Select(m =>
                    {
                        var baseWc = NormalizeWcForAs400(m.WC);
                        moProgresses.TryGetValue((m.MO, baseWc), out var progress);

                        return new
                        {
                            mo = m.MO,
                            wc = m.WC,
                            baseWc = baseWc,
                            plannedQty = m.PlannedQty,
                            progress = $"{progress?.ActualQty ?? 0}/{m.PlannedQty}",
                            status = progress?.Status ?? "pending",
                            leadtime = m.Leadtime
                        };
                    }).ToList();

                    // ✅ Chỉ dùng những WC KHÔNG thuộc BYPASS_WCS để tính trạng thái group
                    var detailsForStatus = details
                        .Where(d => !BYPASS_WCS.Contains(d.baseWc))
                        .ToList();

                    string statusGroup;
                    string tooltip;

                    if (!detailsForStatus.Any())
                    {
                        // Nếu group này CHỈ toàn WC bypass -> coi như "na" (không đánh giá)
                        statusGroup = "na";
                        tooltip = "Chỉ có WC bypass (không tính trạng thái)";
                    }
                    else
                    {
                        bool allDone = detailsForStatus.All(d => d.status == "done" || d.status == "late");

                        if (allDone)
                            statusGroup = "green";
                        else if (isPastDeadline)
                            statusGroup = "red";
                        else
                            statusGroup = "pending";

                        var completedCount = detailsForStatus.Count(d => d.status == "done" || d.status == "late");
                        tooltip = $"{completedCount} / {detailsForStatus.Count} MOs hoàn thành (không tính WC bypass)";
                    }

                    groupStatus[groupName] = new
                    {
                        status = statusGroup,
                        tooltip,
                        details = details.Select(d => new
                        {
                            d.mo,
                            d.wc,
                            d.progress,
                            d.status,
                            d.leadtime
                        }).ToList()
                    };
                }

                string overallStatus = "Pending";
                if (groupStatus.Values.Any(s => ((dynamic)s).status == "red")) overallStatus = "Alert";
                else if (groupStatus.Values.Any(s => ((dynamic)s).status == "green")) overallStatus = "In Progress";
                if (groupStatus.Values.All(s => ((dynamic)s).status == "green" || ((dynamic)s).status == "na")) overallStatus = "Done";

                return new
                {
                    mx = mxData.MX,
                    fgItem = mxData.FgItem,
                    ex = exHour,
                    ltUphSp = ltUphSp,
                    groups = groupStatus,
                    status = overallStatus
                };
            })
            .OrderBy(item => item.ex)
            .ToList();

            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.ToString());
        }
    });

    // ==================== WEB ROUTES ====================
    app.MapGet("/manager-dashboard", async ctx => {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync("wwwroot/manager-dashboard.html");
    });

    app.MapGet("/", async ctx =>
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync("wwwroot/index.html");
    });

    app.MapGet("/wip-wnk3", async ctx =>
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync("wwwroot/wip-wnk3.html");
    });

    app.MapGet("/kits-inv", async ctx =>
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync("wwwroot/kits-inv.html");
    });

    app.MapGet("/tracking", async ctx =>
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync("wwwroot/tracking.html");
    });

    app.MapGet("/dashboard", async ctx =>
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync("wwwroot/dashboard.html");
    });

    app.MapGet("/wc-details", async ctx =>
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync("wwwroot/wc-details.html");
    });

    // Placeholder pages
    app.MapGet("/kho1", async ctx =>
    {
        await ctx.Response.WriteAsync("<h1>KHO 1 - Dang xay dung...</h1><a href='/'>Quay lai</a>");
    });
    app.MapGet("/kho3", async ctx =>
    {
        await ctx.Response.WriteAsync("<h1>KHO 3 - Dang xay dung...</h1><a href='/'>Quay lai</a>");
    });
    app.MapGet("/assemble", async ctx =>
    {
        await ctx.Response.WriteAsync("<h1>ASSEMBLE - Dang xay dung...</h1><a href='/'>Quay lai</a>");
    });

    app.MapGet("/health", async ctx =>
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.SendFileAsync("wwwroot/health.html");
    });

    // ==================== CNC GO ROUTES ====================
    app.MapGet("/cnc-go", async ctx =>
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync("wwwroot/cnc-go.html");
    });

    app.MapGet("/cnc-go-dashboard", async ctx =>
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync("wwwroot/cnc-go-dashboard.html");
    });

    app.MapGet("/cnc-go-history", async ctx =>
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync("wwwroot/cnc-go-history.html");
    });

    app.MapGet("/cnc-go-supplier-report", async ctx =>
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync("wwwroot/cnc-go-supplier-report.html");
    });

    // ==================== TOOL MANAGEMENT API ENDPOINTS ====================
    // GET: Lấy danh sách lịch sử thay dao (có filter)
    app.MapGet("/api/tools/history", async (
        string? machine, 
        string? shift, 
        DateTime? fromDate, 
        DateTime? toDate,
        string? reason,
        string? toolType,
        ToolManagementDbContext db) =>
    {
        try
        {
            var query = db.ToolChanges.AsQueryable();
            
            if (!string.IsNullOrEmpty(machine))
                query = query.Where(t => t.MachineName == machine);
            
            if (!string.IsNullOrEmpty(shift))
                query = query.Where(t => t.Shift == shift);
            
            if (fromDate.HasValue)
                query = query.Where(t => t.Date >= fromDate.Value);
            
            if (toDate.HasValue)
                query = query.Where(t => t.Date <= toDate.Value);
            
            if (!string.IsNullOrEmpty(reason))
                query = query.Where(t => t.Reason == reason);
            
            if (!string.IsNullOrEmpty(toolType))
                query = query.Where(t => t.ToolType == toolType);
            
            var results = await query
                .OrderByDescending(t => t.Date)
                .ThenByDescending(t => t.ReplaceTime)
                .ToListAsync();
            
            return Results.Ok(results);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    // GET: Lấy trạng thái dao hiện tại của tất cả máy
    app.MapGet("/api/tools/status", async (ToolManagementDbContext db) =>
    {
        try
        {
            var statuses = await db.ToolStatuses
                .OrderBy(t => t.MachineName)
                .ThenBy(t => t.Shift)
                .ThenBy(t => t.ToolPosition)
                .ToListAsync();
            
            return Results.Ok(statuses);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    // GET: Lấy trạng thái dao của 1 máy cụ thể
    app.MapGet("/api/tools/status/{machine}/{shift}", async (
        string machine, 
        string shift, 
        ToolManagementDbContext db) =>
    {
        try
        {
            var statuses = await db.ToolStatuses
                .Where(t => t.MachineName == machine && t.Shift == shift)
                .OrderBy(t => t.ToolPosition)
                .ToListAsync();
            
            return Results.Ok(statuses);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    // POST: Thêm bản ghi thay dao mới
    app.MapPost("/api/tools/change", async (ToolChangeRequest req, ToolManagementDbContext db, IHubContext<OrderHub> hubContext) =>
    {
        try
        {
            // Validate
            if (string.IsNullOrEmpty(req.MachineName) || string.IsNullOrEmpty(req.Shift))
                return Results.BadRequest("Thiếu thông tin máy hoặc ca làm việc");
            
            if (req.ToolPosition < 1 || req.ToolPosition > 4)
                return Results.BadRequest("Vị trí dao phải từ 1-4");

            // Parse ngày giờ
            DateTime date = DateTime.TryParse(req.Date, out var d) ? d : DateTime.Today;
            DateTime? installDate = DateTime.TryParse(req.InstallDate, out var id) ? id : null;
            DateTime? replaceDate = DateTime.TryParse(req.ReplaceDate, out var rd) ? rd : null;
            TimeSpan? installTime = TimeSpan.TryParse(req.InstallTime, out var it) ? it : null;
            TimeSpan? replaceTime = TimeSpan.TryParse(req.ReplaceTime, out var rt) ? rt : null;

            // ✅ LOGIC MỚI: XÁC ĐỊNH CÓ TĂNG VERSION HAY KHÔNG
            bool shouldIncrementVersion = req.Reason != "Cuối ca thay";

            // Lấy status hiện tại
            var currentStatus = await db.ToolStatuses
                .FirstOrDefaultAsync(t => 
                    t.MachineName == req.MachineName && 
                    t.Shift == req.Shift && 
                    t.ToolPosition == req.ToolPosition);
            
            int newVersion;
            int newCurrentVersionHours;

            if (shouldIncrementVersion)
            {
                // ✅ DAO HƯ → TĂNG VERSION, RESET GIỜ CHẠY
                newVersion = (currentStatus?.CurrentVersion ?? 0) + 1;
                newCurrentVersionHours = req.ActualHours; // Giờ chạy của lần lắp mới
                Console.WriteLine($"🔧 DAO HƯ ({req.Reason}) → Tăng Version lên {newVersion}, Reset giờ chạy về {req.ActualHours}h");
            }
            else
            {
                // ❌ CUỐI CA THAY → GIỮ NGUYÊN VERSION, CỘNG DỒN GIỜ
                newVersion = currentStatus?.CurrentVersion ?? 1; // Giữ nguyên version
                int previousHours = currentStatus?.CurrentVersionHours ?? 0;
                newCurrentVersionHours = previousHours + req.ActualHours;
                Console.WriteLine($"📦 CUỐI CA THAY → Giữ nguyên Version {newVersion}, Cộng dồn giờ: {previousHours}h + {req.ActualHours}h = {newCurrentVersionHours}h");
            }
            
            // Tạo bản ghi thay dao
            var toolChange = new ToolChange
            {
                Shift       = req.Shift,
                Supervisor  = req.Supervisor ?? "",
                MSS         = req.MSS ?? "",
                Date        = date,
                MachineName = req.MachineName,
                ToolPosition= req.ToolPosition,
                ToolVersion = newVersion,
                ToolType    = req.ToolType ?? "MỚI",
                InstallDate = installDate,
                InstallTime = installTime,
                ReplaceDate = replaceDate,
                ReplaceTime = replaceTime,
                ActualHours = req.ActualHours,
                Reason      = req.Reason ?? "",
                Material    = req.Material ?? "PLYWOOD",
                Supplier = req.Supplier ?? "",
                IsVersionIncrement = shouldIncrementVersion,
                CreatedAt   = DateTime.Now,
                UpdatedAt   = DateTime.Now
            };
            
            db.ToolChanges.Add(toolChange);
            
            // Cập nhật hoặc tạo ToolStatus
            if (currentStatus != null)
            {
                currentStatus.CurrentVersion = newVersion;
                currentStatus.CurrentVersionHours = newCurrentVersionHours;
                currentStatus.LastUpdated = DateTime.Now;
            }
            else
            {
                db.ToolStatuses.Add(new ToolStatus
                {
                    MachineName  = req.MachineName,
                    Shift        = req.Shift,
                    ToolPosition = req.ToolPosition,
                    CurrentVersion = newVersion,
                    CurrentVersionHours = newCurrentVersionHours,
                    LastUpdated  = DateTime.Now
                });
            }
            
            await db.SaveChangesAsync();
            
            // Broadcast update qua SignalR
            await hubContext.Clients.All.SendAsync("ToolStatusUpdated", new
            {
                machine   = req.MachineName,
                shift     = req.Shift,
                position  = req.ToolPosition,
                version   = newVersion,
                hours     = newCurrentVersionHours
            });
            
            return Results.Ok(new { 
                success = true, 
                version = newVersion,
                currentVersionHours = newCurrentVersionHours,
                message = shouldIncrementVersion 
                    ? $"✅ Dao hư → Version tăng lên {newVersion}, Reset giờ về {req.ActualHours}h"
                    : $"✅ Cuối ca thay → Version giữ nguyên {newVersion}, Cộng dồn giờ: {newCurrentVersionHours}h"
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    // PUT: Cập nhật bản ghi thay dao
    app.MapPut("/api/tools/change/{id}", async (int id, ToolChangeRequest req,ToolManagementDbContext db) =>
    {
        try
        {
            var toolChange = await db.ToolChanges.FindAsync(id);
            if (toolChange == null)
                return Results.NotFound("Không tìm thấy bản ghi");
            
            toolChange.Supervisor = req.Supervisor ?? toolChange.Supervisor;
            toolChange.MSS = req.MSS ?? toolChange.MSS;
            toolChange.ToolType = req.ToolType ?? toolChange.ToolType;
            // toolChange.InstallDate = req.InstallDate ?? toolChange.InstallDate;
            // toolChange.InstallTime = req.InstallTime ?? toolChange.InstallTime;
            // toolChange.ReplaceDate = req.ReplaceDate ?? toolChange.ReplaceDate;
            // toolChange.ReplaceTime = req.ReplaceTime ?? toolChange.ReplaceTime;
            toolChange.Reason = req.Reason ?? toolChange.Reason;
            toolChange.Material = req.Material ?? toolChange.Material;
            
            // Cập nhật ActualHours và TotalHours nếu thay đổi
            if (req.ActualHours != toolChange.ActualHours)
            {
                int diff = req.ActualHours - toolChange.ActualHours;
                toolChange.ActualHours = req.ActualHours;
                
                var status = await db.ToolStatuses.FirstOrDefaultAsync(t =>
                    t.MachineName == toolChange.MachineName &&
                    t.Shift == toolChange.Shift &&
                    t.ToolPosition == toolChange.ToolPosition);
                
                if (status != null)
                {
                    status.TotalHours += diff;
                    status.LastUpdated = DateTime.Now;
                }
            }
            
            toolChange.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync();
            
            return Results.Ok(new { success = true, message = "✅ Đã cập nhật thành công" });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    // DELETE: Xóa bản ghi thay dao
    app.MapDelete("/api/tools/change/{id}", async (int id, ToolManagementDbContext db) =>
    {
        try
        {
            var toolChange = await db.ToolChanges.FindAsync(id);
            if (toolChange == null)
                return Results.NotFound("Không tìm thấy bản ghi");
            
            // Cập nhật lại TotalHours
            var status = await db.ToolStatuses.FirstOrDefaultAsync(t =>
                t.MachineName == toolChange.MachineName &&
                t.Shift == toolChange.Shift &&
                t.ToolPosition == toolChange.ToolPosition);
            
            if (status != null)
            {
                status.TotalHours -= toolChange.ActualHours;
                if (status.TotalHours < 0) status.TotalHours = 0;
            }
            
            db.ToolChanges.Remove(toolChange);
            await db.SaveChangesAsync();
            
            return Results.Ok(new { success = true, message = "✅ Đã xóa thành công" });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    // GET: Export Excel lịch sử thay dao
    app.MapGet("/api/tools/export", async (
        string? machine,
        string? shift,
        DateTime? fromDate,
        DateTime? toDate,
        ToolManagementDbContext db) =>
    {
        try
        {
            var query = db.ToolChanges.AsQueryable();
            
            if (!string.IsNullOrEmpty(machine))
                query = query.Where(t => t.MachineName == machine);
            
            if (!string.IsNullOrEmpty(shift))
                query = query.Where(t => t.Shift == shift);
            
            if (fromDate.HasValue)
                query = query.Where(t => t.Date >= fromDate.Value);
            
            if (toDate.HasValue)
                query = query.Where(t => t.Date <= toDate.Value);
            
            var data = await query
                .OrderByDescending(t => t.Date)
                .ThenByDescending(t => t.ReplaceTime)
                .ToListAsync();
            
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Tool Changes");
            
            // Header
            worksheet.Cell(1, 1).Value = "Ca";
            worksheet.Cell(1, 2).Value = "Supervisor";
            worksheet.Cell(1, 3).Value = "MSS";
            worksheet.Cell(1, 4).Value = "Máy";
            worksheet.Cell(1, 5).Value = "Vị trí dao";
            worksheet.Cell(1, 6).Value = "Version";
            worksheet.Cell(1, 7).Value = "Loại dao";
            worksheet.Cell(1, 8).Value = "Ngày lắp";
            worksheet.Cell(1, 9).Value = "Giờ lắp";
            worksheet.Cell(1, 10).Value = "Ngày thay";
            worksheet.Cell(1, 11).Value = "Giờ thay";
            worksheet.Cell(1, 12).Value = "Giờ thực tế";
            worksheet.Cell(1, 13).Value = "Lý do thay";
            worksheet.Cell(1, 14).Value = "Nguyên liệu";
            
            // Data
            int row = 2;
            foreach (var item in data)
            {
                worksheet.Cell(row, 1).Value = item.Shift;
                worksheet.Cell(row, 2).Value = item.Supervisor;
                worksheet.Cell(row, 3).Value = item.MSS;
                worksheet.Cell(row, 4).Value = item.MachineName;
                worksheet.Cell(row, 5).Value = item.ToolPosition;
                worksheet.Cell(row, 6).Value = item.ToolVersion;
                worksheet.Cell(row, 7).Value = item.ToolType;
                worksheet.Cell(row, 8).Value = item.InstallDate?.ToString("dd/MM/yyyy") ?? "";
                worksheet.Cell(row, 9).Value = item.InstallTime?.ToString(@"hh\:mm") ?? "";
                worksheet.Cell(row, 10).Value = item.ReplaceDate?.ToString("dd/MM/yyyy") ?? "";
                worksheet.Cell(row, 11).Value = item.ReplaceTime?.ToString(@"hh\:mm") ?? "";
                worksheet.Cell(row, 12).Value = item.ActualHours;
                worksheet.Cell(row, 13).Value = item.Reason;
                worksheet.Cell(row, 14).Value = item.Material;
                row++;
            }
            
            worksheet.Columns().AdjustToContents();
            
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var content = stream.ToArray();
            
            return Results.File(
                content,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"Tool_Changes_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
            );
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    // ==================== API BÁO CÁO HIỆU SUẤT SUPPLIER ====================
    app.MapGet("/api/tools/supplier-performance", async (ToolManagementDbContext db) =>
    {
        try
        {
            var performanceData = await db.ToolChanges
                // Chỉ lấy các bản ghi có thông tin Supplier và là dao đã mài
                .Where(t => t.Supplier != "" && (t.ToolType == "MÀI LẦN 1" || t.ToolType == "MÀI LẦN 2"))
                .GroupBy(t => new { t.Supplier, t.ToolType }) // Gom nhóm theo Supplier và Loại dao
                .Select(g => new
                {
                    Supplier = g.Key.Supplier,
                    ToolType = g.Key.ToolType,
                    AverageHours = g.Average(x => x.ActualHours), // Tính giờ chạy trung bình
                    TotalChanges = g.Count() // Đếm số lần thay để biết độ tin cậy
                })
                .OrderBy(r => r.Supplier).ThenBy(r => r.ToolType)
                .ToListAsync();
                
            return Results.Ok(performanceData);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    app.MapGet("/ban-khung-go", async ctx =>
    {
        await ctx.Response.WriteAsync("<h1>BAN KHUNG GO - Dang xay dung...</h1><a href='/'>Quay lai</a>");
    });

    // ==================== BLOW-FILL WEB APP ====================
    app.MapGet("/blow-fill", async ctx =>
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync("wwwroot/blow-fill.html");
    });

    // API: Load Excel data
    app.MapGet("/api/blow-fill/load-excel", async () =>
    {
        try
        {
            var excelPath = @"Data\Định mức gòn.xlsb";
            if (!File.Exists(excelPath))
            {
                return Results.NotFound(new { error = "Excel file not found" });
            }

            Console.WriteLine($"📂 Loading Excel with robust logic: {excelPath}");

            using var stream = File.Open(excelPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            
            // SỬ DỤNG AsDataSet để đọc tất cả dữ liệu thô
            var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
            {
                ConfigureDataTable = (_) => new ExcelDataTableConfiguration
                {
                    UseHeaderRow = false // Đọc cả dòng header để tự xử lý
                }
            });

            if (dataSet.Tables.Count == 0) return Results.BadRequest(new { error = "No sheets found" });

            var table = dataSet.Tables[0];
            var productDatabase = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

            // Tìm dòng bắt đầu của dữ liệu thực tế (bỏ qua các dòng tiêu đề)
            int startRow = 0;
            for (int i = 0; i < table.Rows.Count; i++)
            {
                // Giả sử cột C (index 2) là "Fiber Kit"
                if (table.Rows[i][2]?.ToString()?.Trim().Equals("Fiber Kit", StringComparison.OrdinalIgnoreCase) ?? false)
                {
                    startRow = i + 1;
                    break;
                }
            }

            if (startRow == 0) return Results.BadRequest(new { error = "Could not find header row 'Fiber Kit'" });
            
            List<object>? currentPartSteps = null;
            string currentFiberKit = "";
            string currentDescription = "";

            const int FIBER_KIT_COL = 2;
            const int DESCRIPTION_COL = 5;
            const int INDIVIDUAL_WEIGHT_COL = 16;
            const int CUMULATIVE_WEIGHT_COL = 17;

            for (int i = startRow; i < table.Rows.Count; i++)
            {
                var row = table.Rows[i];
                string fiberKit = row[FIBER_KIT_COL]?.ToString()?.Trim() ?? "";
                string description = row[DESCRIPTION_COL]?.ToString()?.Trim() ?? "";

                if (string.IsNullOrEmpty(fiberKit)) continue;

                // Phát hiện Part mới khi Fiber Kit hoặc Description thay đổi
                if (fiberKit.ToUpper() != currentFiberKit.ToUpper() || description != currentDescription)
                {
                    // Lưu part cũ nếu có
                    if (currentPartSteps != null && currentPartSteps.Any())
                    {
                        if (!productDatabase.ContainsKey(currentFiberKit))
                        {
                            productDatabase[currentFiberKit] = new List<object>();
                        }
                        productDatabase[currentFiberKit].Add(new {
                            description = currentDescription,
                            steps = currentPartSteps
                        });
                    }

                    // Bắt đầu Part mới
                    currentFiberKit = fiberKit;
                    currentDescription = description;
                    currentPartSteps = new List<object>();
                }

                // Thêm các step vào part hiện tại
                if (currentPartSteps != null)
                {
                    var cumulativeCell = row[CUMULATIVE_WEIGHT_COL];
                    var individualCell = row[INDIVIDUAL_WEIGHT_COL];

                    if (cumulativeCell != null && double.TryParse(cumulativeCell.ToString(), out double cumulativeWeight) && cumulativeWeight > 0)
                    {
                        currentPartSteps.Add(new { name = $"Step {currentPartSteps.Count + 1}", target_weight = cumulativeWeight });
                    }
                    else if (currentPartSteps.Count == 0 && individualCell != null && double.TryParse(individualCell.ToString(), out double individualWeight) && individualWeight > 0)
                    {
                        currentPartSteps.Add(new { name = "Step 1", target_weight = individualWeight });
                    }
                }
            }

            // Lưu part cuối cùng trong file
            if (currentPartSteps != null && currentPartSteps.Any())
            {
                if (!productDatabase.ContainsKey(currentFiberKit))
                {
                    productDatabase[currentFiberKit] = new List<object>();
                }
                productDatabase[currentFiberKit].Add(new {
                    description = currentDescription,
                    steps = currentPartSteps
                });
            }

            Console.WriteLine($"✅ Loaded {productDatabase.Count} Fiber Kits with robust logic.");
            return Results.Ok(new { success = true, products = productDatabase });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error loading Excel: {ex.Message}");
            return Results.Problem(ex.Message);
        }
    });
    
    // GET /api/blow-fill/plan?mo=123456
    app.MapGet("/api/blow-fill/plan", async (string mo, AppDbContext db) =>
    {
        try
        {
            if (string.IsNullOrWhiteSpace(mo))
                return Results.BadRequest("Missing mo");

            string moUpper = mo.Trim().ToUpper();

            // ✅ Danh sách WC Blow Fill (chuẩn hóa)
            var blowFillWCs = new List<string> 
            { 
                "UBF03", "UBF05", "UBF06", "UBF12", "UBF13" 
            };

            // ✅ Tìm kế hoạch trong TẤT CẢ các WC Blow Fill
            var plan = await db.MoPlans
                .Where(p => blowFillWCs.Contains(p.WorkCenter) && p.MO == moUpper)
                .OrderByDescending(p => p.PlanDate)
                .FirstOrDefaultAsync();

            if (plan == null)
            {
                return Results.Ok(new { 
                    found = false, 
                    message = "MO này không có trong kế hoạch Blow Fill hôm nay." 
                });
            }

            return Results.Ok(new
            {
                found = true,
                mo = plan.MO,
                workCenter = plan.WorkCenter, // ✅ Trả về WC tìm được
                fiberKit = plan.FiberKit,
                plannedQty = plan.PlannedQty,
                planDate = plan.PlanDate.ToString("yyyy-MM-dd")
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    // API: Search product
    app.MapGet("/api/blow-fill/search", (string fiberKit) =>
    {
        try
        {
            // This would search in cached data
            // For now, return sample
            return Results.Ok(new 
            { 
                found = true,
                fiberKit = fiberKit,
                steps = new[] 
                {
                    new { name = "Step 1", target_weight = 2.5, is_single_step = true }
                },
                tolerance = 0.05
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    // GET /api/blow-fill/dashboard-data?date=2026-07-03
    app.MapGet("/api/blow-fill/dashboard-data", async (string? date, BlowFillDbContext blowDb, AppDbContext appDb) =>
    {
        try
        {
            DateTime targetDate = DateTime.Today;
            if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, out var d))
                targetDate = d.Date;

            var nextDay = targetDate.AddDays(1);

            // 1. Lấy kế hoạch MoPlans trong ngày (để biết PlannedQty theo MO)
            var plansForDate = await appDb.MoPlans
                .Where(p => p.PlanDate.Date == targetDate.Date)
                .ToListAsync();

            // Map MO -> PlannedQty (ở Blow Fill). 
            // Nếu một MO có nhiều dòng WC, bạn có thể cộng lại hoặc chỉ lấy WC Blow Fill tùy nhu cầu.
            var plannedByMo = plansForDate
                .GroupBy(p => p.MO)
                .ToDictionary(
                    g => g.Key,
                    g => g.Sum(x => x.PlannedQty)  // tổng số kit kế hoạch cho MO
                );

            // 2. Lấy log cân trong ngày
            var logs = await blowDb.WeighLogs
                .Where(w => w.Timestamp >= targetDate && w.Timestamp < nextDay)
                .OrderBy(w => w.Timestamp)
                .ToListAsync();

            if (!logs.Any())
            {
                return Results.Ok(new { isEmpty = true, date = targetDate.ToString("yyyy-MM-dd") });
            }

            // 3. Tính theo từng máy (M1..M8)
            var perMachine = logs
                .GroupBy(w => w.MachineId)
                .Select(g =>
                {
                    string machineId = g.Key;

                    // Đếm kit & fiber theo từng MO trên máy này
                    var kitsPerMo = g
                        .GroupBy(x => x.MO)
                        .Select(moGroup =>
                        {
                            string mo = moGroup.Key;
                            int maxStepForMo = moGroup.Max(x => x.StepNumber);

                            var okFinalStepsForMo = moGroup
                                .Where(x => x.StepNumber == maxStepForMo && x.Status == "OK")
                                .ToList();

                            int kitsOk = okFinalStepsForMo.Count;
                            double fiberOk = okFinalStepsForMo.Sum(x => x.ActualWeight);

                            // ✅ Lấy danh sách WC mà máy này chạy MO này
                            var wcSet = moGroup
                                .Select(x => x.WorkCenter.Trim().ToUpper())
                                .Distinct()
                                .ToList();

                            // ✅ Tính PlannedQty cho MO này TRÊN CÁC WC ĐÓ
                            int plannedQtyForThisMachineMo = 0;
                            if (!string.IsNullOrWhiteSpace(mo) && wcSet.Count > 0)
                            {
                                plannedQtyForThisMachineMo = plansForDate
                                    .Where(p => p.MO == mo && wcSet.Contains(p.WorkCenter.Trim().ToUpper()))
                                    .Sum(p => p.PlannedQty);
                            }

                            return new
                            {
                                MO = mo,
                                kitsOk,
                                fiberOk,
                                plannedQtyForThisMachineMo
                            };
                        })
                        .ToList();

                    // Tổng kit + fiber theo máy
                    int completedKits = kitsPerMo.Sum(x => x.kitsOk);
                    double usedFiberKg = kitsPerMo.Sum(x => x.fiberOk);

                    // ✅ Đếm MO hoàn thành: chỉ những MO có kế hoạch >0 và kitsOk >= plannedQty ở máy này
                    int completedMos = kitsPerMo.Count(x =>
                        x.plannedQtyForThisMachineMo > 0 &&
                        x.kitsOk >= x.plannedQtyForThisMachineMo
                    );

                    return new
                    {
                        machineId,
                        completedMos,
                        completedKits,
                        usedFiberKg
                    };
                })
                .ToList();

            // 4. Tổng toàn bộ máy
            int totalCompletedKits = perMachine.Sum(m => m.completedKits);
            double totalFiberKg = perMachine.Sum(m => m.usedFiberKg);

            return Results.Ok(new
            {
                isEmpty = false,
                date = targetDate.ToString("yyyy-MM-dd"),
                totalCompletedKits,
                totalFiberKg,
                perMachine
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    // GET /api/blow-fill/context?machineId=M1
    app.MapGet("/api/blow-fill/context", async (string machineId, BlowFillDbContext blowDb) =>
    {
        try
        {
            if (string.IsNullOrWhiteSpace(machineId))
                return Results.BadRequest("Missing machineId");

            string m = machineId.Trim();

            var ctx = await blowDb.BlowFillContexts
                .FirstOrDefaultAsync(c => c.MachineId == m);

            if (ctx == null)
            {
                return Results.Ok(new { found = false });
            }

            return Results.Ok(new
            {
                found = true,
                machineId = ctx.MachineId,
                mo = ctx.MO,
                fiberKit = ctx.FiberKit,
                targetWeight = ctx.TargetWeight,
                totalSteps = ctx.TotalSteps,
                lastUpdate = ctx.LastUpdate.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    app.MapGet("/blow-fill-report", async ctx =>
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync("wwwroot/blow-fill-report.html");
    });

    app.MapGet("/blow-fill-dashboard", async ctx =>
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync("wwwroot/blow-fill-dashboard.html");
    });

    app.MapGet("/glue-line", async ctx =>
    {
        await ctx.Response.WriteAsync("<h1>GLUE LINE - Dang xay dung...</h1><a href='/'>Quay lai</a>");
    });
    app.MapGet("/sorting-foam", async ctx =>
    {
        await ctx.Response.WriteAsync("<h1>SORTING FOAM - Dang xay dung...</h1><a href='/'>Quay lai</a>");
    });
    app.MapGet("/hand-fill", async ctx =>
    {
        await ctx.Response.WriteAsync("<h1>HAND FILL - Dang xay dung...</h1><a href='/'>Quay lai</a>");
    });
    app.MapGet("/feather-fill", async ctx =>
    {
        await ctx.Response.WriteAsync("<h1>FEATHER FILL - Dang xay dung...</h1><a href='/'>Quay lai</a>");
    });

    // ==================== API VÀ TRANG TEST ĐẦU CÂN ====================

    // Route để hiển thị trang HTML
    app.MapGet("/scale-tester", async ctx =>
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync("wwwroot/scale-tester.html");
    });

    // API để MỞ cổng COM và bắt đầu lắng nghe
    app.MapPost("/api/scale/start-listening", async (string portName, IHubContext<ScaleTestHub> hubContext) =>
    {
        if (_scaleSerialPort != null && _scaleSerialPort.IsOpen)
        {
            return Results.Conflict("Cổng COM đã được mở.");
        }

        try
        {
            _scaleSerialPort = new System.IO.Ports.SerialPort(portName, 9600, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);
            _scaleSerialPort.DataReceived += async (sender, e) =>
            {
                try
                {
                    var sp = (System.IO.Ports.SerialPort)sender;
                    string data = sp.ReadLine();
                    // Gửi dữ liệu nhận được cho tất cả client đang kết nối
                    await hubContext.Clients.All.SendAsync("ReceiveData", data.Trim());
                }
                catch { /* Bỏ qua lỗi đọc */ }
            };
            _scaleSerialPort.Open();
            return Results.Ok($"Đã mở cổng {portName} và bắt đầu lắng nghe.");
        }
        catch (Exception ex)
        {
            return Results.Problem($"Lỗi khi mở cổng {portName}: {ex.Message}");
        }
    });

    // API để GỬI lệnh đi
    app.MapPost("/api/scale/send-command", (string command) => // Bỏ async và await
    {
        if (_scaleSerialPort == null || !_scaleSerialPort.IsOpen)
        {
            return Results.BadRequest("Cổng COM chưa được mở. Vui lòng bắt đầu lắng nghe trước.");
        }
        
        try
        {
            // ✅ SỬA LẠI THÀNH WriteLine
            _scaleSerialPort.WriteLine(command);
            return Results.Ok($"Đã gửi lệnh: '{command}'");
        }
        catch (Exception ex)
        {
            return Results.Problem($"Lỗi khi gửi lệnh: {ex.Message}");
        }
    });

    // API để ĐÓNG cổng COM
    app.MapPost("/api/scale/stop-listening", () =>
    {
        if (_scaleSerialPort != null && _scaleSerialPort.IsOpen)
        {
            _scaleSerialPort.Close();
            _scaleSerialPort.Dispose();
            _scaleSerialPort = null;
            return Results.Ok("Đã đóng cổng COM.");
        }
        return Results.Ok("Cổng COM đã đóng sẵn.");
    });

    // ==================== HEALTH CHECK API ====================
    app.MapGet("/api/health", async (AppDbContext appDb, BlowFillDbContext blowDb, ToolManagementDbContext toolDb) =>
    {
        try
        {
            // 1. Kiểm tra kết nối DB + số bản ghi cơ bản
            var now = DateTime.Now;

            // DB chính
            int orderCount      = await appDb.Orders.CountAsync();
            int moProgressCount = await appDb.MoProgresses.CountAsync();

            // Blow Fill
            DateTime today = DateTime.Today;
            DateTime sevenDaysAgo = today.AddDays(-7);

            int weighLog7Days = await blowDb.WeighLogs
                .Where(w => w.Timestamp >= sevenDaysAgo)
                .CountAsync();

            int blowContextCount = await blowDb.BlowFillContexts.CountAsync();

            // Tool
            int toolChanges30Days = await toolDb.ToolChanges
                .Where(t => t.Date >= today.AddDays(-30))
                .CountAsync();

            // 2. Kích thước file DB (nếu chạy trên Windows / có quyền truy cập)
            string appBase = Directory.GetCurrentDirectory();
            string dataFolder = Path.Combine(appBase, "Data");

            long GetFileSizeKb(string path)
            {
                if (!File.Exists(path)) return 0;
                return new FileInfo(path).Length / 1024;
            }

            long orderDbSizeKb     = GetFileSizeKb(Path.Combine(dataFolder, "OrderTracking.db"));
            long blowDbSizeKb      = GetFileSizeKb(Path.Combine(dataFolder, "BlowFillWeigh.db"));
            long toolDbSizeKb      = GetFileSizeKb(Path.Combine(dataFolder, "ToolManagement.db"));

            // 3. Trả về thông tin
            int onlineConnections = OrderHub.OnlineUserCount;
            return Results.Ok(new
            {
                serverTime = now.ToString("yyyy-MM-dd HH:mm:ss"),
                signalR = new
                {
                    onlineConnections = onlineConnections
                },
                databases = new
                {
                    orderTracking = new { sizeKb = orderDbSizeKb, orderCount, moProgressCount },
                    blowFill      = new { sizeKb = blowDbSizeKb, weighLogLast7Days = weighLog7Days, contextCount = blowContextCount },
                    tool          = new { sizeKb = toolDbSizeKb, toolChangesLast30Days = toolChanges30Days }
                }
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    app.Run("http://0.0.0.0:5050");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled exception - server is terminating");
}
finally
{
    Log.CloseAndFlush();
}

// ==================== DATABASE MODELS ====================
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Order> Orders { get; set; }
    public DbSet<MxDetail> MxDetails { get; set; }
    public DbSet<Kho2_Inventory> Kho2_Inventory { get; set; }
    public DbSet<ScanLog> ScanLogs { get; set; }
    public DbSet<MoProgress> MoProgresses { get; set; }
    public DbSet<AppSetting> AppSettings { get; set; }
    public DbSet<MoPlan> MoPlans { get; set; }
    public DbSet<MxHeader> MxHeaders { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Order>()
            .HasIndex(o => new { o.OdrNo, o.DateKey });

        modelBuilder.Entity<Order>()
            .HasIndex(o => o.Status);

        modelBuilder.Entity<MxDetail>()
            .HasIndex(m => m.OdrNo);

        modelBuilder.Entity<ScanLog>()
            .HasIndex(s => new { s.MO, s.ScanTime });

        modelBuilder.Entity<MoProgress>()
            .HasIndex(m => new { m.MO, m.WorkCenter });

        modelBuilder.Entity<MoPlan>()
            .HasIndex(m => new { m.PlanDate, m.MO, m.WorkCenter });

        modelBuilder.Entity<MoPlan>()
            .HasIndex(m => m.FiberKit);
        modelBuilder.Entity<MxHeader>()
            .HasIndex(h => new { h.OdrNo, h.DateKey })
            .IsUnique();
    }
}

public class BlowFillDbContext : DbContext
{
    public BlowFillDbContext(DbContextOptions<BlowFillDbContext> options) : base(options) { }

    public DbSet<WeighLog> WeighLogs { get; set; }

    // ✅ PHẢI CÓ
    public DbSet<BlowFillContext> BlowFillContexts { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<WeighLog>()
            .HasIndex(w => w.Timestamp);

        modelBuilder.Entity<WeighLog>()
            .HasIndex(w => new { w.MO, w.FiberKit });

        modelBuilder.Entity<BlowFillContext>()
            .HasIndex(c => c.MachineId)
            .IsUnique();
    }
}

// ==================== TOOL MANAGEMENT DATABASE CONTEXT ====================
public class ToolManagementDbContext : DbContext
{
    public ToolManagementDbContext(DbContextOptions<ToolManagementDbContext> options) : base(options) { }

    public DbSet<ToolChange> ToolChanges { get; set; }
    public DbSet<ToolStatus> ToolStatuses { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Index cho ToolChange
        modelBuilder.Entity<ToolChange>()
            .HasIndex(t => new { t.MachineName, t.Shift, t.ToolPosition, t.Date });

        // Index unique cho ToolStatus
        modelBuilder.Entity<ToolStatus>()
            .HasIndex(t => new { t.MachineName, t.Shift, t.ToolPosition })
            .IsUnique();
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
    public string FileType { get; set; } = "";
    public string DateKey { get; set; } = "";
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
    public string PartNameVN { get; set; } = "";
    public int PartQty { get; set; }
    public int PartOrder { get; set; }
    public string LastUpdate { get; set; } = "";
}

public class MxHeader
{
    public int Id { get; set; }
    public string OdrNo { get; set; } = "";
    public string DateKey { get; set; } = ""; 
    public string UphLine { get; set; } = "";
    public string ExpValue { get; set; } = "";
}


public class WeighLog
{
    public int Id { get; set; }
    public string MachineId { get; set; } = "";
    public DateTime Timestamp { get; set; }   
    public string WorkCenter { get; set; } = "";
    public string MO { get; set; } = "";
    public string FiberKit { get; set; } = "";
    public int StepNumber { get; set; }
    public double TargetWeight { get; set; }
    public double ActualWeight { get; set; }
    public double Tolerance { get; set; }
    public string Status { get; set; } = "";     
    public string Operator { get; set; } = "";  
}

public class BlowFillContext
{
    public int Id { get; set; }
    public string MachineId { get; set; } = "";
    public string MO { get; set; } = "";
    public string FiberKit { get; set; } = "";
    public double TargetWeight { get; set; }
    public int TotalSteps { get; set; }
    public DateTime LastUpdate { get; set; }
}


public class Kho2_Inventory
{
    public int Id { get; set; }
    public string OdrNo { get; set; } = "";
    public string ZoneCode { get; set; } = "";
    public DateTime InTime { get; set; }
    public DateTime UpdateTime { get; set; }
    public DateTime? OutTime { get; set; }
    public string Status { get; set; } = "In";
}

public class ScanLog
{
    public int Id { get; set; }
    public string MO { get; set; } = "";
    public string Item { get; set; } = "";
    public string WorkCenter { get; set; } = "";
    public int Qty { get; set; }
    public DateTime ScanTime { get; set; }
    public string Source { get; set; } = "AS400";
}

public class MoProgress
{
    public int Id { get; set; }
    public DateTime PlannedDate { get; set; }
    public string MO { get; set; } = "";
    public string MX { get; set; } = "";
    public string WorkCenter { get; set; } = "";
    public int PlannedQty { get; set; }
    public int ActualQty { get; set; }
    public DateTime? LastScanTime { get; set; }
    public string Status { get; set; } = "pending";
    public string LeadtimeString { get; set; } = "";
}

public class MoPlan
{
    public int Id { get; set; }
    public DateTime PlanDate { get; set; }  
    public string MO { get; set; } = "";
    public string WorkCenter { get; set; } = ""; 
    public string FiberKit { get; set; } = "";   
    public int PlannedQty { get; set; }           
}

public class AppSetting
{
    [Key]
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

// ==================== TOOL MANAGEMENT MODELS ====================
public class ToolChange
{
    public int Id { get; set; }
    public string Shift { get; set; } = "";           // Day Shift / Night Shift
    public string Supervisor { get; set; } = "";      
    public string MSS { get; set; } = "";             
    public DateTime Date { get; set; }                
    public string MachineName { get; set; } = "";     // Heian 4 - Heian 21
    
    public int ToolPosition { get; set; }             // 1-4 (DS: 1-4, NS: 5-8)
    public int ToolVersion { get; set; }              
    public string ToolType { get; set; } = "MỚI";     // MỚI / MÀI LẦN 1 / MÀI LẦN 2
    
    public DateTime? InstallDate { get; set; }        
    public TimeSpan? InstallTime { get; set; }        
    public DateTime? ReplaceDate { get; set; }        
    public TimeSpan? ReplaceTime { get; set; }        
    
    public int ActualHours { get; set; }              // 0-11
    public string Reason { get; set; } = "";          // Cháy / Cùn / Cuối ca thay / Mẻ / Gãy
    public string Material { get; set; } = "PLYWOOD"; 
    public string Supplier { get; set; } = "";

    public bool IsVersionIncrement { get; set; } = true;
    
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public class ToolStatus
{
    public int Id { get; set; }
    public string MachineName { get; set; } = "";
    public string Shift { get; set; } = "";           // Day Shift / Night Shift
    public int ToolPosition { get; set; }             // 1-4 (DS: 1-4, NS: 5-8)
    public int CurrentVersion { get; set; }           
    public int TotalHours { get; set; } = 0;          
    public int CurrentVersionHours { get; set; } = 0;
    public DateTime LastUpdated { get; set; } = DateTime.Now;
}

// ==================== BACKGROUND SERVICE POLLING AS400 ====================
public class As400ScanPollingService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<As400ScanPollingService> _logger;
    // private const string LastScanTimeKey = "LastScanTime";

    // Danh sách WC trong file kế hoạch mà bạn muốn theo dõi
    private static readonly HashSet<string> ALLOWED_WORKCENTERS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "UBF03", "UBF04", "UBF05", "UBF06", "UBF12", "UBF13",
        "UPHD1", "UCFBP", "UCFHS", "UCFHM", "UCFCH", "UCFCT",
        "UCFCM", "UCFCS", "UCFCV", 
        "UPGL1", "UPGL2", "UPGL3", "UPGL4", "UPGL6",
        "WLGL2", "UCFCO", "UFGL2"
    };

    public As400ScanPollingService(IServiceProvider services, ILogger<As400ScanPollingService> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Chuẩn hóa WorkCenter từ file Excel sang WorkCenter gốc trên AS400.
    /// Ví dụ: UPGL2_I, UPGL2_II → UPGL2 ; UBF03_M, UBF03_S → UBF03
    /// </summary>
    private static string NormalizeWcForAs400(string wcFromExcel)
    {
        if (string.IsNullOrWhiteSpace(wcFromExcel)) return wcFromExcel;

        wcFromExcel = wcFromExcel.Trim().ToUpper();

        int underscoreIndex = wcFromExcel.IndexOf('_');
        if (underscoreIndex > 0)
        {
            return wcFromExcel.Substring(0, underscoreIndex);
        }

        return wcFromExcel;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AS400 Scan Polling Service started");
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                await PollOnceAsync(scope, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while polling AS400 scan data");
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private async Task PollOnceAsync(IServiceScope scope, CancellationToken token)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<OrderHub>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<As400ScanPollingService>>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        if (token.IsCancellationRequested)
        {
            logger.LogInformation("[AS400 Polling] Shutdown requested, skipping poll.");
            return;
        }

        // ===== 1. Đọc file kế hoạch, gom MO và tính tỷ lệ cho UBF12 =====
        var moList = new List<(string MO, string WorkCenterExcel, string WorkCenterBase)>();
        var ubf12Ratios = new Dictionary<string, double>(); // Dictionary để lưu tỷ lệ: MO -> (sản phẩm/kit)

        try
        {
            string? schedulePathLocal = configuration["SchedulePath"];
            string? filePath = FileHelpers.FindLatestScheduleFile(DateTime.Now, schedulePathLocal ?? "");

            if (filePath != null && File.Exists(filePath))
            {
                logger.LogInformation($"[AS400 Polling] Reading latest schedule: {Path.GetFileName(filePath)}");
                using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = ExcelReaderFactory.CreateReader(stream);
                var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration() { ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = false } });
                
                foreach (DataTable table in dataSet.Tables)
                {
                    string workCenterName = table.TableName;
                    string baseWcName = NormalizeWcForAs400(workCenterName);
                    if (!ALLOWED_WORKCENTERS.Contains(baseWcName)) continue;
                    if (workCenterName.ToLower().Contains("pivot") || workCenterName.ToLower().Contains("summary")) continue;
                    
                    for (int i = 1; i < table.Rows.Count; i++)
                    {
                        var row = table.Rows[i];
                        string mo = ExcelHelpers.GetCellValue(row, 5)?.Trim() ?? "";
                        if (string.IsNullOrEmpty(mo)) continue;
                        
                        moList.Add((mo, workCenterName, baseWcName));

                        // ✅ UBF12 SPECIAL LOGIC: Tính và lưu tỷ lệ sản phẩm/kit
                        if (baseWcName.Equals("UBF12", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!ubf12Ratios.ContainsKey(mo))
                            {
                                string productsStr = ExcelHelpers.GetCellValue(row, 8); // Cột I
                                string kitsStr = ExcelHelpers.GetCellValue(row, 3);     // Cột D

                                if (double.TryParse(productsStr, out double products) && 
                                    double.TryParse(kitsStr, out double kits) && 
                                    kits > 0)
                                {
                                    ubf12Ratios[mo] = products / kits;
                                }
                            }
                        }
                    }
                }
            }

            // Đọc thêm file GLUE FOAM (logic này giữ nguyên)
            var glueLinePath = configuration["GlueLinePath"];
            if (!string.IsNullOrEmpty(glueLinePath))
            {
                string glueFileName = $"GLUE FOAM {DateTime.Now:ddMMyyyy}.xlsx";
                string glueFilePath = Path.Combine(glueLinePath, glueFileName);
                if (File.Exists(glueFilePath))
                {
                    logger.LogInformation($"[AS400 Polling] Reading additional plan from: {glueFileName}");
                    using var stream = File.Open(glueFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = ExcelReaderFactory.CreateReader(stream);
                    var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration() { ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = false } });
                    
                    var glueTable = dataSet.Tables["GLUE"] ?? dataSet.Tables[0];
                    for (int i = 2; i < glueTable.Rows.Count; i++)
                    {
                        var row = glueTable.Rows[i];
                        string mo = ExcelHelpers.GetCellValue(row, 1);
                        string wc = ExcelHelpers.GetCellValue(row, 16);
                        if (!string.IsNullOrEmpty(mo) && !string.IsNullOrEmpty(wc))
                        {
                            string baseWc = NormalizeWcForAs400(wc);
                            if(ALLOWED_WORKCENTERS.Contains(baseWc))
                            {
                                moList.Add((mo, wc, baseWc));
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AS400 Polling] Error reading schedule files.");
            return;
        }

        var plansByBaseWc = moList
            .GroupBy(item => item.WorkCenterBase)
            .ToDictionary(g => g.Key, g => g.Select(item => item.MO).Distinct().ToList());

        if (!plansByBaseWc.Any())
        {
            logger.LogInformation("[AS400 Polling] No MOs found in allowed WorkCenters from schedule.");
            return;
        }

        // ===== 2. Lấy LastScanTime (giữ nguyên) =====
        var lastScanTimeKeys = plansByBaseWc.Keys.Select(wc => $"LastScanTime_{wc}").ToList();
        var allLastScanTimeSettings = await db.AppSettings
            .Where(s => lastScanTimeKeys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => DateTime.Parse(s.Value), token);

        // ===== 3. Lặp qua từng WC để query AS400 (giữ nguyên) =====
        foreach (var kvp in plansByBaseWc)
        {
            string baseWc = kvp.Key;
            List<string> moInWc = kvp.Value;

            if (token.IsCancellationRequested) break;

            string currentKey = $"LastScanTime_{baseWc}";
            DateTime lastScanTime = allLastScanTimeSettings.TryGetValue(currentKey, out var time) ? time : DateTime.UtcNow.AddDays(-7);

            var allNewRowsForWc = new List<(string MO, string MX, string Item, string Wc, int Qty, DateTime ScanTime)>();
            DateTime latestScanTimeInBatch = lastScanTime;

            try
            {
                if (!moInWc.Any()) continue;
                var whereClause = $"TRIM(A.ODORDR) IN ({string.Join(",", moInWc.Select(mo => $"'{mo}'"))}) AND TRIM(A.ODWKCN) = '{baseWc}'";
                using var conn = new OdbcConnection("DSN=WFVNPROD;UID=WNKRND;PWD=wnkrnd@112;TRANSLATE=1;");
                await conn.OpenAsync(token);
                string sql = $@"
                    SELECT 
                        TRIM(A.ODORDR) AS ODORDR, TRIM(B.REFNO) AS MX_REFNO,
                        TRIM(A.ODPN) AS ODPN, A.ODQTYC,
                        TRIM(A.ODWKCN) AS ODWKCN, A.ODTSTP
                    FROM WWDCF.GRPORDH A
                    LEFT JOIN AMFLIBW.MOMAST B ON A.ODORDR = B.ORDNO
                    WHERE ({whereClause})
                    AND A.ODTSTP > ?
                    AND B.OSTAT NOT IN ('99')
                    AND SUBSTR(B.REFNO, 1, 2) = 'MX'
                    ORDER BY A.ODTSTP";
                using var cmd = new OdbcCommand(sql, conn);
                cmd.Parameters.Add("?", OdbcType.VarChar).Value = lastScanTime.ToString("yyyy-MM-dd-HH.mm.ss.ffffff");
                using var reader = await cmd.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    //... (code đọc reader giữ nguyên)
                    string mo = reader.GetString(0);
                    string mx = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    string item = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    int qty = reader.IsDBNull(3) ? 0 : (int)reader.GetDecimal(3);
                    string wc = reader.IsDBNull(4) ? "" : reader.GetString(4);
                    DateTime scanTime = reader.GetDateTime(5);
                    allNewRowsForWc.Add((mo, mx, item, wc, qty, scanTime));
                    if (scanTime > latestScanTimeInBatch) latestScanTimeInBatch = scanTime;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"[AS400 Polling] Exception for WC {baseWc}.");
                continue;
            }

            if (allNewRowsForWc.Count == 0) continue;
            logger.LogInformation($"[AS400 Polling] Found {allNewRowsForWc.Count} new scans for WC {baseWc}.");

            // ===== 4. Cập nhật DB và broadcast SignalR =====
            var updatedMoGroups = allNewRowsForWc.GroupBy(r => new { r.MO, BaseWc = NormalizeWcForAs400(r.Wc) });
            foreach (var group in updatedMoGroups)
            {
                string mo = group.Key.MO;
                string currentBaseWc = group.Key.BaseWc;

                // 4.1 Lưu ScanLog (giữ nguyên)
                foreach (var row in group)
                {
                    var logExists = await db.ScanLogs.AnyAsync(l => l.MO == row.MO && l.ScanTime == row.ScanTime && l.WorkCenter == row.Wc, token);
                    if (!logExists)
                    {
                        db.ScanLogs.Add(new ScanLog { MO = row.MO, Item = row.Item, WorkCenter = row.Wc, Qty = row.Qty, ScanTime = row.ScanTime });
                    }
                }
                await db.SaveChangesAsync(token);

                // 4.2 Tìm hoặc tạo MoProgress (giữ nguyên)
                var allMpForMo = await db.MoProgresses.Where(m => m.MO == mo).ToListAsync(token);
                var relatedMp = allMpForMo.Where(m => NormalizeWcForAs400(m.WorkCenter) == currentBaseWc).ToList();
                if (!relatedMp.Any())
                {
                    var firstScan = group.First();
                    var newMp = new MoProgress { MO = mo, MX = firstScan.MX, WorkCenter = currentBaseWc, PlannedQty = 0, Status = "pending", PlannedDate = DateTime.Now };
                    db.MoProgresses.Add(newMp);
                    await db.SaveChangesAsync(token);
                    relatedMp.Add(newMp);
                    logger.LogInformation($"[AS400 Polling] Auto-created MoProgress for MO={mo}, WC={currentBaseWc}.");
                }
                
                // ✅ 4.3 TÍNH TOÁN LẠI TIẾN ĐỘ VỚI LOGIC CHO UBF12
                var logsForMo = await db.ScanLogs.Where(s => s.MO == mo).ToListAsync(token);
                var logsForMoAndBaseWc = logsForMo.Where(s => NormalizeWcForAs400(s.WorkCenter) == currentBaseWc).ToList();
                
                int totalQty;
                if (currentBaseWc.Equals("UBF12", StringComparison.OrdinalIgnoreCase))
                {
                    double calculatedTotalProducts = 0;
                    foreach (var scanLog in logsForMoAndBaseWc)
                    {
                        if (ubf12Ratios.TryGetValue(scanLog.MO, out double ratio))
                        {
                            calculatedTotalProducts += scanLog.Qty * ratio;
                        }
                        else
                        {
                            // Fallback: nếu không tìm thấy tỷ lệ, coi như tỷ lệ là 1
                            calculatedTotalProducts += scanLog.Qty;
                            logger.LogWarning($"[AS400 Polling] Missing ratio for UBF12 MO: {scanLog.MO}. Using 1:1 conversion.");
                        }
                    }
                    totalQty = (int)Math.Round(calculatedTotalProducts);
                }
                else
                {
                    // Logic cũ cho các WC khác
                    totalQty = logsForMoAndBaseWc.Sum(s => s.Qty);
                }

                DateTime? lastScanTimeForPair = logsForMoAndBaseWc.OrderByDescending(s => s.ScanTime).Select(s => (DateTime?)s.ScanTime).FirstOrDefault();

                // 4.4 & 4.5 Cập nhật và broadcast (giữ nguyên)
                foreach (var mp in relatedMp)
                {
                    mp.ActualQty = totalQty;
                    mp.LastScanTime = lastScanTimeForPair;
                    if (mp.PlannedQty > 0)
                        mp.Status = AppHelpers.ComputeStatus(mp);
                }
                if (relatedMp.Any()) await db.SaveChangesAsync(token);

                foreach (var mp in relatedMp)
                {
                    await hubContext.Clients.All.SendAsync("MoProgressUpdated", new { mo = mp.MO, mx = mp.MX, workCenter = mp.WorkCenter, planned = mp.PlannedQty, actual = mp.ActualQty, status = mp.Status, lastScanTime = mp.LastScanTime?.ToString("yyyy-MM-dd HH:mm:ss") }, token);
                }
            }
            
            // 5. Cập nhật LastScanTime (giữ nguyên)
            var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == currentKey, token);
            if (setting == null)
            {
                db.AppSettings.Add(new AppSetting { Key = currentKey, Value = latestScanTimeInBatch.ToString("o") });
            }
            else
            {
                setting.Value = latestScanTimeInBatch.ToString("o");
            }
            await db.SaveChangesAsync(token);
        }
    }
}

// ==================== DTO MODELS ====================
record UpdateRequest(string Odrno, string Status, string Note);
record ExportRequest(string Date, string FileType, List<Dictionary<string, object>> Orders);
record MxItemData { public string ItemCode { get; set; } = ""; public int Quantity { get; set; } }
record PartDetailData { public string PartName { get; set; } = ""; public int Quantity { get; set; } public int Order { get; set; } }
record PartMappingData 
{ 
    public int Order { get; set; } 
    public string PartName { get; set; } = "";   
    public string PartNameVN { get; set; } = "";  
    public int ColumnIndex { get; set; } 
}
record TrackingData(string Mx, List<WorkCenterStep> Steps);
record WorkCenterStep(string Mx, string WorkCenter, string FgItem, string Mo, string Qty, string Leadtime);
record Kho2ScanRequest(string Odrno, string ZoneCode);
record WeighLogRequest( string MachineId, string WorkCenter, string MO, string FiberKit, int StepNumber, double TargetWeight, double ActualWeight, double Tolerance, string Status, string OperatorName);
record ToolChangeRequest(
    string MachineName,
    string Shift,
    int ToolPosition,
    string? Supervisor,
    string? MSS,
    string? Date,
    string? ToolType,
    string? InstallDate,
    string? InstallTime,
    string? ReplaceDate,
    string? ReplaceTime,
    int ActualHours,
    string? Reason,
    string? Material,
    string? Supplier
);
record ChangePortRequest(string PortName);
// record BlowFillPushRequest(string MachineId, double Weight);

// Get Cell Value Helper
public static class ExcelHelpers
{
    public static string GetCellValue(DataRow row, int columnIndex)
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
}

public static class FileHelpers
{
    // ==================== HELPER: TÌM FILE KẾ HOẠCH MỚI NHẤT (THEO VERSION HOẶC NGÀY SỬA) ====================
    public static string? FindLatestScheduleFile(DateTime targetDate, string schedulePath)
    {
        // 1. Tạo pattern tìm kiếm cho ngày cụ thể
        string datePattern = targetDate.ToString("ddMMyyyy");
        string searchPattern = $"UPH Support Schedule {datePattern}*.xlsx";

        // 2. Tìm tất cả các file khớp với pattern
        var matchingFiles = Directory.GetFiles(schedulePath, searchPattern)
            .Where(f => !Path.GetFileName(f).StartsWith("~"))
            .ToList();

        if (!matchingFiles.Any())
        {
            return null; // Không tìm thấy file nào
        }

        // 3. Phân tích phiên bản và ngày sửa của từng file
        var fileVersions = matchingFiles.Select(file => {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var fileInfo = new FileInfo(file);
            int version = 0;

            // Dùng Regex để tìm các chuỗi như "VER 3", "V.2", "Version4"
            var match = System.Text.RegularExpressions.Regex.Match(fileName, @"(?:V|VER|VERSION)[\s\.]*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                int.TryParse(match.Groups[1].Value, out version);
            }

            return new 
            {
                FullPath = file,
                Version = version,
                LastWriteTime = fileInfo.LastWriteTime
            };
        }).ToList();

        // 4. Sắp xếp để tìm file "tốt nhất"
        // Ưu tiên 1: Version cao nhất
        // Ưu tiên 2: Nếu version bằng nhau, lấy file được sửa gần đây nhất
        var latestFile = fileVersions
            .OrderByDescending(f => f.Version)
            .ThenByDescending(f => f.LastWriteTime)
            .FirstOrDefault();

        return latestFile?.FullPath;
    }
}

// ==================== GLOBAL HELPER FUNCTIONS ====================
public static class AppHelpers
{
    public static string ComputeStatus(MoProgress mp)
    {
        // Nếu chưa quét, luôn là pending
        if (mp.ActualQty <= 0) return "pending";

        // Nếu có quét nhưng không có kế hoạch -> đang làm
        if (mp.PlannedQty <= 0) return "in-progress";

        // Nếu quét chưa đủ so với kế hoạch -> đang làm
        if (mp.ActualQty < mp.PlannedQty) return "in-progress";

        // Từ đây, ActualQty >= PlannedQty và PlannedQty > 0
        // => Đã hoàn thành, chỉ cần xét trễ hay không
        if (mp.LastScanTime.HasValue)
        {
            try
            {
                if (string.IsNullOrEmpty(mp.LeadtimeString) || !mp.LeadtimeString.Contains('-')) return "done";
                
                var parts = mp.LeadtimeString.Split('-');
                var endStr = parts[1].Trim();
                var endParts = endStr.Split(':');
                int endHour = int.Parse(endParts[0]);
                int endMin = int.Parse(endParts[1]);

                DateTime targetDate = mp.PlannedDate.Date;
                DateTime leadtimeEnd = targetDate.AddHours(endHour).AddMinutes(endMin);

                var startParts = parts[0].Trim().Split(':');
                int startHour = int.Parse(startParts[0]);

                if (endHour < startHour)
                {
                    leadtimeEnd = leadtimeEnd.AddDays(1);
                }

                return mp.LastScanTime.Value <= leadtimeEnd ? "done" : "late";
            }
            catch { return "done"; }
        }
        
        // Nếu không có LastScanTime nhưng đã đủ số lượng -> done
        return "done";
    }

    public static bool IsPastLeadtime(MoProgress mp, DateTime? nowOverride = null)
    {
        var now = nowOverride ?? DateTime.Now;

        if (string.IsNullOrEmpty(mp.LeadtimeString) || !mp.LeadtimeString.Contains('-'))
            return false;

        try
        {
            var parts = mp.LeadtimeString.Split('-');
            var endStr = parts[1].Trim();
            var endParts = endStr.Split(':');
            int endHour = int.Parse(endParts[0]);
            int endMin  = int.Parse(endParts[1]);

            DateTime targetDate  = mp.PlannedDate.Date;
            DateTime leadtimeEnd = targetDate.AddHours(endHour).AddMinutes(endMin);

            // Xử lý case ca qua ngày
            var startParts = parts[0].Trim().Split(':');
            int startHour = int.Parse(startParts[0]);
            if (endHour < startHour)
                leadtimeEnd = leadtimeEnd.AddDays(1);

            return now > leadtimeEnd;
        }
        catch
        {
            return false;
        }
    }

}

// ===== RETRY HELPER FOR WRITE OPERATIONS =====
public static class DbRetryHelper
{
    public static async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, int maxRetries = 3)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                return await operation();
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
            {
                if (i == maxRetries - 1) throw;
                Console.WriteLine($"⚠️ Database busy, retrying... (attempt {i + 1}/{maxRetries})");
                await Task.Delay(50 * (i + 1)); // 50ms, 100ms, 150ms
            }
        }
        throw new InvalidOperationException("Should not reach here");
    }
    
    public static async Task ExecuteWithRetryAsync(Func<Task> operation, int maxRetries = 3)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await operation();
            return true;
        }, maxRetries);
    }
}

public class ScaleTestHub : Hub { }

public class ScaleReaderService : BackgroundService
{
    private readonly IHubContext<OrderHub> _hubContext;
    private readonly ILogger<ScaleReaderService> _logger;
    private readonly IConfiguration _configuration;
    private System.IO.Ports.SerialPort? _serialPort;

    public ScaleReaderService(IHubContext<OrderHub> hubContext, ILogger<ScaleReaderService> logger, IConfiguration configuration)
    {
        _hubContext = hubContext;
        _logger = logger;
        _configuration = configuration;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string portName = _configuration.GetValue<string>("ScaleSettings:PortName") ?? "COM14";
        int baudRate = _configuration.GetValue<int>("ScaleSettings:BaudRate", 9600);

        _serialPort = new System.IO.Ports.SerialPort(portName, baudRate, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);
        
        try
        {
            _serialPort.DataReceived += OnDataReceived;
            _serialPort.Open();
            _logger.LogInformation($"✅ Cổng cân '{portName}' đã được mở và đang lắng nghe.");
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Không thể mở cổng cân '{portName}': {ex.Message}");
            return Task.CompletedTask;
        }

        stoppingToken.Register(() => {
            if (_serialPort?.IsOpen ?? false)
            {
                _serialPort.Close();
                _logger.LogInformation($"✅ Cổng cân '{portName}' đã được đóng.");
            }
        });

        return Task.CompletedTask;
    }

    private async void OnDataReceived(object sender, System.IO.Ports.SerialDataReceivedEventArgs e)
    {
        if (_serialPort == null || !_serialPort.IsOpen) return;
        
        try
        {
            string rawData = _serialPort.ReadLine().Trim();
            _logger.LogInformation($"[ScaleData] Raw: '{rawData}'"); // ✅ THÊM LOG NÀY

            double weight = ParseWeight(rawData);
            _logger.LogInformation($"[ScaleData] Parsed: {weight}"); // ✅ THÊM LOG NÀY

            await _hubContext.Clients.All.SendAsync("ReceiveScaleData", weight);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Lỗi khi xử lý dữ liệu từ cân: {ex.Message}");
        }
    }

    private double ParseWeight(string rawData)
    {
        if (string.IsNullOrWhiteSpace(rawData))
        {
            return 0;
        }
        try
        {
            // Regex mới: tìm chuỗi số nằm sau dấu phẩy cuối cùng và trước "kg"
            var match = System.Text.RegularExpressions.Regex.Match(rawData, @",([+-]?\s*[\d\.]+)\s*kg");
            
            if (match.Success)
            {
                string weightString = match.Groups[1].Value;
                if (double.TryParse(weightString, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double weight))
                {
                    return weight;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Không thể parse dữ liệu cân: '{rawData}'. Lỗi: {ex.Message}");
        }
        return 0;
    }

    public override void Dispose()
    {
        _serialPort?.Dispose();
        base.Dispose();
    }
}
