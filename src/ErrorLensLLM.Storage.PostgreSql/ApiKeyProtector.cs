using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ErrorLensLLM.Storage.PostgreSql;

/// <summary>
/// Encrypts the API keys entered on the settings screen before they reach the database. The dashboard encrypts, the
/// collector decrypts; both use the same key ring on disk (<c>DataProtection:KeysPath</c>, by default ~/.errorlens/keys),
/// so a copy of the database alone does not reveal a key.
/// </summary>
public sealed class ApiKeyProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("ErrorLensLLM.LlmApiKeys");

    public string Protect(string apiKey) => _protector.Protect(apiKey);

    /// <summary>The plain key, or null when it was encrypted with a key ring this machine does not have.</summary>
    public string? Unprotect(string? protectedKey)
    {
        if (string.IsNullOrEmpty(protectedKey))
            return null;
        try
        {
            return _protector.Unprotect(protectedKey);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>What the screen shows of a key: its first four and last four characters.</summary>
    public static string Hint(string apiKey) =>
        apiKey.Length <= 10 ? new string('•', apiKey.Length) : $"{apiKey[..4]}••••••••{apiKey[^4..]}";

    public static IServiceCollection AddTo(IServiceCollection services, IConfiguration configuration)
    {
        var path = configuration["DataProtection:KeysPath"]
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".errorlens", "keys");
        services.AddDataProtection().SetApplicationName("ErrorLensLLM").PersistKeysToFileSystem(new DirectoryInfo(path));
        services.AddSingleton<ApiKeyProtector>();
        return services;
    }
}
