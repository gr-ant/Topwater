namespace Topwater;

internal static partial class Builtins
{
    /// <summary>to-markdown: converts documents in the virtual filesystem to Markdown (see <see cref="MarkdownConverter"/>).</summary>
    static void RegisterDocumentCommands(Shell shell)
    {
        shell.Register("to-markdown", ctx =>
        {
            var a = Args.Parse(ctx.Args, "", "o", "output=o");
            if (a.Positional.Count == 0) throw new ShellException("missing file operand\nUsage: to-markdown [-o OUT.md] FILE...");
            int code = 0;
            var parts = new List<string>();
            foreach (var f in a.Positional)
            {
                string md;
                try { md = MarkdownConverter.ToMarkdown(ctx.Fs.ReadAllBytes(ctx.ResolvePath(f)), f); }
                catch (ShellException e)
                {
                    // filesystem errors carry the path ("/abs/x: No such file..."); show the name as typed instead
                    var msg = e.Message.StartsWith('/') ? e.Message[(e.Message.LastIndexOf(": ") + 2)..] : e.Message;
                    ctx.Stderr.WriteLine($"{ctx.Name}: {f}: {msg}");
                    code = 1;
                    continue;
                }
                if (md.Length > 0 && !md.EndsWith('\n')) md += "\n";
                parts.Add(a.Positional.Count > 1 ? $"# {f}\n\n{md}" : md);
            }
            var text = string.Join("\n", parts);
            if (a.Value('o') is { } output) ctx.Fs.WriteAllText(ctx.ResolvePath(output), text);
            else ctx.Stdout.Write(text);
            return code;
        }, "Convert documents (docx, xlsx, pptx, html, csv, json) to Markdown on stdout (-o FILE to save)");
    }
}
