# Kho WIP B3 � L2

## �u?ng d?n v� DB ri�ng

- Trang kho: `/wip-b3-l2/index.html` (ho?c `/wip-b3-l2`). Trang ch? nh� m�y ? B3 ? L2 ? **KHO WIP B3 � L2**.
- Dashboard: `/wip-b3-l2/dashboard.html`.
- API ri�ng: `/api/wip-b3-l2/*`.
- DB: `<ContentRoot c?a server>/Data/WipB3L2.db`, du?c t?o khi module du?c truy c?p l?n d?u. T�i kho?n ch?y server c?n quy?n ghi thu m?c Data.
- Module kh�ng d�ng `AppDbContext`, `BlowFillDbContext`, `ToolManagementDbContext`, kh�ng d�ng connection string c?a c�c DB cu v� kh�ng ch?y migration l�n ch�ng.
- `Program.cs` ch? b? sung dang k� service v� �nh x? endpoint. Kh�ng thay d?i logic kh?i t?o DB cu c� s?n trong server.

## C�ch luu an to�n v?i nhi?u tab/m�y

T?t c? thao t�c nh?p, xu?t, chuy?n v? tr� di qua server. M?i giao d?ch SQLite ghi t?n kho, l?ch s? v� m� giao d?ch c�ng nhau. Giao d?ch ch? tr? th�nh c�ng sau khi commit. WAL v� synchronous=FULL du?c b?t cho ri�ng DB kho m?i.

Server ki?m tra MO tr�ng (kh�ng ph�n bi?t hoa/thu?ng), cho ph�p t?i da 999 MO/v? tr� v� ch?n k? tr?ng trong transaction. Scan m?i v� chuy?n k? ch? d�ng line C, D, E, F, G; c�c t?n cu ? line kh�c v?n hi?n th? d? d?i chi?u. Nh?p nhi?u xe v� xu?t nhi?u MO c�ng th�nh c�ng ho?c c�ng ho�n t�c.

M?i l?n n?m trong kho c� ID ri�ng: tab gi? d? li?u cu kh�ng th? xu?t nh?m m?t lu?t nh?p m?i c?a c�ng MO. L?ch s? OUT du?c ghi tru?c khi x�a t?n trong c�ng transaction, n�n xu?t MO cu?i c�ng v?n c� m� MO.

Kh�ng c� API ghi d� to�n b? snapshot. Tr�nh duy?t kh�ng luu t?n kho/log v�o localStorage ho?c IndexedDB. Tab c?p nh?t tr?ng th�i m?i 3 gi�y khi dang hi?n th? v� khi quay l?i tab. ��ng tab kh�ng ghi t?n kho.

M� giao d?ch v� n?i dung y�u c?u chua x�c nh?n du?c luu t?m trong sessionStorage c?a tab. N?u m?t ph?n h?i sau khi server d� luu, g?i l?i c�ng m� ch? tr? k?t qu?, kh�ng ghi th�m IN/OUT. Khi c� y�u c?u chua x�c nh?n, giao di?n ch?n scan ti?p v� hi?n th? **Ki?m tra / g?i l?i**. Kh�ng d�ng tab khi giao d?ch v?n dang ch?; n?u tab d� d�ng, ki?m tra tr?ng th�i MO/log t? server tru?c khi thao t�c ti?p.

Kh�ng c� ghi kho ngo?i tuy?n. Khi m?t m?ng, giao di?n th�ng b�o chua x�c nh?n thay v� b�o th�nh c�ng. Trang t?i l?i v?n l?y t?n kho t? DB server.

## Manager: x�a v� ho�n t�c line

Dashboard ch? cho ph�p xu?t h�ng lo?t ? C�G. L?n d?u d�ng m?t kh?u manager l� `1122`; d?i ngay sau khi tri?n khai qua n�t **�?i m?t kh?u** ? dashboard. M?t kh?u m?i du?c bam v� luu ri�ng t?i `Data/WipB3L2.manager.json`, kh�ng n?m trong DB v� kh�ng du?c g?i l?i tr�nh duy?t.

X�a line y�u c?u m?t kh?u, r?i server ki?m tra l?i ch�nh x�c danh s�ch MO v?a x�c nh?n tru?c khi ghi OUT. N?u c� tab kh�c thay d?i line trong th?i gian x�c nh?n, thao t�c b? t? ch?i d? ngu?i d�ng t?i l?i v� x�c nh?n l?i. Ho�n t�c cung c?n m?t kh?u, ch? d�ng du?c m?t l?n, kh�i ph?c t? giao d?ch d� luu server v� ghi `RESTORE` v�o l?ch s?. Kh�ng d�ng b?n snapshot ri�ng trong tr�nh duy?t.

## Chuy?n d? li?u t? b?n index.html cu

1. D?ng scan ? b?n cu, x�c d?nh tab c� danh s�ch d�ng. Kh�ng d�ng tab cu kh�c d? tr�nh k�ch ho?t l?i ghi d� c?a b?n cu.
2. ? tab d�ng, d�ng **Sao luu WIP**, ch?n thu m?c v� l?y `wip-backup.json`. Ki?m tra file c� d? MO tru?c khi ti?p t?c.
3. Khi DB kho server c�n m?i, chua c� giao d?ch: m? trang kho server, xu?ng cu?i trang, ch?n **Chuy?n t?n kho cu (JSON)** v� ch?n file.
4. �?i chi?u s? lu?ng v� v? tr�. Sau d� ch? thao t�c qua d?a ch? server, kh�ng d�ng ti?p b?n file `index.html` cu.

