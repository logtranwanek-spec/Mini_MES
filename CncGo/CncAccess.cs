using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace OrderTrackingWeb.CncGo;

/// <summary>Session access for CNC-GO. Passwords are kept outside the database.</summary>
public sealed class CncAccess
{
    private readonly ConcurrentDictionary<string, (string Role, DateTime Expires)> _sessions = new();
    private readonly string _managerPassword = Environment.GetEnvironmentVariable("CNC_MANAGER_PASSWORD") ?? "1122";
    private readonly string _leadPassword = Environment.GetEnvironmentVariable("CNC_LEAD_PASSWORD") ?? "1122";

    public string? Login(string role, string password)
    {
        role = (role ?? "").Trim().ToLowerInvariant();
        var expected = role == "manager" ? _managerPassword : role is "lead" or "supervisor" ? _leadPassword : null;
        if (expected == null || string.IsNullOrWhiteSpace(password) || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password)),
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected)))) return null;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = (role, DateTime.UtcNow.AddHours(8));
        return token;
    }

    public bool HasRole(HttpRequest request, string requiredRole)
    {
        var value = request.Headers.Authorization.FirstOrDefault();
        var token = value?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? value[7..].Trim() : "";
        if (!_sessions.TryGetValue(token, out var session) || session.Expires <= DateTime.UtcNow) { _sessions.TryRemove(token, out _); return false; }
        return session.Role == requiredRole ||
               (requiredRole == "lead" && session.Role == "supervisor") ||
               session.Role == "manager";
    }
}
