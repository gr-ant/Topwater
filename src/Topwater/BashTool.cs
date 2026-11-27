namespace Topwater;

/// <summary>The single tool definition to give the model. Wire its "command" input to <see cref="Shell.RunAsync"/>.</summary>
public static class BashTool
{
    public const string Name = "bash";

    public const string Description =
        "Run a command in a sandboxed Linux shell (bash). The working directory, environment variables and files persist between calls. " +
        "Standard command-line tools are available (ls, cat, grep, sed, awk, jq, find, sort, diff, git, ...); git works on local repositories. " +
        "Python, network access and package installation are not. " +
        "Read Word, Excel, PowerPoint and HTML files with `to-markdown FILE`. " +
        "Other programs on this machine are live interfaces to this application's systems, so their actions are real: " +
        "run `help` to list them and `<program> --help` for usage.";

    const string NoNetwork = "Python, network access and package installation are not. ";

    /// <summary>
    /// <see cref="Description"/> for this shell: with web access (<see cref="Shell.AllowWeb(WebPolicy)"/>) it names the sites and methods
    /// <c>curl</c> and <c>wget</c> can reach (the first 20 sites) instead of saying there is no network, and it names each mounted folder.
    /// </summary>
    public static string DescriptionFor(Shell shell)
    {
        var text = Description;
        if (shell.Web is { IsReplay: false, Policy: var p } && !ReferenceEquals(p, WebSession.NoNetwork) && p.Domains.Count > 0)
        {
            var sites = string.Join(", ", p.Domains.Take(20).Select(d => d.Trim())) + (p.Domains.Count > 20 ? $", and {p.Domains.Count - 20} more" : "");
            var methods = string.Join(", ", p.Methods.Select(m => m.ToUpperInvariant()));
            text = text.Replace(NoNetwork, $"Python and package installation are not; `curl` and `wget` reach only these sites (methods: {methods}): {sites}. ");
        }
        return text + MountSentences(shell);
    }

    /// <summary>One sentence per mount (docs/mounts.md), so the model knows the folders from outside are there.</summary>
    static string MountSentences(Shell shell) => string.Concat(shell.Mounts.OrderBy(m => m.Path, StringComparer.Ordinal).Select(m => m.Mode switch
    {
        MountMode.ReadOnly => $" `{m.Path}` is a folder from outside, read-only.",
        MountMode.Staged => $" `{m.Path}` is a folder from outside; your changes there are reviewed before they are saved (`mount --changes`).",
        _ => $" `{m.Path}` is a folder from outside; your changes there are saved to it after each command.",
    }));

    public const string InputSchemaJson = """
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "The bash command to run." }
          },
          "required": ["command"]
        }
        """;

    /// <summary>Runs the model's command and returns the text to send back as the tool result.</summary>
    public static Task<string> ExecuteAsync(Shell shell, string command, CancellationToken cancellationToken = default) =>
        OffContext.Run(async () => (await shell.RunAsync(command, cancellationToken)).ToToolOutput());
}
