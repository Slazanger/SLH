using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SLH.Models;

using System.Runtime.Versioning;

namespace SLH.Services;

[SupportedOSPlatform("windows")]
public sealed class SecureSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public StoredSession? Load()
    {
        if (!File.Exists(AppPaths.TokenPath))
            return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(AppPaths.TokenPath);
            var jsonBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredSession>(Encoding.UTF8.GetString(jsonBytes), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(StoredSession session)
    {
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
        var json = JsonSerializer.Serialize(session, JsonOptions);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(AppPaths.TokenPath, bytes);
    }

    public void Clear()
    {
        if (File.Exists(AppPaths.TokenPath))
            File.Delete(AppPaths.TokenPath);
    }
}
