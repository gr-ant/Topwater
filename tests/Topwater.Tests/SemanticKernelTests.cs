using Microsoft.SemanticKernel;
using Topwater;
using Topwater.AI;

namespace Topwater.Tests;

/// <summary>Semantic Kernel takes the shell's AIFunction as-is: no Topwater.SemanticKernel package needed.</summary>
public class SemanticKernelTests
{
    [Fact]
    public async Task ShellIsAKernelPlugin()
    {
        var shell = new Shell();
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("shell", [shell.AsAIFunction().AsKernelFunction()]);

        var bash = kernel.Plugins["shell"]["bash"];
        Assert.Equal(BashTool.Description, bash.Description);
        var command = Assert.Single(bash.Metadata.Parameters);
        Assert.Equal("command", command.Name);
        Assert.True(command.IsRequired);

        Assert.Equal("(no output)", (await kernel.InvokeAsync("shell", "bash", new() { ["command"] = "cd /tmp && echo hi > a.txt" })).ToString());
        Assert.Equal("/tmp\nhi\n", (await kernel.InvokeAsync(bash, new() { ["command"] = "pwd; cat a.txt" })).ToString());
        Assert.Equal("cat: nope: No such file or directory\n[exit code 1]", (await kernel.InvokeAsync(bash, new() { ["command"] = "cat nope" })).ToString());
    }
}
