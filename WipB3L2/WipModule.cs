using Microsoft.Data.Sqlite;
using ClosedXML.Excel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrderTrackingWeb.WipB3L2;

// This module never opens or migrates any of the existing application databases.
public static class WipModule
{
    public static IServiceCollection AddB3L2Wip(this IServiceCollection services, string contentRoot)
        => services.AddSingleton(new WipStore(Path.Combine(contentRoot, "Data", "WipB3L2.db")))
            .AddSingleton(new WipManagerAccess(Path.Combine(contentRoot, "Data", "WipB3L2.manager.json")));

    public static void MapB3L2Wip(this WebApplication app)
    {
        app.MapGet("/wip-b3-l2", () => Results.Redirect("/wip-b3-l2/index.html"));
        var api = app.MapGroup("/api/wip-b3-l2");
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (WipConflict ex) { return Results.Json(new { error = ex.Message }, statusCode: 409); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (SqliteException ex)
            {
                app.Logger.LogError(ex, "B3 L2 WIP database operation failed");
                return Results.Json(new { error = "Không thể lưu kho. Vui lòng thử lại cùng giao dịch." }, statusCode: 503);
            }
        });
        api.MapGet("/state", (WipStore store) => store.GetState());
        api.MapGet("/product-zones", () => new { MO = "CDEFG", Cushion = "IK", Fiber = "AB", Decking = "MN" });
        api.MapGet("/revision", (WipStore store) => new { revision = store.GetRevision() });
        api.MapGet("/logs", (WipStore store, string? month, string? mo, bool? family) => store.GetLogs(month, mo, family == true));
        api.MapGet("/export", (WipStore store, string? month) =>
        {
            var logs = store.GetLogs(month);
            using var workbook = new XLWorkbook();
            foreach (var (name, action) in new[] { ("Lịch sử chung", ""), ("Lịch sử IN", "ADD"), ("Lịch sử OUT", "REMOVE") })
            {
                var sheet = workbook.Worksheets.Add(name);
                string[] headers = ["Thời gian (UTC+7)", "Hành động", "Vị trí", "Mã MO", "Thời gian lưu kho", "ID Card", "Mã giao dịch", "Vị trí cũ"];
                for (var col = 0; col < headers.Length; col++) sheet.Cell(1, col + 1).Value = headers[col];
                var row = 2;
                foreach (var log in logs.Where(l => action == "" || l.Action == action || (action == "ADD" && l.Action == "RESTORE")))
                {
                    sheet.Cell(row, 1).Value = DateTimeOffset.Parse(log.Timestamp).ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd HH:mm:ss");
                    sheet.Cell(row, 2).Value = log.Action == "ADD" ? "IN" : log.Action == "REMOVE" ? "OUT" : log.Action;
                    sheet.Cell(row, 3).Value = log.ShelfCode;
                    sheet.Cell(row, 4).Value = log.MoNumber;
                    sheet.Cell(row, 5).Value = log.Duration ?? "";
                    sheet.Cell(row, 6).Value = log.CardId;
                    sheet.Cell(row, 7).Value = log.RequestId;
                    sheet.Cell(row, 8).Value = log.OldShelfCode ?? "";
                    row++;
                }
                sheet.Row(1).Style.Font.Bold = true;
                sheet.Columns().Width = 24;
            }
            using var stream = new MemoryStream(); workbook.SaveAs(stream);
            return Results.File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"WipB3L2-{month ?? "all"}.xlsx");
        });
        api.MapPost("/manager/login", (WipPassword password, WipManagerAccess access) =>
        {
            var token = access.Login(password.Password);
            return token == null ? Results.Json(new { error = "Sai mật khẩu hoặc đã thử quá nhiều lần. Thử lại sau." }, statusCode: 401)
                : Results.Ok(new { token });
        });
        api.MapPost("/manager/password", (WipPassword password, WipManagerAccess access, HttpContext context) =>
        {
            if (!access.IsAuthorized(context.Request.Headers.Authorization.ToString())) return Results.Unauthorized();
            access.ChangePassword(password.Password);
            return Results.Ok(new { changed = true });
        });
        api.MapGet("/undo", (WipStore store) => Results.Json(store.GetUndo()));
        api.MapPost("/commands", (WipCommand command, WipStore store, WipManagerAccess access, HttpContext context) =>
        {
            if (command.Action is "clear-line" or "undo-line" && !access.IsAuthorized(context.Request.Headers.Authorization.ToString()))
                return Results.Json(new { error = "Cần xác thực manager trước khi xóa line hoặc hoàn tác." }, statusCode: 401);
            return Results.Ok(store.Execute(command));
        });
        api.MapGet("/backup", (WipStore store) => Results.File(store.Backup(), "application/octet-stream",
            $"WipB3L2-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db"));
    }
}

