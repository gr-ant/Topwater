// Drive a real website from the shell: Chromium loads the page, Topwater mounts it at /web as files, and a bash script (or a
// model through OpenRouter) reads it with cat and acts on it with echo. Every request the page makes goes through the web
// policy below, which allows one public demo site and nothing else.
//
//   dotnet run --project examples/BrowserMount                                   # the built-in script: log in, read quotes
//   dotnet run --project examples/BrowserMount -- --script mine.sh               # your own bash script
//   OPENROUTER_API_KEY=... dotnet run --project examples/BrowserMount -- --prompt "Log in, then list the tags of the first three quotes"
//   dotnet run -c Release --project examples/BrowserMount -- --bench             # time four Wikipedia articles, page by page
//
// The first run downloads Chromium into your Playwright cache (~/.cache/ms-playwright).
using Topwater;
using Topwater.Browser;

string? prompt = null, scriptFile = null;
bool bench = false;
for (int i = 0; i < args.Length; i++)
    switch (args[i])
    {
        case "--prompt" when i + 1 < args.Length: prompt = args[++i]; break;
        case "--script" when i + 1 < args.Length: scriptFile = args[++i]; break;
        case "--bench": bench = true; break;
        default: Console.Error.WriteLine("usage: BrowserMount [--prompt TEXT | --script FILE | --bench]"); return 2;
    }

if (Microsoft.Playwright.Program.Main(["install", "chromium"]) != 0) { Console.Error.WriteLine("could not install Chromium"); return 1; }

if (bench) return await Bench();

// quotes.toscrape.com is a sandbox made for scraping practice; its login accepts any name and password
var policy = new WebPolicy { Domains = ["quotes.toscrape.com"], AllowHttp = true, Methods = ["GET", "POST"], MaxRequestsPerCall = 100 };
await using var page = await BrowserSource.LaunchAsync(new BrowserSourceOptions { Policy = policy }, "https://quotes.toscrape.com/");
var shell = new Shell(new ShellOptions { MaxCallDuration = TimeSpan.FromMinutes(2) });
await shell.MountAsync("/web", page, MountMode.Live);

const string Demo = """
    cat /web/title /web/url
    ls /web/elements | head -8
    echo 1 > /web/elements/link-login/click && cat /web/url
    cat /web/forms/form/fields.json
    echo '{"Username": "agent", "Password": "secret"}' > /web/forms/form/fields.json
    echo > /web/forms/form/submit && cat /web/url
    grep -c 'by ' /web/text.md
    ls /web/elements | grep -i logout
    """;

if (prompt != null)
{
    var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
    if (string.IsNullOrEmpty(key)) { Console.Error.WriteLine("set OPENROUTER_API_KEY to run a model"); return 2; }
    var run = await OpenRouterAgent.RunAsync(shell, prompt, key, BashTool.DescriptionFor(shell), maxTurns: 40, verbose: true);
    Console.WriteLine($"\n{run.Answer}");
    return 0;
}

if (scriptFile != null)
{
    var r = await shell.RunAsync(File.ReadAllText(scriptFile));
    Console.Write(r.Stdout);
    Console.Error.Write(r.Stderr);
    return r.ExitCode;
}

foreach (var line in Demo.Split('\n', StringSplitOptions.RemoveEmptyEntries)) // one call a line, to show each
{
    Console.WriteLine($"$ {line}");
    var r = await shell.RunAsync(line);
    Console.Write(r.Stdout);
    Console.Error.Write(r.Stderr);
}
return 0;

// Opens four Wikipedia articles through the mount, one call each as an agent would, and prints the time of each.
static async Task<int> Bench()
{
    var policy = new WebPolicy { Domains = ["en.wikipedia.org"], MaxRequestsPerCall = 200, MaxRequests = 2000 };
    await using var page = await BrowserSource.LaunchAsync(new BrowserSourceOptions { Policy = policy });
    var shell = new Shell(new ShellOptions { MaxCallDuration = TimeSpan.FromMinutes(2) });
    await shell.MountAsync("/web", page, MountMode.Live);
    var total = System.Diagnostics.Stopwatch.StartNew();
    foreach (var p in new[] { "Donald_Trump", "Joe_Biden", "Barack_Obama", "George_W._Bush" })
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = await shell.RunAsync($"echo https://en.wikipedia.org/wiki/{p} > /web/url; grep -m3 -i born /web/text.md | cut -c1-80");
        Console.WriteLine($"{p,-16} {watch.Elapsed.TotalSeconds,6:F2} s  {r.Stdout.Split('\n')[0]}{r.Stderr}");
    }
    Console.WriteLine($"{"total",-16} {total.Elapsed.TotalSeconds,6:F2} s");
    Console.Write((await shell.RunAsync("grep refused /web/status | cut -c1-150")).Stdout); // the last page's refusals
    return 0;
}
