namespace Topwater.Containers;

/// <summary>Something that didn't make the move, and why.</summary>
public sealed record ContainerReportItem(string What, string Why)
{
    public override string ToString() => $"{What}: {Why}";
}

/// <summary>What an export or import carried over and what it couldn't. See docs/containers.md.</summary>
public sealed class ContainerReport
{
    internal readonly List<string> _carried = [], _deleted = [];
    internal readonly List<ContainerReportItem> _notCarried = [];

    /// <summary>Paths written: into the image's layer (export) or into the Topwater machine (import).</summary>
    public IReadOnlyList<string> Carried => _carried;
    /// <summary>Paths removed because the source machine had deleted them.</summary>
    public IReadOnlyList<string> Deleted => _deleted;
    /// <summary>Everything left behind, each with the reason.</summary>
    public IReadOnlyList<ContainerReportItem> NotCarried => _notCarried;

    internal void Skip(string what, string why) => _notCarried.Add(new(what, why));

    public override string ToString() =>
        $"{Carried.Count} paths carried, {Deleted.Count} deleted, {NotCarried.Count} not carried"
        + string.Concat(NotCarried.Select(i => "\n  " + i));
}

/// <summary>A docker command failed.</summary>
public sealed class ContainerException(string message) : Exception(message);
