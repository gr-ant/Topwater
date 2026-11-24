// End-to-end check: a real model writes a Word document with the real OfficeCLI
// (https://github.com/iOfficeAI/OfficeCLI), exposed inside the simulated machine through the host bridge.
//
//   GEMINI_API_KEY=... dotnet run --project examples/OfficeAgent              (officecli on PATH)
//   GEMINI_API_KEY=... OFFICECLI=/path/to/officecli dotnet run --project examples/OfficeAgent
//
// OfficeCLI is a real program: it runs on your machine, in a private copy of the agent's home directory.
using Topwater;

var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
if (string.IsNullOrEmpty(key)) { Console.Error.WriteLine("set GEMINI_API_KEY"); return 2; }

var shell = new Shell();
using var office = shell.RegisterHostCommand("officecli", Environment.GetEnvironmentVariable("OFFICECLI"), new HostCommandOptions
{
    Description = "Create and edit Word, Excel and PowerPoint files",
    // no background process: each call writes the file before it returns, so the copy the agent sees is current
    Environment = { ["OFFICECLI_NO_AUTO_RESIDENT"] = "1", ["OFFICECLI_SKIP_UPDATE"] = "1" },
});
shell.Fs.WriteAllText("/home/agent/notes.txt", """
    Q3 review notes (rough)
    - revenue 4.2M, up 12% on Q2
    - churn 3.1%, down from 3.8%
    - support tickets: 1,840 (Q2: 2,210)
    - launched: SSO, audit log export
    - slipped to Q4: mobile offline mode
    - headcount 61 -> 66
    """);
shell.Fs.SetOwner("/home/agent/notes.txt", "agent");

const string Prompt = """
    Turn my rough notes in ~/notes.txt into a Word document at ~/q3-review.docx using the `officecli` program.
    It should have the title "Q3 Review" as a heading, a short summary paragraph, a table with one row per metric
    (columns: Metric, Q2, Q3) for churn, support tickets and headcount, and a "Launched" and a "Slipped" section.
    Check the result before you finish.
    """;
var run = await GeminiAgent.RunAsync(shell, Prompt, key, maxTurns: 60, verbose: true);
var logPath = RunLog.NewPath("office-agent");
RunLog.Save(logPath, run);

// ---------- checks: read the file back with Topwater's own converter ----------
const string Doc = "/home/agent/q3-review.docx";
string markdown = "";
bool exists = shell.Fs.IsFile(Doc);
if (exists)
    try { markdown = MarkdownConverter.ToMarkdown(shell.Fs.ReadAllBytes(Doc), "q3-review.docx"); } catch (Exception e) { markdown = $"(unreadable: {e.Message})"; }
bool Has(params string[] words) => words.All(w => markdown.Contains(w, StringComparison.OrdinalIgnoreCase));
var checks = new (string label, bool ok)[]
{
    ("~/q3-review.docx exists and is a real Word file", exists && shell.Fs.ReadAllBytes(Doc) is [0x50, 0x4B, ..]),
    ("it has the heading \"Q3 Review\"", System.Text.RegularExpressions.Regex.IsMatch(markdown, @"^#+\s*\**Q3 Review", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
    ("it has a table with Metric / Q2 / Q3 columns", System.Text.RegularExpressions.Regex.IsMatch(markdown, @"\|\s*Metric\s*\|\s*Q2\s*\|\s*Q3\s*\|", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
    ("the table carries the numbers (3.8%, 3.1%, 2,210, 1,840, 61, 66)", Has("3.8%", "3.1%", "2,210", "1,840", "61", "66")),
    ("it has Launched and Slipped sections with their items", Has("Launched", "SSO", "audit log", "Slipped", "offline")),
};
Console.WriteLine($"\n=== Office document: {run.Commands.Count} commands, {run.Commands.Count(c => c.ExitCode != 0)} non-zero exits\n{run.Answer}\n");
foreach (var (label, ok) in checks) Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}");
Console.WriteLine($"\n{(checks.All(c => c.ok) ? "PASSED" : "FAILED")} ({checks.Count(c => c.ok)}/{checks.Length})\ntranscript: {logPath}");
if (exists && Environment.GetEnvironmentVariable("SAVE_DOCX") is { Length: > 0 } save) File.WriteAllBytes(save, shell.Fs.ReadAllBytes(Doc));
Console.WriteLine("\n--- the document, as Markdown ---\n" + markdown);
return checks.All(c => c.ok) ? 0 : 1;
