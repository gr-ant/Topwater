// Drive a real website from the shell: Chromium loads the page, Topwater mounts it at /web as files, and a bash script (or a
// model through OpenRouter) reads it with cat and acts on it with echo. Every request the page makes goes through the web
// policy below, which allows one public demo site and nothing else.
//
//   dotnet run --project examples/BrowserMount                                   # the built-in script: log in, read quotes
//   dotnet run --project examples/BrowserMount -- --script mine.sh               # your own bash script
//   OPENROUTER_API_KEY=... dotnet run --project examples/BrowserMount -- --prompt "Log in, then list the tags of the first three quotes"
//
// The first run downloads Chromium into your Playwright cache (~/.cache/ms-playwright).
using Topwater;
using Topwater.Browser;

string? prompt = null, scriptFile = null;
for (int i = 0; i < args.Length; i++)
    switch (args[i])
    {
        case "--prompt" when i + 1 < args.Length: prompt = args[++i]; break;
        case "--script" when i + 1 < args.Length: scriptFile = args[++i]; break;
        default: Console.Error.WriteLine("usage: BrowserMount [--prompt TEXT | --script FILE]"); return 2;
    }

if (Microsoft.Playwright.Program.Main(["install", "chromium"]) != 0) { Console.Error.WriteLine("could not install Chromium"); return 1; }

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
