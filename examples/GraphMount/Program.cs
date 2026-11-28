// Mount a SharePoint site in the machine, staged, and let an agent (or your own bash script) restructure it. Nothing is
// written to SharePoint until you have seen the change set and pass --apply.
//
//   dotnet run --project examples/GraphMount                                    # sign in, show what the agent would see
//   OPENROUTER_API_KEY=... dotnet run --project examples/GraphMount -- --prompt "File the Inbox into Projects and Archive"
//   dotnet run --project examples/GraphMount -- --script restructure.sh         # a bash script instead of a model
//   ... --apply                                                                 # then apply the change set
//
// Settings: GRAPH_TENANT_ID, GRAPH_CLIENT_ID and GRAPH_SITE_URL in the environment or ~/.config/topwater/graph.env (one
// NAME=value a line). The app registration needs "Allow public client flows" and the delegated permissions
// Files.ReadWrite.All and Sites.ReadWrite.All (docs/connectors/graph.md). You sign in with a device code on each run.
using Azure.Core;
using Azure.Identity;
using Topwater;
using Topwater.Connectors;

string? prompt = null, scriptFile = null;
bool apply = false;
for (int i = 0; i < args.Length; i++)
    switch (args[i])
    {
        case "--prompt" when i + 1 < args.Length: prompt = args[++i]; break;
        case "--script" when i + 1 < args.Length: scriptFile = args[++i]; break;
        case "--apply": apply = true; break;
        default: Console.Error.WriteLine("usage: GraphMount [--prompt TEXT | --script FILE] [--apply]"); return 2;
    }

var settings = Settings();
string? Setting(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : settings.GetValueOrDefault(name);
var (tenant, client, site) = (Setting("GRAPH_TENANT_ID"), Setting("GRAPH_CLIENT_ID"), Setting("GRAPH_SITE_URL"));
if (tenant is null || client is null || site is null)
{
    Console.Error.WriteLine("set GRAPH_TENANT_ID, GRAPH_CLIENT_ID and GRAPH_SITE_URL (or put them in ~/.config/topwater/graph.env)");
    return 2;
}

// the credential keeps the access token and refreshes it; GraphSource asks for it before every request
var credential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
{
    TenantId = tenant,
    ClientId = client,
    DeviceCodeCallback = (code, _) => { Console.WriteLine(code.Message); return Task.CompletedTask; },
});
var scopes = new TokenRequestContext(["https://graph.microsoft.com/.default"]);
var source = new GraphSource(async ct => (await credential.GetTokenAsync(scopes, ct)).Token, new GraphOptions { Site = site });

var shell = new Shell(new ShellOptions { MaxCallDuration = TimeSpan.FromMinutes(5) });
await shell.MountAsync("/mnt/sp", source, MountMode.Staged);   // signs in, then lists the site: a bad setting fails here

if (scriptFile != null)
{
    var r = await shell.RunAsync(File.ReadAllText(scriptFile));
    Console.Write(r.Stdout);
    Console.Error.Write(r.Stderr);
}
else if (prompt != null)
{
    var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
    if (string.IsNullOrEmpty(key)) { Console.Error.WriteLine("set OPENROUTER_API_KEY to run a model"); return 2; }
    var run = await OpenRouterAgent.RunAsync(shell, prompt, key, BashTool.DescriptionFor(shell), maxTurns: 60, verbose: true);
    Console.WriteLine($"\n{run.Answer}");
}
else
{
    Console.Write((await shell.RunAsync("mount; find /mnt/sp -maxdepth 3 | head -100")).Stdout);
    return 0;
}

var changes = shell.PendingChanges("/mnt/sp");
Console.WriteLine($"\n{changes.Count} changes:");
foreach (var c in changes)
{
    Console.WriteLine($"  {c.Kind.ToString().ToLowerInvariant(),-6} {c.Path}{(c.To != null ? " -> " + c.To : "")}");
    if (c.Note is { } note) Console.WriteLine($"         note: {note}");
}
if (!apply)
{
    Console.WriteLine("\nnothing was written to SharePoint (add --apply to apply the change set)");
    return 0;
}
var result = await shell.ApplyChangesAsync("/mnt/sp");
Console.WriteLine($"\napplied {result.Applied.Count} of {changes.Count}");
if (result.Failed is { } failed)
{
    Console.WriteLine($"stopped at {failed.Kind.ToString().ToLowerInvariant()} {failed.Path}: {(result.Conflict ? "conflict: " : "")}{result.Error}");
    Console.WriteLine($"not tried: {result.NotTried.Count}");
    return 1;
}
return 0;

static Dictionary<string, string> Settings()
{
    var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "topwater", "graph.env");
    var output = new Dictionary<string, string>();
    if (!File.Exists(file)) return output;
    foreach (var line in File.ReadAllLines(file))
        if (line.Split('=', 2) is [var k, var v] && !k.TrimStart().StartsWith('#'))
            output[k.Trim()] = v.Trim().Trim('"');
    return output;
}
