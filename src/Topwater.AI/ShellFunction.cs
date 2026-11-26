using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Topwater.AI;

public static class AIExtensions
{
    static readonly ConditionalWeakTable<Shell, SemaphoreSlim> Gates = new();

    /// <summary>
    /// The shell as the model's one tool: an <see cref="AIFunction"/> named <c>bash</c> with <see cref="BashTool.DescriptionFor"/>
    /// (or <paramref name="description"/>) and a single string parameter <c>command</c>. Invoking it runs the command and returns
    /// <see cref="BashTool.ExecuteAsync"/>'s text, e.g. <c>options.Tools = [shell.AsAIFunction()]</c> with a function-invoking chat client.
    /// A <see cref="Shell"/> is not thread-safe and models can ask for several tool calls at once, so calls through any function
    /// made from the same shell run one at a time, in the order they arrive. Calls you make on the shell directly are not serialised.
    /// </summary>
    public static AIFunction AsAIFunction(this Shell shell, string? description = null) =>
        new ShellFunction(shell, Gates.GetValue(shell, _ => new SemaphoreSlim(1, 1)), description ?? BashTool.DescriptionFor(shell));
}

sealed class ShellFunction(Shell shell, SemaphoreSlim gate, string description) : AIFunction
{
    static readonly JsonElement Schema = JsonDocument.Parse(BashTool.InputSchemaJson).RootElement.Clone();

    public override string Name => BashTool.Name;
    public override string Description => description;
    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var command = arguments.TryGetValue("command", out var value) ? value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            _ => null,
        } : null;
        // a model that gets the call wrong should read why, not a generic "function failed"
        if (command is null) return "bash: the \"command\" argument (a string) is required\n[exit code 2]";
        await gate.WaitAsync(cancellationToken);
        try { return await BashTool.ExecuteAsync(shell, command, cancellationToken); }
        finally { gate.Release(); }
    }
}