Import ki?m tra to�n b? file, gi? ng�y nh?p g?c d? t�nh t?n l�u, ghi l?ch s? `IMPORT` t?i th?i di?m chuy?n. Kh�ng t?o gi? log IN/OUT qu� kh?. File t?n kho kh�ng ch?a l?ch s? IndexedDB cu; n?u c?n gi? l?ch s? cu, xu?t Excel t? d�ng tr�nh duy?t cu d? luu ri�ng tru?c khi chuy?n.

Import ch? du?c ph�p khi DB chua c� giao d?ch; kh�ng th? d�ng file cu d? ghi d� m?t kho dang ho?t d?ng. N?u d� scan th? v�o DB s?n xu?t, kh�ng x�a DB d? import m� c?n d?i chi?u/chuy?n d? li?u c� ki?m so�t.

## Xu?t log v� sao luu

- **Xu?t Log**: d? tr?ng xu?t to�n b? l?ch s?, ho?c nh?p `YYYY-MM`. Excel g?m l?ch s? chung, IN, OUT; IMPORT/MOVE n?m ? l?ch s? chung. Th?i gian xu?t v� ph�n th�ng d�ng UTC+7.
- **Sao luu WIP** t?i b?n SQLite ch?a d?y d? t?n kho, log v� m� giao d?ch t? `/api/wip-b3-l2/backup`. D�ng SQLite Backup API n�n bao g?m c? d? li?u d� commit trong WAL.
- Luu b?n backup d?nh k? ? v? tr� kh�c m�y ch?; gi? nhi?u phi�n b?n. B?n n�y chua c�i l?ch sao luu t? d?ng tr�n h? di?u h�nh.
- Khi ph?c h?i: d?ng server, gi? b?n sao c�c file kho hi?n t?i r?i ph?c h?i **ch? DB WipB3L2**, x? l� WAL/SHM di k�m trong l�c server d� d?ng. Kh�ng ch�p d� file `.db` dang m? ho?c l?y ri�ng `.db` khi WAL c�n ho?t d?ng. Kh�ng d?ng c�c DB kh�c.
- Kh�ng c� n�t x�a l?ch s? hay co ch? t? x�a MO theo th?i gian trong module m?i.

## Tri?n khai v�o server dang v?n h�nh

1. Build/publish ? thu m?c staging, kh�ng publish tr?c ti?p d� thu m?c dang ch?y. Gi? b?n build hi?n h�nh d? rollback.
2. �ua m� module v� giao di?n m?i v�o b?n ph�t h�nh. Gi? nguy�n c�c file DB hi?n c� v� c?u h�nh v?n h�nh t?i m�y ch?. Kh�ng tri?n khai DB th? nghi?m, kh�ng thay `appsettings.json` v?n h�nh b?ng c?u h�nh kh�c.
3. Backend m?i c?n du?c n?p khi ti?n tr�nh server kh?i d?ng l?i. Kh�ng ch? ch�p HTML/JS tru?c trong khi backend c�n b?n cu.
4. M? `/wip-b3-l2/index.html`, ki?m tra b�o k?t n?i. DB m?i du?c t?o d?c l?p khi truy c?p. Chuy?n t?n kho cu tru?c khi b?t d?u scan n?u c� nhu c?u gi? t?n.
5. Ki?m tra c? c�c trang cu. Module kh�ng s?a DB cu, nhung vi?c kh?i d?ng l?i v?n ch?y logic kh?i d?ng v?n c� c?a server.

Vi?c s?a m� ngu?n kh�ng t? c?p nh?t ti?n tr�nh dang ch?y. C�c ki?m th? ph�t tri?n d�ng host v� DB t?m, kh�ng ch?y `Program.cs` s?n xu?t d? th?.

## Ki?m th?

T? thu m?c CI Project:

```powershell
dotnet build Test/WipB3L2.Tests/WipB3L2.Tests.csproj -p:NuGetAudit=false
dotnet Test/WipB3L2.Tests/bin/Debug/net8.0/WipB3L2.Tests.dll
```

Test t?o DB trong TEMP: d?ng th?i nhi?u writer, retry, MO tr�ng, OUT cu?i c�ng, OUT cu sau khi MO nh?p l?i, rollback l�, nhi?u xe, restart, backup v� import v�o kho m?i.

Host ri�ng cho ki?m th? tr�nh duy?t:

```powershell
dotnet Test/WipB3L2.Tests/bin/Debug/net8.0/WipB3L2.Tests.dll --serve
```

Host n�y ch? nghe `127.0.0.1:15057`, ch? dang k� module kho, d�ng Data trong TEMP. `browser-test.cjs` ki?m tra Edge headless v?i playwright-core 1.51.1 d?t t?i `%TEMP%/wip-b3-l2-browser-tools/node_modules`; c?n ch?y tr�n DB test m?i. N� ch?n truy c?p HTTPS ngo�i d? ki?m tra kh? nang scan v� xu?t log kh�ng c?n CDN.
