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
builder.Configuration["SchedulePath"] = schedulePath;

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
                Status = "Pending",
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
            await db.SaveChangesAsync();
        }

        await hubContext.Clients.All.SendAsync("MasterFileSynced", new
        {
            message = "Master file has been updated",
            time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        });

        Console.WriteLine("📡 Broadcasted sync completion to all clients");

        // =====================================================================
        // 🧹 TỰ ĐỘNG DỌN DẸP DỮ LIỆU CŨ (LƯU 21 NGÀY)
        // =====================================================================
        Console.WriteLine("🧹 Đang dọn dẹp dữ liệu cũ hơn 21 ngày...");
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
                Console.WriteLine($"   🗑️ Đã xóa lịch sử {kho2ToDelete.Count} xe xuất Kho 2 cũ.");
            }

            await db.SaveChangesAsync();
            Console.WriteLine("✅ Dọn dẹp hoàn tất!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Lỗi khi dọn dẹp dữ liệu cũ: {ex.Message}");
        }

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

            string odrno = GetCellValue(row, 3);
            if (string.IsNullOrWhiteSpace(odrno)) continue;

            uploadedOdrNos.Add(odrno);

            var existingOrder = await db.Orders
                .FirstOrDefaultAsync(o => o.OdrNo == odrno && o.DateKey == dateKey);

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

        var details = await db.MxDetails
            .Where(m => m.OdrNo.ToUpper() == odrno.ToUpper())
            .ToListAsync();

        Console.WriteLine($"📊 Tìm thấy {details.Count} dòng dữ liệu trong Database");

        if (details.Count == 0)
        {
            var totalDetailsInDb = await db.MxDetails.CountAsync();
            Console.WriteLine($"⚠️ Tổng số dòng detail trong DB: {totalDetailsInDb}");

            if (totalDetailsInDb == 0)
            {
                return Results.NotFound("Database chưa có dữ liệu chi tiết. Vui lòng bấm 'Cập Nhật Master File'.");
            }
            return Results.NotFound($"Không tìm thấy chi tiết cho MX {odrno}.");
        }

        var itemsList = details
            .GroupBy(d => d.ItemCode)
            .Select(g => new MxItemData { ItemCode = g.Key, Quantity = g.First().ItemQty })
            .ToList();

        var partsList = details
            .GroupBy(d => new { d.PartName, d.PartOrder })
            .Select(g => new PartDetailData
            {
                PartName = g.Key.PartName,
                Order = g.Key.PartOrder,
                Quantity = g.Sum(x => x.PartQty)
            })
            .OrderBy(p => p.Order)
            .ToList();

        Console.WriteLine($"✅ Đã đóng gói: {itemsList.Count} Items, {partsList.Count} Parts");

        return Results.Ok(new
        {
            odrno = odrno,
            items = itemsList,
            parts = partsList
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

        if (!dataSet.Tables.Contains("Print") ||
            !dataSet.Tables.Contains("4") ||
            !dataSet.Tables.Contains("13") ||
            !dataSet.Tables.Contains("17"))
        {
            Console.WriteLine("  ❌ Missing required sheets (Print, 4, 13, or 17)");
            return result;
        }

        var sheetPrint = dataSet.Tables["Print"];
        var sheet4 = dataSet.Tables["4"];
        var sheet13 = dataSet.Tables["13"];
        var sheet17 = dataSet.Tables["17"];

        var partMapping = GetPartMapping();

        string GetCell(DataTable dt, int rowIdx, int colIdx)
        {
            if (rowIdx >= dt.Rows.Count || colIdx >= dt.Columns.Count) return "";
            return dt.Rows[rowIdx][colIdx]?.ToString() ?? "";
        }

        var mxInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int r = 5; r < sheetPrint.Rows.Count; r++)
        {
            var mx = CleanStr(GetCell(sheetPrint, r, 18)); // col S (index 18)
            if (!string.IsNullOrEmpty(mx)) mxInFile.Add(mx);
        }

        var mxToProcess = odrnos
            .Where(mx => mxInFile.Contains(CleanStr(mx)))
            .Select(m => CleanStr(m))
            .ToList();

        var fallbackItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int r = 1; r < sheet17.Rows.Count; r++)
        {
            string mx = CleanStr(GetCell(sheet17, r, 1));
            string fallbackItem = CleanStr(GetCell(sheet17, r, 50));
            if (!string.IsNullOrEmpty(mx) && !string.IsNullOrEmpty(fallbackItem))
                fallbackItems[mx] = fallbackItem;
        }

        var sheet13Rows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int r = 0; r < sheet13.Rows.Count; r++)
        {
            string item = CleanStr(GetCell(sheet13, r, 0));
            if (!string.IsNullOrEmpty(item) && !sheet13Rows.ContainsKey(item))
                sheet13Rows[item] = r;
        }

        var sheet4Items = new Dictionary<string, List<(string ItemCode, int Qty)>>(StringComparer.OrdinalIgnoreCase);
        string lastValidItemCode = "";
        int lastValidQty = 0;

        for (int r = 1; r < sheet4.Rows.Count; r++)
        {
            string rawMx = CleanStr(GetCell(sheet4, r, 0));
            if (string.IsNullOrWhiteSpace(rawMx)) continue;

            var mxList = rawMx
                .Split(new[] { '/', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(m => CleanStr(m))
                .Where(m => !string.IsNullOrEmpty(m));

            string currentItemCode = CleanStr(GetCell(sheet4, r, 10));
            string qtyStr = CleanStr(GetCell(sheet4, r, 17));

            int currentQty = 0;
            if (int.TryParse(qtyStr, out int q)) currentQty = q;
            else if (double.TryParse(qtyStr, out double qd)) currentQty = (int)qd;

            if (string.IsNullOrEmpty(currentItemCode))
                currentItemCode = lastValidItemCode;
            else
                lastValidItemCode = currentItemCode;

            if (currentQty <= 0)
                currentQty = lastValidQty;
            else
                lastValidQty = currentQty;

            if (!string.IsNullOrEmpty(currentItemCode) && currentQty > 0)
            {
                foreach (var mx in mxList)
                {
                    if (!sheet4Items.ContainsKey(mx))
                        sheet4Items[mx] = new List<(string, int)>();
                    sheet4Items[mx].Add((currentItemCode, currentQty));
                }
            }
        }

        foreach (var odrno in mxToProcess)
        {
            if (!sheet4Items.TryGetValue(odrno, out var items)) continue;

            foreach (var (originalItemCode, itemQty) in items)
            {
                int itemRowIdx = -1;
                string finalItemCode = originalItemCode;

                if (sheet13Rows.TryGetValue(originalItemCode, out int row13))
                {
                    itemRowIdx = row13;
                }
                else
                {
                    if (fallbackItems.TryGetValue(odrno, out var fallbackItemCode))
                    {
                        finalItemCode = fallbackItemCode;
                        if (sheet13Rows.TryGetValue(fallbackItemCode, out int fallbackRow13))
                            itemRowIdx = fallbackRow13;
                    }
                }

                if (itemRowIdx == -1) continue;

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
                            PartName = part.PartName,
                            PartQty = partQtyPerItem * itemQty,
                            PartOrder = part.Order,
                            LastUpdate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        });
                    }
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

// ==================== TRACKING API ====================
app.MapGet("/api/tracking/journey", async (string date, AppDbContext db) =>
{
    try
    {
        DateTime targetDate;
        if (!DateTime.TryParse(date, out targetDate))
            return Results.BadRequest("Invalid date format. Use yyyy-MM-dd.");

        string fileName = $"UPH Support Schedule {targetDate:ddMMyyyy}.xlsx";
        string filePath = Path.Combine(schedulePath, fileName);

        if (!File.Exists(filePath))
            return Results.NotFound($"Không tìm thấy file kế hoạch: {fileName}");

        Console.WriteLine($"🔍 Đang đọc file tracking: {fileName}");

        var dataByMx = new Dictionary<string, List<WorkCenterStep>>();

        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration()
        {
            ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = false }
        });

        foreach (DataTable table in dataSet.Tables)
        {
            string workCenterName = table.TableName;
            if (workCenterName.ToLower().Contains("pivot") ||
                workCenterName.ToLower().Contains("summary"))
                continue;

            for (int i = 1; i < table.Rows.Count; i++)
            {
                var row = table.Rows[i];
                string mx = GetCellValue(row, 1);
                if (string.IsNullOrWhiteSpace(mx)) continue;
                string fg_item = GetCellValue(row, 2);
                string mo = GetCellValue(row, 5);
                string qty = GetCellValue(row, 8);
                string leadtime = GetCellValue(row, 12);

                if (!dataByMx.ContainsKey(mx))
                    dataByMx[mx] = new List<WorkCenterStep>();

                var alreadyExists = dataByMx[mx].Any(step =>
                    step.WorkCenter == workCenterName &&
                    step.Mo == mo &&
                    step.Qty == qty &&
                    step.Leadtime == leadtime);

                if (!alreadyExists)
                {
                    dataByMx[mx].Add(new WorkCenterStep(mx, workCenterName, fg_item, mo, qty, leadtime));
                }
            }
        }

        var result = dataByMx
            .Select(kvp => new TrackingData(kvp.Key, kvp.Value))
            .OrderBy(t => t.Mx)
            .ToList();

        Console.WriteLine($"✅ Đã xử lý xong {result.Count} mã MX.");

        // TẠO/CẬP NHẬT MoProgress THEO CẶP (MO, WC GỐC)
        try
        {
            var allSteps = result
                .SelectMany(r => r.Steps.Select(s => new
                {
                    Mx = r.Mx,
                    WcExcel = s.WorkCenter,
                    WcBase = NormalizeWcForAs400(s.WorkCenter),
                    Step = s
                }))
                .ToList();

            // Lấy hết MoProgress hiện có, key theo (MO, WC GỐC)
            var existing = await db.MoProgresses.ToListAsync();
            var existingMap = existing
                .GroupBy(p => (MO: p.MO.ToUpper(), WC: p.WorkCenter.ToUpper()))
                .ToDictionary(g => g.Key, g => g.First());

            // Gom planned Qty theo (MO, WC GỐC) trước
            var mergedPlan = allSteps
                .GroupBy(item => (MO: item.Step.Mo.Trim().ToUpper(), WC: item.WcBase.Trim().ToUpper()))
                .ToDictionary(
                    g => g.Key,
                    g => new
                    {
                        PlannedQty = g.Sum(x => int.TryParse(x.Step.Qty, out int q) ? q : 0),
                        Leadtime = g.Last().Step.Leadtime, // Lấy leadtime cuối
                        Mx = g.Last().Mx
                    }
                );

            var addedCount = 0;
            var updatedCount = 0;

            foreach (var kvp in mergedPlan)
            {
                var key = kvp.Key;
                var plan = kvp.Value;

                if (existingMap.TryGetValue(key, out var mp))
                {
                    // Ghi đè kế hoạch theo file mới
                    mp.PlannedQty = plan.PlannedQty;
                    mp.LeadtimeString = plan.Leadtime;
                    mp.MX = plan.Mx;
                    updatedCount++;
                }
                else
                {
                    var newMp = new MoProgress
                    {
                        MO = key.MO,
                        MX = plan.Mx,
                        WorkCenter = key.WC, // ✅ LƯU WC GỐC
                        PlannedQty = plan.PlannedQty,
                        ActualQty = 0,
                        Status = "pending",
                        LeadtimeString = plan.Leadtime
                    };

                    db.MoProgresses.Add(newMp);
                    existingMap[key] = newMp;
                    addedCount++;
                }
            }

            if (addedCount > 0 || updatedCount > 0)
            {
                await db.SaveChangesAsync();
                Console.WriteLine($"✅ MoProgress Sync: Thêm {addedCount}, Cập nhật {updatedCount} (MO + WC gốc).");
            }
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

// ==================== API DEBUG: LẤY DANH SÁCH WORK CENTER ====================
app.MapGet("/api/debug/workcenters", (string date) =>
{
    try
    {
        DateTime targetDate;
        if (!DateTime.TryParse(date, out targetDate))
            return Results.BadRequest("Invalid date format. Use yyyy-MM-dd.");

        string fileName = $"UPH Support Schedule {targetDate:ddMMyyyy}.xlsx";
        string filePath = Path.Combine(schedulePath, fileName);

        if (!File.Exists(filePath))
            return Results.NotFound($"Không tìm thấy file kế hoạch: {fileName}");

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
        var progressData = await db.MoProgresses
            .Select(p => new
            {
                mo = p.MO,
                mx = p.MX,
                workCenter = p.WorkCenter,
                plannedQty = p.PlannedQty,
                currentQty = p.ActualQty,
                leadtime = p.LeadtimeString,
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

// ==================== API DEBUG: XEM BẢNG MoProgresses ====================
app.MapGet("/api/debug/mo-progress", async (AppDbContext db) =>
{
    var allProgress = await db.MoProgresses
        .OrderBy(p => p.WorkCenter)
        .ThenBy(p => p.MO)
        .ToListAsync();
    return Results.Ok(allProgress);
});

// ==================== WEB ROUTES ====================

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

app.MapGet("/cnc-go", async ctx =>
{
    await ctx.Response.WriteAsync("<h1>CNC GO - Dang xay dung...</h1><a href='/'>Quay lai</a>");
});
app.MapGet("/ban-khung-go", async ctx =>
{
    await ctx.Response.WriteAsync("<h1>BAN KHUNG GO - Dang xay dung...</h1><a href='/'>Quay lai</a>");
});
app.MapGet("/blow-fill", async ctx =>
{
    await ctx.Response.WriteAsync("<h1>BLOW FILL - Dang xay dung...</h1><a href='/'>Quay lai</a>");
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
    public DbSet<AppSetting> AppSettings { get; set; }

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
    public int PartQty { get; set; }
    public int PartOrder { get; set; }
    public string LastUpdate { get; set; } = "";
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
    public string MO { get; set; } = "";
    public string MX { get; set; } = "";
    public string WorkCenter { get; set; } = "";
    public int PlannedQty { get; set; }
    public int ActualQty { get; set; }
    public DateTime? LastScanTime { get; set; }
    public string Status { get; set; } = "pending";
    public string LeadtimeString { get; set; } = "";
}

public class AppSetting
{
    [Key]
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
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
        "UCFCM", "UCFCS", "UCFCV", "UPGL1", "UPGL4", "UPGL6", 
        "WLGL2", "UCFCO", "UPGL2", "UFGL2", "UPHD1", "WLGL2"
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

        // ===== 1. Đọc file kế hoạch và gom MO theo từng WorkCenter gốc =====
        var moList = new List<(string MO, string WorkCenterExcel, string WorkCenterBase)>();
        try
        {
            var schedulePath = configuration["SchedulePath"];
            if (string.IsNullOrEmpty(schedulePath))
            {
                logger.LogError("[AS400 Polling] SchedulePath is not configured.");
                return;
            }
            string fileName = $"UPH Support Schedule {DateTime.Now:ddMMyyyy}.xlsx";
            string filePath = Path.Combine(schedulePath, fileName);

            if (File.Exists(filePath))
            {
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
                        string mo = table.Rows[i][5]?.ToString()?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(mo))
                        {
                            moList.Add((mo, workCenterName, NormalizeWcForAs400(workCenterName)));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AS400 Polling] Error reading schedule file.");
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

        // ===== 2. Lấy tất cả LastScanTime của các WC cùng lúc =====
        var lastScanTimeKeys = plansByBaseWc.Keys.Select(wc => $"LastScanTime_{wc}").ToList();
        var allLastScanTimeSettings = await db.AppSettings
            .Where(s => lastScanTimeKeys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => DateTime.Parse(s.Value), token);

        // ===== 3. Lặp qua từng Work Center gốc để query AS400 riêng =====
        foreach (var kvp in plansByBaseWc)
        {
            string baseWc = kvp.Key;
            List<string> moInWc = kvp.Value;

            if (token.IsCancellationRequested) break;

            // Lấy LastScanTime cho WC hiện tại, nếu chưa có thì quét 7 ngày gần nhất
            string currentKey = $"LastScanTime_{baseWc}";
            DateTime lastScanTime = allLastScanTimeSettings.TryGetValue(currentKey, out var time) ? time : DateTime.UtcNow.AddDays(-7);

            var allNewRowsForWc = new List<(string MO, string MX, string Item, string Wc, int Qty, DateTime ScanTime)>();
            DateTime latestScanTimeInBatch = lastScanTime;

            try
            {
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
                continue; // Lỗi WC này, tiếp tục với WC khác
            }

            if (allNewRowsForWc.Count == 0) continue;

            logger.LogInformation($"[AS400 Polling] Found {allNewRowsForWc.Count} new scans for WC {baseWc}.");

            // ===== 4. Cập nhật DB và broadcast SignalR cho WC này =====
            var updatedMoGroups = allNewRowsForWc.GroupBy(r => new { r.MO, BaseWc = NormalizeWcForAs400(r.Wc) });
            
            foreach (var group in updatedMoGroups)
            {
                string mo = group.Key.MO;
                string wc = group.Key.BaseWc;

                foreach (var row in group)
                {
                    var logExists = await db.ScanLogs.AnyAsync(l => l.MO == row.MO && l.ScanTime == row.ScanTime && l.WorkCenter == row.Wc, token);
                    if (!logExists)
                    {
                        db.ScanLogs.Add(new ScanLog { MO = row.MO, Item = row.Item, WorkCenter = row.Wc, Qty = row.Qty, ScanTime = row.ScanTime });
                    }
                }
                await db.SaveChangesAsync(token);

                var logsForMo = await db.ScanLogs.Where(s => s.MO == mo).ToListAsync(token);
                var logsForMoAndBaseWc = logsForMo.Where(s => NormalizeWcForAs400(s.WorkCenter) == wc).ToList();

                int totalQty = logsForMoAndBaseWc.Sum(s => s.Qty);
                DateTime? lastScanTimeForPair = logsForMoAndBaseWc.OrderByDescending(s => s.ScanTime).Select(s => (DateTime?)s.ScanTime).FirstOrDefault();

                var relatedMp = (await db.MoProgresses.Where(m => m.MO == mo).ToListAsync(token)).Where(m => NormalizeWcForAs400(m.WorkCenter) == wc).ToList();
                
                foreach (var mp in relatedMp)
                {
                    mp.ActualQty = totalQty;
                    mp.LastScanTime = lastScanTimeForPair;
                    mp.Status = AppHelpers.ComputeStatus(mp);
                }
                if (relatedMp.Any()) await db.SaveChangesAsync(token);

                // Bắn SignalR cho từng WC chi tiết
                foreach (var mp in relatedMp)
                {
                    await hubContext.Clients.All.SendAsync("MoProgressUpdated", new
                    {
                        mo = mp.MO,
                        mx = mp.MX,
                        workCenter = mp.WorkCenter,   // Gửi WC chi tiết
                        planned = mp.PlannedQty,
                        actual = mp.ActualQty,
                        status = mp.Status,
                        lastScanTime = mp.LastScanTime?.ToString("yyyy-MM-dd HH:mm:ss")
                    }, token);
                }
            }
            
            // ===== 5. Cập nhật LastScanTime cho WC này =====
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
record PartMappingData { public int Order { get; set; } public string PartName { get; set; } = ""; public int ColumnIndex { get; set; } }
record TrackingData(string Mx, List<WorkCenterStep> Steps);
record WorkCenterStep(string Mx, string WorkCenter, string FgItem, string Mo, string Qty, string Leadtime);
record Kho2ScanRequest(string Odrno, string ZoneCode);

// ==================== GLOBAL HELPER FUNCTIONS ====================
public static class AppHelpers
{
    public static string ComputeStatus(MoProgress mp)
    {
        if (mp.ActualQty <= 0) return "pending";
        if (mp.ActualQty < mp.PlannedQty) return "in-progress";

        if (mp.LastScanTime.HasValue)
        {
            try
            {
                if (string.IsNullOrEmpty(mp.LeadtimeString) ||
                    !mp.LeadtimeString.Contains('-')) return "done";

                var parts = mp.LeadtimeString.Split('-');
                var endStr = parts[1].Trim();
                var endParts = endStr.Split(':');
                int endHour = int.Parse(endParts[0]);
                int endMin = int.Parse(endParts[1]);

                DateTime targetDate = mp.LastScanTime.Value.Date;
                DateTime leadtimeEnd = targetDate.AddHours(endHour).AddMinutes(endMin);

                var startParts = parts[0].Trim().Split(':');
                int startHour = int.Parse(startParts[0]);

                if (endHour < startHour &&
                    mp.LastScanTime.Value.TimeOfDay.TotalHours >= startHour)
                {
                    leadtimeEnd = leadtimeEnd.AddDays(1);
                }

                return mp.LastScanTime.Value <= leadtimeEnd ? "done" : "late";
            }
            catch { return "done"; }
        }
        return "done";
    }
}
