// python3: CPython compiled to WebAssembly, run over this machine's files. Opt-in: the Topwater.Python add-on carries the
// interpreter and the command (WASI host included) and registers it here when the host calls AllowPython. See docs/python.md.
namespace Topwater;

internal static partial class Builtins
{
    /// <summary>Registers python3 when the host enabled it; RegisterAll calls this without a handler, which registers nothing.</summary>
    internal static void RegisterPythonCommand(Shell shell, CommandHandler? python = null)
    {
        if (python is null) return;
        var spec = CommandSpec.Of("python3");
        shell.Register("python3", python, spec.Summary, spec.Help);
        shell.MarkBuiltin("python3"); // a built-in from spec/: its --help and --version are the spec's
    }
}
