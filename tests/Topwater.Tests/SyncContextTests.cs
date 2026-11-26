using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Topwater;
using Topwater.AI;
using Topwater.Mcp;

namespace Topwater.Tests;

/// <summary>
/// A host that blocks on a Topwater task from a UI thread (WinForms, WPF: one thread pumps the context's queue) must not
/// deadlock, and host commands must not run on that thread.
/// </summary>
public class SyncContextTests
{
    /// <summary>A UI thread: one thread runs everything posted to its context, in order. While it blocks, nothing runs.</summary>
    sealed class UiContext : SynchronizationContext
    {
        readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback d, object? state)
        {
            try { _queue.Add((d, state)); } catch (InvalidOperationException) { } // after the thread ended: dropped, as on a closed window
        }
        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
        public void Pump() { foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state); }
        public void Complete() => _queue.CompleteAdding();
    }

    /// <summary>Runs <paramref name="body"/> on a fresh UI thread; a hang fails the test instead of the suite.</summary>
    static void OnUiThread(Action body)
    {
        var context = new UiContext();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            context.Post(_ =>
            {
                try { body(); }
                catch (Exception e) { error = e; }
                finally { context.Complete(); }
            }, null);
            context.Pump();
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the UI thread hung");
        if (error != null) ExceptionDispatchInfo.Throw(error);
    }

    /// <summary>What a UI host writes: block on the task.</summary>
    static T Block<T>(Task<T> task)
    {
        Assert.True(task.Wait(TimeSpan.FromSeconds(5)), "deadlock: the task waits for the blocked UI thread");
        return task.Result;
    }
    static void Block(Task task) => Assert.True(task.Wait(TimeSpan.FromSeconds(5)), "deadlock: the task waits for the blocked UI thread");

    [Fact]
    public void RunAsync_with_sleep() => OnUiThread(() =>
        Assert.Equal("hi\n", Block(new Shell().RunAsync("sleep 0.05; echo hi")).Stdout));

    [Fact]
    public void Host_command_that_awaits_runs_off_the_UI_thread() => OnUiThread(() =>
    {
        var ui = Thread.CurrentThread;
        bool? sawContext = null, onUiThread = null;
        var sh = new Shell();
        sh.Register("slow", async ctx =>
        {
            sawContext = SynchronizationContext.Current != null;
            onUiThread = Thread.CurrentThread == ui;
            await Task.Delay(20);
            ctx.Stdout.WriteLine("done");
            return 0;
        });
        Assert.Equal("done\n", Block(sh.RunAsync("slow")).Stdout);
        Assert.False(sawContext);
        Assert.False(onUiThread);
    });

    [Fact]
    public void BashTool_ExecuteAsync() => OnUiThread(() =>
        Assert.Equal("hi\n", Block(BashTool.ExecuteAsync(new Shell(), "sleep 0.05; echo hi"))));

    [Fact]
    public void AIFunction_InvokeAsync() => OnUiThread(() =>
    {
        var result = new Shell().AsAIFunction().InvokeAsync(new AIFunctionArguments { ["command"] = "sleep 0.05; echo hi" }).AsTask();
        Assert.Equal("hi\n", Block(result));
    });

    [Fact]
    public void Semantic_Kernel_plugin_invoke() => OnUiThread(() =>
    {
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("shell", [new Shell().AsAIFunction().AsKernelFunction()]);
        Assert.Equal("hi\n", Block(kernel.InvokeAsync("shell", "bash", new() { ["command"] = "sleep 0.05; echo hi" })).ToString());
    });

    static Recording Recorded()
    {
        var sh = new Shell();
        using var rec = sh.StartRecording();
        sh.RunAsync("sleep 0.05; echo hi > a.txt; cat a.txt").GetAwaiter().GetResult(); // called off the UI thread
        return rec.Recording;
    }

    [Fact]
    public void Recording_replay_and_shell_at()
    {
        var recording = Recorded();
        OnUiThread(() =>
        {
            Assert.True(Block(recording.ReplayAsync()).Matches);
            Assert.Equal("hi\n", Block(Block(recording.ShellAtAsync(0)).RunAsync("cat a.txt")).Stdout);
            Assert.Single(Block(recording.RerecordAsync(new ShellTarget(recording.CreateShell()))).Calls);
        });
    }

    [Fact]
    public void Replay_AssertMatches()
    {
        var path = Path.Combine(Path.GetTempPath(), $"topwater-sync-{Guid.NewGuid():N}.topwater");
        try
        {
            Recorded().Save(path);
            OnUiThread(() => Block(Topwater.Replay.Replay.AssertMatches(path)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Mcp_mount_and_call()
    {
        Pipe toServer = new(), toClient = new();
        var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "echo", Version = "1.0" },
            ToolCollection = [McpServerTool.Create(async (string text) => { await Task.Delay(20); return text; }, new McpServerToolCreateOptions { Name = "say" })],
        });
        using var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));

        OnUiThread(() =>
        {
            var sh = new Shell();
            Block(sh.MountMcpAsync("echo", client));
            Assert.Equal("hi", Block(sh.RunAsync("echo say --text hi")).Stdout.TrimEnd());
        });

        stop.Cancel();
        try { await running; } catch (OperationCanceledException) { }
    }
}
