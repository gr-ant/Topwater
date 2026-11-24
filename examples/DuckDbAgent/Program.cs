// End-to-end check: a real model answers questions about three CSV files with the real DuckDB CLI
// (https://duckdb.org), exposed inside the simulated machine through the host bridge.
//
//   GEMINI_API_KEY=... dotnet run --project examples/DuckDbAgent              (duckdb on PATH)
//   GEMINI_API_KEY=... DUCKDB=/path/to/duckdb dotnet run --project examples/DuckDbAgent
//   dotnet run --project examples/DuckDbAgent -- --seed-only DIR              (write the CSVs, print the expected answers)
//
// DuckDB is a real program: it runs on your machine, in a private copy of the agent's home directory, inside
// bubblewrap with the network off when bwrap is installed.
using System.Globalization;
using System.Text.Json;
using Topwater;

var inv = CultureInfo.InvariantCulture;

// ---------- the data: a small shop, generated deterministically ----------
var rnd = new Random(20240);
string[] regions = ["North", "South", "East", "West"];
string[] first = ["Ada", "Ben", "Chloe", "Dev", "Ema", "Finn", "Grace", "Hugo", "Iris", "Jon", "Kai", "Lena", "Max", "Nora", "Omar", "Pia"];
string[] last = ["Smith", "Okafor", "Nguyen", "Garcia", "Muller", "Rossi", "Kowalski", "Haddad", "Tanaka", "Silva"];
var customers = Enumerable.Range(1, 120).Select(i =>
{
    // some customers are companies whose names contain commas, so the CSV has quoted fields
    var name = i % 9 == 0 ? $"{last[rnd.Next(last.Length)]}, {last[rnd.Next(last.Length)]} & Co" : $"{first[rnd.Next(first.Length)]} {last[rnd.Next(last.Length)]}";
    return (Id: $"C{i:000}", Name: name, Region: regions[rnd.Next(regions.Length)], Signup: new DateOnly(2022, 1, 1).AddDays(rnd.Next(900)));
}).ToList();
string[] categories = ["Hardware", "Software", "Services", "Accessories"];
var products = Enumerable.Range(1, 30).Select(i =>
    (Sku: $"P{i:00}", Name: $"{categories[(i - 1) % 4]} item {i}", Category: categories[(i - 1) % 4], Price: Math.Round(5m + rnd.Next(1, 40000) / 100m, 2))).ToList();
// a fifth of the customers never order
var buyers = customers.Where(_ => rnd.Next(5) != 0).ToList();
var orders = Enumerable.Range(1, 600).Select(i =>
    (Id: 10000 + i, Customer: buyers[rnd.Next(buyers.Count)].Id, Sku: products[rnd.Next(products.Count)].Sku, Qty: rnd.Next(1, 6),
     Date: new DateOnly(2023, 7, 1).AddDays(rnd.Next(550)))).ToList();

static string Q(string s) => s.Contains(',') ? $"\"{s}\"" : s;
var csv = new Dictionary<string, string>
{
    ["customers.csv"] = "customer_id,name,region,signup_date\n" + string.Concat(customers.Select(c => $"{c.Id},{Q(c.Name)},{c.Region},{c.Signup:yyyy-MM-dd}\n")),
    ["products.csv"] = "sku,product_name,category,unit_price\n" + string.Concat(products.Select(p => $"{p.Sku},{p.Name},{p.Category},{p.Price.ToString("0.00", inv)}\n")),
    ["orders.csv"] = "order_id,customer_id,sku,quantity,order_date\n" + string.Concat(orders.Select(o => $"{o.Id},{o.Customer},{o.Sku},{o.Qty},{o.Date:yyyy-MM-dd}\n")),
};

// ---------- the expected answers, computed here without DuckDB ----------
var price = products.ToDictionary(p => p.Sku, p => p.Price);
var category = products.ToDictionary(p => p.Sku, p => p.Category);
var region = customers.ToDictionary(c => c.Id, c => c.Region);
var byRegion = orders.Where(o => o.Date.Year == 2024).GroupBy(o => region[o.Customer]).Select(g => (Key: g.Key, Value: g.Sum(o => o.Qty * price[o.Sku]))).OrderByDescending(x => x.Value).ToList();
var q2 = orders.Where(o => o.Date >= new DateOnly(2024, 4, 1) && o.Date <= new DateOnly(2024, 6, 30))
    .GroupBy(o => category[o.Sku]).Select(g => (Key: g.Key, Value: (decimal)g.Select(o => o.Customer).Distinct().Count())).OrderByDescending(x => x.Value).ToList();
var bySpend = orders.GroupBy(o => o.Customer).Select(g => (Key: g.Key, Value: g.Sum(o => o.Qty * price[o.Sku]))).OrderByDescending(x => x.Value).ToList();
// each question must have one right answer
foreach (var ranking in new[] { byRegion, q2, bySpend })
    if (ranking[0].Value == ranking[1].Value) throw new InvalidOperationException($"tie for first place: {ranking[0]} / {ranking[1]}; change the seed");
