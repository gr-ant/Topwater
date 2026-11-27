// mount: the sources the host mounted (docs/mounts.md), the changes waiting in them, listing them again, dropping changes. See docs/architecture.md.
namespace Topwater;

internal static partial class Builtins
{
    static void RegisterMountCommands(Shell shell) => shell.Register("mount", Mount);

    /// <summary>`TYPE:ID on DIR type TYPE (OPTIONS)`, the options naming what a writable mount's source can't do.</summary>
    static string MountLine(MountPoint m)
    {
        var caps = m.Capabilities();
        var opts = m.Mode == MountMode.ReadOnly ? ["ro"]
            : new[] { "rw", MountManager.ModeName(m.Mode) }.Concat(new[] { (MountCapabilities.Delete, "delete"), (MountCapabilities.Rename, "rename"), (MountCapabilities.Mkdir, "mkdir") }
                .Where(c => !caps.HasFlag(c.Item1)).Select(c => "no-" + c.Item2)).ToArray();
        return $"{m.Type}:{m.Id} on {m.Path} type {m.Type} ({string.Join(',', opts)})" + (m.Shown != null ? " " + m.Shown : "");
    }

    static string MountChangeLine(MountPoint m, MountChange c) =>
        c.Kind == MountChangeKind.Rename ? $"rename {m.Machine(c.Path)} -> {m.Machine(c.To!)}" : $"{c.KindName} {m.Machine(c.Path)}";

    static async Task<int> Mount(CommandContext ctx)
    {
        var a = Args.Parse(ctx.Args, CommandSpec.Of("mount"));
        var mounts = ctx.Shell.MountTable;
        int code = 0;
        var chosen = new List<MountPoint>();
        foreach (var d in a.Positional)
        {
            var path = VirtualFileSystem.Normalize(ctx.Shell.Cwd, d);
            if (mounts.Items.Find(m => m.Path == path) is { } m) chosen.Add(m);
            else
            {
                ctx.Stderr.WriteLine("mount: " + CommandSpec.Message("mount", "notMounted", ("dir", d)));
                code = 1;
            }
        }
        if (a.Positional.Count == 0) chosen = [.. mounts.Items.OrderBy(m => m.Path, StringComparer.Ordinal)];
        foreach (var m in chosen)
        {
            if (a.Has('r'))
            {
                try { await mounts.RefreshAsync(m.Path); }
                catch (SourceFailure e)
                {
                    ctx.Stderr.WriteLine($"mount: {m.Path}: {e.Safe}");
                    code = 1;
                }
                foreach (var w in m.Warnings) ctx.Stderr.WriteLine("mount: " + w);
            }
            else if (a.Has('d')) mounts.Discard(m.Path, held: false);
            else if (a.Has('c')) foreach (var c in m.Changes()) ctx.Stdout.WriteLine(MountChangeLine(m, c));
            else
            {
                ctx.Stdout.WriteLine(MountLine(m));
                foreach (var w in m.Held.Count > 0 ? [.. m.Warnings, CommandSpec.Message("mount", "held", ("count", m.Held.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)))] : m.Warnings)
                    ctx.Stdout.WriteLine("  " + w);
            }
        }
        return code;
    }
}