public sealed record WipCommand(string RequestId, string Action, string? MoNumber = null,
    string? ShelfCode = null, string? CardId = null, int VehicleCount = 1,
    List<WipRemoval>? Items = null, List<WipImportCard>? Cards = null, string? EntryId = null,
    string? Line = null, string? UndoRequestId = null, string? ProductType = null);
public sealed record WipRemoval(string CardId, string MoNumber, string EntryId);
public sealed record WipImportCard(string ShelfCode, List<string> MoNumbers, string CreatedAt);
public sealed record WipVehicle(string BaseMO, int VehicleNumber, int TotalVehicles);
public sealed record WipCard(string Id, string ShelfCode, List<string> MoNumbers, string CreatedAt,
    Dictionary<string, string> MoCreatedAt, WipVehicle? VehicleInfo, Dictionary<string, string> MoEntryIds);
public sealed record WipState(long Revision, List<WipCard> Cards);
public sealed record WipResult(long Revision, List<WipCard> Cards, List<string> AffectedCardIds, bool Replayed);
public sealed record WipLog(long Id, string EntryId, string Timestamp, string Month, string Action,
    string ShelfCode, string MoNumber, string CardId, string? Duration, string? OldShelfCode, string RequestId);
internal sealed record Entry(string Id, string CardId, string Shelf, string Mo, string CreatedAt,
    string? BaseMO, int VehicleNumber, int TotalVehicles);
internal sealed record ClearedLine(WipCommand Command, List<Entry> RemovedEntries, string SavedAt);
public sealed record WipUndo(string RequestId, string Line, int TotalMOs, int TotalCards, string SavedAt);
public sealed class WipConflict(string message) : Exception(message);