var expected = new Dictionary<string, object>
{
    ["top_region_2024"] = byRegion[0].Key,
    ["top_region_2024_revenue"] = byRegion[0].Value,
    ["customers_without_orders"] = customers.Count(c => orders.All(o => o.Customer != c.Id)),
    ["q2_2024_top_category"] = q2[0].Key,
    ["q2_2024_top_category_customers"] = (int)q2[0].Value,
    ["top_customer_id"] = bySpend[0].Key,
    ["top_customer_spend"] = bySpend[0].Value,
};

if (args is ["--seed-only", var dir])
{
    Directory.CreateDirectory(dir);
    foreach (var (name, text) in csv) File.WriteAllText(Path.Combine(dir, name), text);
    Console.WriteLine(JsonSerializer.Serialize(expected, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

// ---------- the agent ----------
var shell = new Shell();
HostCommand duck;
try
{
    duck = shell.RegisterHostCommand("duckdb", Environment.GetEnvironmentVariable("DUCKDB"), new HostCommandOptions
    {
        Description = "DuckDB SQL engine: duckdb -c \"SQL\" (or -csv / -json). Refer to files by relative path or ~/file in SQL",
        // DuckDB keeps extensions in ~/.duckdb; keep them in the mirror, out of the agent's view
        Exclude = { ".duckdb", ".duckdbrc", ".duckdb_history" },
        Launcher = Sandbox(),
    });
}
catch (FileNotFoundException)
{
    Console.Error.WriteLine("duckdb is not installed: put it on PATH or set DUCKDB=/path/to/duckdb (https://duckdb.org/docs/installation)");
    return 2;
}
using var _ = duck;
var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
if (string.IsNullOrEmpty(key)) { Console.Error.WriteLine("set GEMINI_API_KEY"); return 2; }

// CSV reading is built in; never try to download an extension mid-run (the sandbox has no network anyway)
Directory.CreateDirectory(Path.Combine(duck.Root, "home", "agent"));
File.WriteAllText(Path.Combine(duck.Root, "home", "agent", ".duckdbrc"), "SET autoinstall_known_extensions = false;\n");
foreach (var (name, text) in csv)
{
    shell.Fs.WriteAllText($"/home/agent/{name}", text);
    shell.Fs.SetOwner($"/home/agent/{name}", "agent");
}

const string Prompt = """
    ~/customers.csv, ~/products.csv and ~/orders.csv are exports from our shop. Revenue of an order line is
    quantity × unit_price. Use the `duckdb` program to answer:
    1. Which region had the highest total revenue from orders placed in 2024, and what was that revenue?
    2. How many customers have never placed an order?
    3. In Q2 2024 (April 1 to June 30, inclusive), which product category was bought by the most distinct
       customers, and how many customers was that?
    4. Which customer (customer_id) has the highest total spend across all orders, and how much is it?
    Write the answers to ~/answers.json as one JSON object with exactly these keys: top_region_2024,
    top_region_2024_revenue, customers_without_orders, q2_2024_top_category, q2_2024_top_category_customers,
    top_customer_id, top_customer_spend. Money as numbers rounded to 2 decimals, counts as integers.
    """;
var run = await GeminiAgent.RunAsync(shell, Prompt, key, maxTurns: 40, verbose: true);
var logPath = RunLog.NewPath("duckdb-agent");
RunLog.Save(logPath, run);

// ---------- checks ----------
const string Answers = "/home/agent/answers.json";
JsonElement? got = null;
if (shell.Fs.IsFile(Answers))
    try { got = JsonDocument.Parse(shell.Fs.ReadAllText(Answers)).RootElement; } catch (JsonException) { }
bool Matches(string k)
{
    if (got is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty(k, out var v)) return false;
    var text = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
    return expected[k] switch
    {
        string s => string.Equals(text.Trim(), s, StringComparison.OrdinalIgnoreCase),
        var n => decimal.TryParse(text.Trim().TrimStart('$').Replace(",", ""), NumberStyles.Number, inv, out var d) && Math.Abs(d - Convert.ToDecimal(n, inv)) < 0.006m,
    };
}
var checks = new List<(string label, bool ok)> { ("~/answers.json exists and is a JSON object", got is { ValueKind: JsonValueKind.Object }) };
checks.AddRange(expected.Select(e => ($"{e.Key} = {Convert.ToString(e.Value, inv)}", Matches(e.Key))));
Console.WriteLine($"\n=== DuckDB questions: {run.Commands.Count} commands, {run.Commands.Count(c => c.ExitCode != 0)} non-zero exits\n{run.Answer}\n");
foreach (var (label, ok) in checks) Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}");
Console.WriteLine($"\n{(checks.All(c => c.ok) ? "PASSED" : "FAILED")} ({checks.Count(c => c.ok)}/{checks.Count})\ntranscript: {logPath}");
return checks.All(c => c.ok) ? 0 : 1;

// bubblewrap with no network where it is available; otherwise DuckDB runs unsandboxed, like any host command
static Func<IReadOnlyList<string>, string, IReadOnlyList<string>>? Sandbox()
{
    if (!OperatingSystem.IsLinux()) return null;
    try { return HostSandbox.Bubblewrap(); }
    catch (Exception e) when (e is FileNotFoundException or InvalidOperationException)
    {
        Console.Error.WriteLine($"note: running duckdb without a sandbox ({e.Message})");
        return null;
    }
}
