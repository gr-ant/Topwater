namespace Topwater;

/// <summary>The single tool definition to give the model. Wire its "command" input to <see cref="Shell.RunAsync"/>.</summary>
public static class BashTool
{
    public const string Name = "bash";

    public const string Description =
        "Run a command in a sandboxed Linux shell (bash). The working directory, environment variables and files persist between calls. " +
        "Standard command-line tools are available (ls, cat, grep, sed, awk, jq, find, sort, diff, ...); " +
        "Python, network access, git and package installation are not. " +
        "Read Word, Excel, PowerPoint, PDF and HTML files with `to-markdown FILE`. " +
        "Other programs on this machine are live interfaces to this application's systems, so their actions are real: " +
        "run `help` to list them and `<program> --help` for usage.";

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
    public static async Task<string> ExecuteAsync(Shell shell, string command, CancellationToken cancellationToken = default) =>
        (await shell.RunAsync(command, cancellationToken)).ToToolOutput();
}
