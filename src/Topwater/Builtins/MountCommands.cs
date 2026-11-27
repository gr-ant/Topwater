// mount: the sources the host mounted (docs/mounts.md), the changes waiting in them, and listing them again. See docs/architecture.md.
namespace Topwater;

internal static partial class Builtins
{
    static void RegisterMountCommands(Shell shell) => shell.Register("mount", Mount);

    static string MountLine(MountInfo info) =>
        $"{info.Type}:{info.Id} on {info.Path} type {info.Type} ({(info.Mode == MountMode.ReadOnly ? "ro" : "rw," + MountManager.ModeName(info.Mode))})" + (info.Error != null ? " " + info.Error : "");

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
                catch (Exception stop) when (stop is OperationCanceledException or ControlFlow) { throw; }
                catch (Exception e) // the source's own error, for the agent
                {
                    ctx.Stderr.WriteLine($"mount: {m.Path}: {e.Message}");
                    code = 1;
                }
                foreach (var w in m.Warnings) ctx.Stderr.WriteLine("mount: " + w);
            }
            else if (a.Has('c')) foreach (var c in m.Changes()) ctx.Stdout.WriteLine(MountChangeLine(m, c));
            else ctx.Stdout.WriteLine(MountLine(m.Info()));
        }
        return code;
    }
}
