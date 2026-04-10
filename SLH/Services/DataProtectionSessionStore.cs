using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using SLH.Models;

namespace SLH.Services;

public sealed class DataProtectionSessionStore : ISecureSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IDataProtector _protector;

    public DataProtectionSessionStore(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("SLH.Session");
    }

    public StoredSession? Load()
    {
        if (!File.Exists(AppPaths.TokenPath))
            return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(AppPaths.TokenPath);
            var json = _protector.Unprotect(Encoding.UTF8.GetString(protectedBytes));
            return JsonSerializer.Deserialize<StoredSession>(json, JsonOptions);
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
        var bytes = Encoding.UTF8.GetBytes(_protector.Protect(json));
        File.WriteAllBytes(AppPaths.TokenPath, bytes);
    }

    public void Clear()
    {
        if (File.Exists(AppPaths.TokenPath))
            File.Delete(AppPaths.TokenPath);
    }
}
