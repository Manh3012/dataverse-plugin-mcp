using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.PowerPlatform.Dataverse.Client;
using ModelContextProtocol;

var builder = Host.CreateApplicationBuilder(args);
// stdout carries JSON-RPC; every log line must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace);
builder.Services.AddSingleton<Dataverse>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();

// Connects lazily on the first tool call: an interactive sign-in opens the system browser
// and would otherwise blow the MCP startup timeout.
// The user signs in once (MSAL, cached) and picks an environment with select_environment, remembered across sessions.
public sealed class Dataverse
{
    // Microsoft's sample app, preconsented for Dataverse (development only).
    const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
    // shortcut: commercial cloud only; GCC / USG / DOD / China need their own discovery URL and authority.
    const string Discovery = "https://globaldisco.crm.dynamics.com";
    // Not GetFolderPath: that ignores the environment, and the smoke test points LOCALAPPDATA at a temp folder.
    static readonly string Home = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\DataversePluginMcp");
    // Line 1 the environment URL, line 2 its name (read by the plug-in's above-prompt band, hooks/register.tsx).
    static readonly string SavedFile = Path.Combine(Home, "environment.txt");
    static string SavedUrl => File.Exists(SavedFile) ? File.ReadLines(SavedFile).FirstOrDefault()?.Trim() ?? "" : "";

    readonly object _gate = new();
    ServiceClient? _client; // null until the first successful connect: a failed sign-in is not cached, the next tool call retries
    readonly Lazy<Task<IPublicClientApplication>> _msal = new(CreateMsal);

    public ServiceClient Client { get { lock (_gate) return _client ??= Connect(); } }

    // For the replay host: MSAL, silent after the first sign-in.
    public string AccessToken => Token(Client.ConnectedOrgUriActual.GetLeftPart(UriPartial.Authority)).GetAwaiter().GetResult();

    ServiceClient Connect()
    {
        var url = SavedUrl;
        if (url.Length == 0)
            throw new McpException("No Dataverse environment selected. Call select_environment without a url: it signs in through the browser the first time and shows the user a picker.");
        return Open(url);
    }

    ServiceClient Open(string url) => Ready(new ServiceClient(new Uri(url), _ => Token(url), useUniqueInstance: true));

    static ServiceClient Ready(ServiceClient client) =>
        client.IsReady ? client : throw new McpException($"Dataverse connection failed: {client.LastError}");

    public async Task<List<DataverseEnvironment>> ListEnvironments()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", await Token(Discovery));
        var json = await http.GetStringAsync($"{Discovery}/api/discovery/v2.0/Instances?$select=FriendlyName,UniqueName,Url,OrganizationType,Region,State,IsUserSysAdmin");
        var current = (_client?.ConnectedOrgUriActual.GetLeftPart(UriPartial.Authority) ?? SavedUrl).Trim().TrimEnd('/');
        return JsonDocument.Parse(json).RootElement.GetProperty("value").EnumerateArray()
            .Select(i =>
            {
                var url = i.GetProperty("Url").GetString()!.TrimEnd('/');
                return new DataverseEnvironment(
                    i.GetProperty("FriendlyName").GetString()!,
                    url,
                    TypeName(i.GetProperty("OrganizationType").GetInt32()),
                    i.GetProperty("UniqueName").GetString()!,
                    i.GetProperty("Region").GetString()!,
                    i.GetProperty("State").GetInt32() != 0,
                    i.GetProperty("IsUserSysAdmin").GetBoolean(),
                    string.Equals(url, current, StringComparison.OrdinalIgnoreCase));
            })
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public object SelectEnvironment(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new McpException($"Not an https environment URL: {url}. Use a Url from list_environments.");
        var root = uri.GetLeftPart(UriPartial.Authority);
        var client = Open(root);
        lock (_gate) { _client?.Dispose(); _client = client; }
        Directory.CreateDirectory(Home);
        File.WriteAllLines(SavedFile, [root, client.ConnectedOrgFriendlyName]);
        return new { Selected = root, Name = client.ConnectedOrgFriendlyName, Version = client.ConnectedOrgVersion?.ToString() };
    }

    // OrganizationType: https://learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/organizationtype
    static string TypeName(int t) => t switch
    {
        0 or 4 => "Production", 5 or 6 => "Sandbox", 7 => "Preview", 9 or 11 or 14 => "Trial", 12 => "Default", 13 => "Developer", 15 => "Teams", _ => $"Other ({t})",
    };

    async Task<string> Token(string resource)
    {
        string[] scopes = [new Uri(resource).GetLeftPart(UriPartial.Authority) + "/.default"];
        var app = await _msal.Value;
        // shortcut: first cached account; add an account switch if someone works across tenants.
        var account = (await app.GetAccountsAsync()).FirstOrDefault();
        if (account != null)
            try { return (await app.AcquireTokenSilent(scopes, account).ExecuteAsync()).AccessToken; }
            catch (MsalUiRequiredException) { }
        // A closed browser tab would otherwise leave the tool call waiting forever.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { return (await app.AcquireTokenInteractive(scopes).WithAccount(account).ExecuteAsync(timeout.Token)).AccessToken; }
        catch (OperationCanceledException) { throw new McpException("The browser sign-in didn't finish within 3 minutes. Call the tool again to retry."); }
    }

    static async Task<IPublicClientApplication> CreateMsal()
    {
        var app = PublicClientApplicationBuilder.Create(AppId)
            .WithAuthority("https://login.microsoftonline.com/organizations")
            .WithRedirectUri("http://localhost")
            .Build();
        // DPAPI-encrypted on Windows: keeps later sessions signed in.
        Directory.CreateDirectory(Home);
        (await MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder("msal.cache", Home).Build())).RegisterCache(app.UserTokenCache);
        return app;
    }
}

public sealed record DataverseEnvironment(string Name, string Url, string Type, string UniqueName, string Region, bool Disabled, bool SysAdmin, bool Selected);
