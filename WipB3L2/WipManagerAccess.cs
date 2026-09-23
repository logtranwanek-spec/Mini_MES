using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OrderTrackingWeb.WipB3L2;

public sealed record WipPassword(string Password);

// Manager settings are separate from all database files. Passwords are never sent back to a browser.
public sealed class WipManagerAccess(string settingsPath)
{
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> sessions = new();
    private int failures;
    private DateTimeOffset blockedUntil;
    private sealed record PasswordHash(string Salt, string Hash);

    public string? Login(string? password)
    {
        lock (gate)
        {
            if (DateTimeOffset.UtcNow < blockedUntil || password == null || password.Length > 256) return null;
            bool valid;
            if (File.Exists(settingsPath))
            {
                var stored = JsonSerializer.Deserialize<PasswordHash>(File.ReadAllText(settingsPath))
                    ?? throw new InvalidOperationException("Manager settings are invalid.");
                var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(stored.Salt), 100000, HashAlgorithmName.SHA256, 32);
                valid = CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(stored.Hash));
            }
            else valid = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes("1122"));
            if (!valid)
            {
                if (++failures >= 5) { blockedUntil = DateTimeOffset.UtcNow.AddSeconds(30); failures = 0; }
                return null;
            }
            failures = 0;
            foreach (var key in sessions.Where(s => s.Value <= DateTimeOffset.UtcNow).Select(s => s.Key).ToArray()) sessions.Remove(key);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            sessions[token] = DateTimeOffset.UtcNow.AddMinutes(30);
            return token;
        }
    }

    public bool IsAuthorized(string authorization)
    {
        lock (gate)
            return authorization.StartsWith("Bearer ", StringComparison.Ordinal) &&
                sessions.TryGetValue(authorization[7..], out var expires) && expires > DateTimeOffset.UtcNow;
    }

    public void ChangePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length is < 2 or > 256)
            throw new ArgumentException("Mật khẩu cần từ 2 đến 256 ký tự.");
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var salt = RandomNumberGenerator.GetBytes(32);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100000, HashAlgorithmName.SHA256, 32);
            var temp = settingsPath + "." + Guid.NewGuid() + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(new PasswordHash(Convert.ToBase64String(salt), Convert.ToBase64String(hash))));
                File.Move(temp, settingsPath, overwrite: true);
                sessions.Clear();
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