public sealed class WipStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private bool _initialized;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly int[] Capacities = [76,76,76,76,74,76,76,76,91,76,90,76,76,76,76,76];
    public const int MaxMOsPerShelf = 999;
    private static readonly string[] Shelves = Capacities.SelectMany((count, i) =>
        Enumerable.Range(1, count).Select(n => $"{(char)('A' + i)}-{n:00}")).ToArray();
    // Keep automatic kit placement in the established area until product areas are assigned.
    private static readonly string[] AutoAssignShelves = Shelves.Where(s => s[0] is >= 'C' and <= 'G').ToArray();

    public WipStore(string databasePath) => _path = Path.GetFullPath(databasePath);

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 15, Pooling = false
        }.ToString());
        db.Open();
        try
        {
            if (!_initialized)
            {
                Run(db, null, "PRAGMA journal_mode=WAL;");
                Run(db, null, """
                    CREATE TABLE IF NOT EXISTS Entries (
                        Id TEXT PRIMARY KEY, CardId TEXT NOT NULL, Shelf TEXT NOT NULL,
                        Mo TEXT NOT NULL COLLATE NOCASE UNIQUE, CreatedAt TEXT NOT NULL,
                        BaseMO TEXT NULL, VehicleNumber INTEGER NOT NULL, TotalVehicles INTEGER NOT NULL);
                    CREATE INDEX IF NOT EXISTS IX_Entries_Shelf ON Entries(Shelf);
                    CREATE TABLE IF NOT EXISTS Logs (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT, EntryId TEXT NOT NULL,
                        Timestamp TEXT NOT NULL, Month TEXT NOT NULL, Action TEXT NOT NULL,
                        ShelfCode TEXT NOT NULL, MoNumber TEXT NOT NULL, CardId TEXT NOT NULL,
                        Duration TEXT NULL, OldShelfCode TEXT NULL, RequestId TEXT NOT NULL);
                    CREATE INDEX IF NOT EXISTS IX_Logs_Mo ON Logs(MoNumber);
                    CREATE INDEX IF NOT EXISTS IX_Logs_Month ON Logs(Month);
                    CREATE TABLE IF NOT EXISTS Commands (
                        Revision INTEGER PRIMARY KEY AUTOINCREMENT, RequestId TEXT NOT NULL UNIQUE,
                        Payload TEXT NOT NULL, Affected TEXT NOT NULL);
                    """);
                // Replace the old global MO uniqueness with uniqueness inside each product area.
                using (var migration = db.BeginTransaction())
                {
                    using var schema = Command(db, migration, "SELECT sql FROM sqlite_master WHERE type='table' AND name='Entries'");
                    if ((schema.ExecuteScalar()?.ToString() ?? "").Contains("COLLATE NOCASE UNIQUE", StringComparison.OrdinalIgnoreCase))
                    {
                        Run(db, migration, """
                            CREATE TABLE EntriesByArea (
                                Id TEXT PRIMARY KEY, CardId TEXT NOT NULL, Shelf TEXT NOT NULL,
                                Mo TEXT NOT NULL COLLATE NOCASE, CreatedAt TEXT NOT NULL,
                                BaseMO TEXT NULL, VehicleNumber INTEGER NOT NULL, TotalVehicles INTEGER NOT NULL);
                            INSERT INTO EntriesByArea SELECT * FROM Entries;
                            DROP TABLE Entries;
                            ALTER TABLE EntriesByArea RENAME TO Entries;
                            CREATE INDEX IX_Entries_Shelf ON Entries(Shelf);
                            """);
                    }
                    Run(db, migration, """
                        CREATE UNIQUE INDEX IF NOT EXISTS IX_Entries_Area_Mo ON Entries (
                            CASE substr(Shelf,1,1)
                                WHEN 'C' THEN 'MO' WHEN 'D' THEN 'MO' WHEN 'E' THEN 'MO' WHEN 'F' THEN 'MO' WHEN 'G' THEN 'MO'
                                WHEN 'I' THEN 'Cushion' WHEN 'K' THEN 'Cushion'
                                WHEN 'A' THEN 'Fiber' WHEN 'B' THEN 'Fiber'
                                WHEN 'M' THEN 'Decking' WHEN 'N' THEN 'Decking'
                                ELSE substr(Shelf,1,1) END, Mo COLLATE NOCASE);
                        """);
                    migration.Commit();
                }
                _initialized = true;
            }
            Run(db, null, "PRAGMA synchronous=FULL;");
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object? Value)[] values)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    private static void Run(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object? Value)[] values)
    { using var cmd = Command(db, tx, sql, values); cmd.ExecuteNonQuery(); }

    private static List<Entry> Entries(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = Command(db, tx, "SELECT Id,CardId,Shelf,Mo,CreatedAt,BaseMO,VehicleNumber,TotalVehicles FROM Entries ORDER BY Shelf,CreatedAt,Id");
        using var reader = cmd.ExecuteReader(); var entries = new List<Entry>();
        while (reader.Read()) entries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7)));
        return entries;
    }
    private static WipState State(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = Command(db, tx, "SELECT COALESCE(MAX(Revision),0) FROM Commands");
        var revision = Convert.ToInt64(cmd.ExecuteScalar());
        var cards = Entries(db, tx).GroupBy(e => e.CardId).Select(g => {
            var first = g.First();
            return new WipCard(first.CardId, first.Shelf, g.Select(e => e.Mo).ToList(), first.CreatedAt,
                g.ToDictionary(e => e.Mo, e => e.CreatedAt), first.BaseMO == null ? null :
                    new WipVehicle(first.BaseMO, first.VehicleNumber, first.TotalVehicles), g.ToDictionary(e => e.Mo, e => e.Id));
        }).ToList();
        return new(revision, cards);
    }
    public WipState GetState()
    {
        lock (_gate) { using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
            var state = State(db, tx); tx.Commit(); return state; }
    }

    public long GetRevision()
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = Command(db, null, "SELECT COALESCE(MAX(Revision),0) FROM Commands");
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    private static string Mo(string? value)
    {
        var mo = value?.Trim().ToUpperInvariant() ?? "";
        if (!Regex.IsMatch(mo, @"^[A-Z0-9][A-Z0-9._/\-]{1,99}$"))
            throw new ArgumentException("Mã MO phải có 2–100 ký tự: chữ, số, dấu chấm, gạch ngang, gạch dưới hoặc /.");
        return mo;
    }
    private static string Area(string shelf) => shelf[0] switch
    {
        'C' or 'D' or 'E' or 'F' or 'G' => "MO",
        'I' or 'K' => "Cushion", 'A' or 'B' => "Fiber", 'M' or 'N' => "Decking",
        _ => shelf[..1]
    };
    private static string ProductArea(string? type) => type switch
    {
        null or "MO" => "MO", "Cushion" => "Cushion", "Fiber" => "Fiber", "Decking" => "Decking",
        _ => throw new ArgumentException("Loại hàng không hợp lệ.")
    };
    private static string Shelf(string? value)
    {
        var shelf = value?.Trim().ToUpperInvariant() ?? "";
        if (!Shelves.Contains(shelf)) throw new ArgumentException("Vị trí kệ không hợp lệ.");
        return shelf;
    }

    public WipResult Execute(WipCommand request)
    {
        if (!Guid.TryParse(request.RequestId, out _)) throw new ArgumentException("Thiếu mã giao dịch hợp lệ.");
        var payload = JsonSerializer.Serialize(request, Json);
        lock (_gate)
        {
            using var db = Open();
            // Immediate SQLite transaction serializes writers even across server processes.
            using var tx = db.BeginTransaction(deferred: false);
            using (var previous = Command(db, tx, "SELECT Payload,Affected FROM Commands WHERE RequestId=$id", ("$id", request.RequestId)))
            using (var reader = previous.ExecuteReader())
            {
                if (reader.Read())
                {
                    using var stored = JsonDocument.Parse(reader.GetString(0));
                    var original = stored.RootElement.TryGetProperty("command", out var nested) ? nested : stored.RootElement;
                    if (JsonSerializer.Serialize(original.Deserialize<WipCommand>(Json), Json) != payload)
                        throw new WipConflict("Mã giao dịch đã được dùng cho thao tác khác.");
                    var affected = JsonSerializer.Deserialize<List<string>>(reader.GetString(1), Json)!;
                    reader.Close();
                    var current = State(db, tx); tx.Commit();
                    return new(current.Revision, current.Cards, affected, true);
                }
            }
            var entries = Entries(db, tx);
            var affectedIds = new HashSet<string>();
            var now = DateTimeOffset.UtcNow;

            void LogEntry(Entry entry, string action, string? oldShelf = null)
            {
                string? duration = null;
                if (action == "REMOVE")
                {
                    var elapsed = now - DateTimeOffset.Parse(entry.CreatedAt, CultureInfo.InvariantCulture);
                    duration = $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
                }
                Run(db, tx, """
                    INSERT INTO Logs(EntryId,Timestamp,Month,Action,ShelfCode,MoNumber,CardId,Duration,OldShelfCode,RequestId)
                    VALUES($entry,$time,$month,$action,$shelf,$mo,$card,$duration,$old,$request)
                    """, ("$entry", entry.Id), ("$time", now.ToString("O")),
                    ("$month", now.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM")), ("$action", action),
                    ("$shelf", entry.Shelf), ("$mo", entry.Mo), ("$card", entry.CardId),
                    ("$duration", duration), ("$old", oldShelf), ("$request", request.RequestId));
                affectedIds.Add(entry.CardId);
            }
            void Add(string mo, string? shelf, string createdAt, string? baseMo = null, int vehicle = 0, int total = 0, bool imported = false, bool restored = false)
            {
                mo = Mo(mo);
                var allowedLines = request.ProductType switch
                {
                    null or "MO" => "CDEFG",
                    "Cushion" => "IK",
                    "Fiber" => "AB",
                    "Decking" => "MN",
                    _ => throw new ArgumentException("Loại hàng không hợp lệ.")
                };
                shelf = shelf == null ? Shelves.FirstOrDefault(s => allowedLines.Contains(s[0]) && entries.All(e => e.Shelf != s)) : Shelf(shelf);
                if (!imported && !restored && request.ProductType != null && shelf != null && !allowedLines.Contains(shelf[0]))
                    throw new ArgumentException("Vị trí không thuộc khu của loại hàng đã chọn.");
                if (shelf == null) throw new WipConflict("Kho đã hết vị trí trống.");
                if (entries.Any(e => e.Mo == mo && Area(e.Shelf) == Area(shelf)))
                    throw new WipConflict($"MO {mo} đã có trong khu {Area(shelf)}.");
                var group = entries.Where(e => e.Shelf == shelf).ToList();
                if (group.Count >= MaxMOsPerShelf) throw new WipConflict($"Vị trí {shelf} đã đủ {MaxMOsPerShelf} MO.");
                var entry = new Entry(Guid.NewGuid().ToString(), group.FirstOrDefault()?.CardId ?? "card-" + Guid.NewGuid(),
                    shelf, mo, createdAt, baseMo, vehicle, total);
                Run(db, tx, "INSERT INTO Entries VALUES($id,$card,$shelf,$mo,$time,$base,$vehicle,$total)",
                    ("$id", entry.Id), ("$card", entry.CardId), ("$shelf", shelf), ("$mo", mo),
                    ("$time", createdAt), ("$base", baseMo), ("$vehicle", vehicle), ("$total", total));
                entries.Add(entry); LogEntry(entry, imported ? "IMPORT" : restored ? "RESTORE" : "ADD");
            }
            void Remove(string? cardId, string? mo, string? entryId)
            {
                mo = Mo(mo);
                var entry = entries.FirstOrDefault(e => e.CardId == cardId && e.Mo == mo && e.Id == entryId)
                    ?? throw new WipConflict($"MO {mo} đã được xuất hoặc đã thay đổi. Danh sách sẽ được cập nhật lại.");
                LogEntry(entry, "REMOVE");
                Run(db, tx, "DELETE FROM Entries WHERE Id=$id", ("$id", entry.Id)); entries.Remove(entry);
            }
            switch (request.Action)
            {
                case "add":
                {
                    var requestedMo = Mo(request.MoNumber);
                    if (Regex.IsMatch(requestedMo, @"-XE[1-9]\d*$", RegexOptions.CultureInvariant))
                    {
                        Add(requestedMo, request.ShelfCode, now.ToString("O"));
                        break;
                    }

                    var positionVehiclePattern = new Regex("^" + Regex.Escape(requestedMo) + @"-XE([1-9]\d*)$", RegexOptions.CultureInvariant);
                    var targetArea = request.ShelfCode == null ? ProductArea(request.ProductType) : Area(Shelf(request.ShelfCode));
                    var positionFamily = entries.Where(e => Area(e.Shelf) == targetArea && (e.Mo == requestedMo || e.BaseMO == requestedMo || positionVehiclePattern.IsMatch(e.Mo))).ToList();
                    if (positionFamily.Count == 0)
                    {
                        Add(requestedMo, request.ShelfCode, now.ToString("O"));
                        break;
                    }

                    var usedVehicleNumbers = positionFamily.Select(e => positionVehiclePattern.Match(e.Mo))
                        .Where(match => match.Success)
                        .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
                        .ToHashSet();
                    var assignedVehicleNumbers = new Dictionary<string, int>();
                    foreach (var existing in positionFamily)
                    {
                        var match = positionVehiclePattern.Match(existing.Mo);
                        if (match.Success)
                        {
                            assignedVehicleNumbers[existing.Id] = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                            continue;
                        }
                        var vehicleNumber = 1;
                        while (usedVehicleNumbers.Contains(vehicleNumber)) vehicleNumber++;
                        usedVehicleNumbers.Add(vehicleNumber);
                        assignedVehicleNumbers[existing.Id] = vehicleNumber;
                    }

                    var newVehicleNumber = assignedVehicleNumbers.Values.Max() + 1;
                    foreach (var existing in positionFamily)
                    {
                        var vehicleNumber = assignedVehicleNumbers[existing.Id];
                        var vehicleMo = $"{requestedMo}-XE{vehicleNumber}";
                        Run(db, tx, "UPDATE Entries SET Mo=$mo,BaseMO=$base,VehicleNumber=$vehicle,TotalVehicles=$total WHERE Id=$id",
                            ("$mo", vehicleMo), ("$base", requestedMo), ("$vehicle", vehicleNumber), ("$total", newVehicleNumber), ("$id", existing.Id));
                        Run(db, tx, "UPDATE Logs SET MoNumber=$mo WHERE EntryId=$id", ("$mo", vehicleMo), ("$id", existing.Id));
                        var index = entries.FindIndex(e => e.Id == existing.Id);
                        entries[index] = existing with { Mo = vehicleMo, BaseMO = requestedMo, VehicleNumber = vehicleNumber, TotalVehicles = newVehicleNumber };
                    }
                    Add($"{requestedMo}-XE{newVehicleNumber}", request.ShelfCode, now.ToString("O"), requestedMo, newVehicleNumber, newVehicleNumber);
                    break;
                }
                case "add-multiple":
                    if (request.VehicleCount < 1 || request.VehicleCount > 10) throw new ArgumentException("Số xe phải từ 1 đến 10.");
                    var baseMo = Mo(request.MoNumber);
                    // Older records created before vehicle metadata existed have BaseMO = NULL.
                    // Their visible suffix still identifies them as part of this MO family.
                    var vehicleNamePattern = new Regex("^" + Regex.Escape(baseMo) + @"-XE([1-9]\d*)$", RegexOptions.CultureInvariant);
                    var family = entries.Where(e => Area(e.Shelf) == ProductArea(request.ProductType) && (e.Mo == baseMo || e.BaseMO == baseMo || vehicleNamePattern.IsMatch(e.Mo))).ToList();
                    if (family.Count == 0)
                    {
                        // One vehicle keeps the original MO; selecting 2+ creates the complete vehicle set.
                        if (request.VehicleCount == 1) Add(baseMo, null, now.ToString("O"));
                        else for (var n = 1; n <= request.VehicleCount; n++)
                            Add($"{baseMo}-XE{n}", null, now.ToString("O"), baseMo, n, request.VehicleCount);
                        break;
                    }

                    // The dialog value is the number of newly arriving vehicles. Keep the existing family and append new vehicles.
                    var namedVehicleNumbers = family.Select(e => vehicleNamePattern.Match(e.Mo))
                        .Where(match => match.Success)
                        .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
                        .ToHashSet();
                    var vehicleNumbers = new Dictionary<string, int>();
                    foreach (var existing in family)
                    {
                        var match = vehicleNamePattern.Match(existing.Mo);
                        if (match.Success)
                        {
                            vehicleNumbers[existing.Id] = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                            continue;
                        }

                        // A legacy unsuffixed MO is XE1 unless XE1 is already occupied.
                        var vehicleNumber = 1;
                        while (namedVehicleNumbers.Contains(vehicleNumber)) vehicleNumber++;
                        namedVehicleNumbers.Add(vehicleNumber);
                        vehicleNumbers[existing.Id] = vehicleNumber;
                    }
                    var highestExistingVehicle = vehicleNumbers.Values.Max();
                    var targetTotal = highestExistingVehicle + request.VehicleCount;
                    foreach (var existing in family)
                    {
                        var vehicleNumber = vehicleNumbers[existing.Id];
                        var vehicleMo = $"{baseMo}-XE{vehicleNumber}";
                        Run(db, tx, "UPDATE Entries SET Mo=$mo,BaseMO=$base,VehicleNumber=$vehicle,TotalVehicles=$total WHERE Id=$id",
                            ("$mo", vehicleMo), ("$base", baseMo), ("$vehicle", vehicleNumber), ("$total", targetTotal), ("$id", existing.Id));
                        var index = entries.FindIndex(e => e.Id == existing.Id);
                        entries[index] = existing with { Mo = vehicleMo, BaseMO = baseMo, VehicleNumber = vehicleNumber, TotalVehicles = targetTotal };
                    }

                    for (var n = highestExistingVehicle + 1; n <= targetTotal; n++)
                    {
                        Add($"{baseMo}-XE{n}", null, now.ToString("O"), baseMo, n, targetTotal);
                    }
                    break;                case "remove": Remove(request.CardId, request.MoNumber, request.EntryId); break;
                case "remove-batch":
                    if (request.Items == null || request.Items.Count is < 1 or > 100000) throw new ArgumentException("Danh sách xuất không hợp lệ.");
                    foreach (var item in request.Items) Remove(item.CardId, item.MoNumber, item.EntryId);
                    break;
                case "clear-line":
                    if (request.Line == null || !Regex.IsMatch(request.Line, "^[A-P]$")) throw new ArgumentException("Line không hợp lệ.");
                    var onLine = entries.Where(e => e.Shelf.StartsWith(request.Line + "-", StringComparison.Ordinal)).ToList();
                    if (onLine.Count == 0) throw new WipConflict("Line không có MO để xuất.");
                    if (request.Items == null || request.Items.Count != onLine.Count ||
                        !onLine.Select(e => new WipRemoval(e.CardId, e.Mo, e.Id)).ToHashSet().SetEquals(request.Items))
                        throw new WipConflict("Line đã thay đổi ở tab khác. Cập nhật và xác nhận lại danh sách trước khi xuất.");
                    payload = JsonSerializer.Serialize(new ClearedLine(request, onLine, now.ToString("O")), Json);
                    foreach (var entry in onLine) Remove(entry.CardId, entry.Mo, entry.Id);
                    break;
                case "undo-line":
                    if (!Guid.TryParse(request.UndoRequestId, out _)) throw new ArgumentException("Thiếu mã giao dịch cần hoàn tác.");
                    using (var used = Command(db, tx, "SELECT COUNT(*) FROM Commands WHERE json_extract(Payload,'$.undoRequestId')=$id", ("$id", request.UndoRequestId)))
                        if (Convert.ToInt64(used.ExecuteScalar()) > 0) throw new WipConflict("Lần xuất này đã được hoàn tác.");
                    ClearedLine? cleared;
                    using (var lookup = Command(db, tx, "SELECT Payload FROM Commands WHERE RequestId=$id AND json_extract(Payload,'$.command.action')='clear-line'", ("$id", request.UndoRequestId)))
                        cleared = lookup.ExecuteScalar() is string raw ? JsonSerializer.Deserialize<ClearedLine>(raw, Json) : null;
                    if (cleared == null) throw new WipConflict("Không tìm thấy giao dịch xuất line để hoàn tác.");
                    foreach (var entry in cleared.RemovedEntries)
                        Add(entry.Mo, entry.Shelf, entry.CreatedAt, entry.BaseMO, entry.VehicleNumber, entry.TotalVehicles, restored: true);
                    break;
                case "move":
                    var destination = Shelf(request.ShelfCode);
                    var moving = entries.Where(e => e.CardId == request.CardId).ToList();
                    if (moving.Count == 0) throw new WipConflict("Thẻ không còn trong kho.");
                    if (moving.Any(item => entries.Any(e => e.CardId != request.CardId && e.Mo == item.Mo && Area(e.Shelf) == Area(destination))))
                        throw new WipConflict("Khu đích đã có mã xe này. Không thể chuyển gây trùng mã.");
                    if (entries.Any(e => e.Shelf == destination && e.CardId != request.CardId)) throw new WipConflict("Kệ đích đang có MO.");
                    foreach (var entry in moving)
                    {
                        Run(db, tx, "UPDATE Entries SET Shelf=$shelf WHERE Id=$id", ("$shelf", destination), ("$id", entry.Id));
                        LogEntry(entry with { Shelf = destination }, "MOVE", entry.Shelf);
                    }
                    break;
                case "import":
                    // Initial migration only. Never overwrite a working warehouse or fabricate scan history.
                    using (var count = Command(db, tx, "SELECT COUNT(*) FROM Commands"))
                        if (Convert.ToInt64(count.ExecuteScalar()) != 0 || entries.Count != 0)
                            throw new WipConflict("Chỉ được nhập dữ liệu cũ vào kho mới chưa có giao dịch.");
                    if (request.Cards == null || request.Cards.Count is < 1 or > 1200) throw new ArgumentException("File tồn kho không hợp lệ.");
                    foreach (var card in request.Cards)
                    {
                        if (!DateTimeOffset.TryParse(card.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created)
                            || created > now || card.MoNumbers == null || card.MoNumbers.Count is < 1 or > MaxMOsPerShelf)
                            throw new ArgumentException("MO hoặc thời gian nhập kho trong file không hợp lệ.");
                        foreach (var mo in card.MoNumbers) Add(mo, Shelf(card.ShelfCode), created.ToString("O"), imported: true);
                    }
                    break;
                default: throw new ArgumentException("Thao tác kho không được hỗ trợ.");
            }
            Run(db, tx, "INSERT INTO Commands(RequestId,Payload,Affected) VALUES($id,$payload,$affected)",
                ("$id", request.RequestId), ("$payload", payload), ("$affected", JsonSerializer.Serialize(affectedIds, Json)));
            var result = State(db, tx);
            tx.Commit(); // Only report success once both inventory and audit log are durable.
            return new(result.Revision, result.Cards, affectedIds.ToList(), false);
        }
    }

    public List<WipLog> GetLogs(string? month = null, string? mo = null, bool family = false)
    {
        if (month != null && !Regex.IsMatch(month, @"^\d{4}-(0[1-9]|1[0-2])$")) throw new ArgumentException("Tháng không hợp lệ.");
        if (mo != null) mo = Mo(mo);
        lock (_gate)
        {
            using var db = Open();
            var familyPrefix = mo == null ? null : mo.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "-XE%";
            using var cmd = Command(db, null, """
                SELECT * FROM Logs
                WHERE ($month IS NULL OR Month=$month)
                  AND ($mo IS NULL OR MoNumber=$mo OR ($family=1 AND MoNumber LIKE $familyPrefix ESCAPE '\'))
                ORDER BY Id
                """,
                ("$month", month), ("$mo", mo), ("$family", family ? 1 : 0), ("$familyPrefix", familyPrefix));
            using var r = cmd.ExecuteReader(); var logs = new List<WipLog>();
            while (r.Read()) logs.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9), r.GetString(10)));
            return logs;
        }
    }

    public WipUndo? GetUndo()
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = Command(db, null, """
                SELECT c.RequestId,c.Payload FROM Commands c
                WHERE json_extract(c.Payload,'$.command.action')='clear-line'
                AND NOT EXISTS (SELECT 1 FROM Commands u WHERE json_extract(u.Payload,'$.undoRequestId')=c.RequestId)
                ORDER BY c.Revision DESC LIMIT 1
                """);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var cleared = JsonSerializer.Deserialize<ClearedLine>(reader.GetString(1), Json)!;
            return new(reader.GetString(0), cleared.Command.Line!, cleared.RemovedEntries.Count,
                cleared.RemovedEntries.Select(e => e.CardId).Distinct().Count(), cleared.SavedAt);
        }
    }

    public byte[] Backup()
    {
        lock (_gate)
        {
            var path = Path.Combine(Path.GetTempPath(), $"wip-b3-l2-{Guid.NewGuid()}.db");
            try
            {
                using (var source = Open())
                using (var target = new SqliteConnection($"Data Source={path};Pooling=False"))
                { target.Open(); source.BackupDatabase(target); }
                return File.ReadAllBytes(path);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
