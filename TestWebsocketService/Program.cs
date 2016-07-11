using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Text;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;

public class Program
{
    public static void Main(string[] args)
    {
        var host = new WebHostBuilder()
            .UseKestrel()
            .UseWebRoot(Directory.GetCurrentDirectory())
            .UseIISIntegration()
            .UseStartup<Startup>()
            .Build();

        host.Run();
    }

    public static async Task DoSocketAsync(HttpContext http, WebSocket socket)
    {
        if (!http.Request.Path.HasValue) { await socket.SendStringAsync("ERROR\tNo project specified"); return; }
        var dir = Environment.ExpandEnvironmentVariables($@"%USERPROFILE%\Documents\Visual Studio 2015\Projects\Replay{http.Request.Path.Value}");
        var projfn = dir + "\\project.json";
        if (!File.Exists(projfn)) { await socket.SendStringAsync($"ERROR\tProject doesn't exist '{projfn}'"); return; }

        // Load the project, and establish the "OK" handshake to show we've done it
        var workspace = new Microsoft.DotNet.ProjectModel.Workspaces.ProjectJsonWorkspace(dir);
        var solution = workspace.CurrentSolution;
        var project = solution.Projects.Single();
        foreach (var csxfn in Directory.GetFiles(dir,"*.csx"))
        {
            project = project.AddDocument(csxfn, File.ReadAllText(csxfn), null, csxfn).WithSourceCodeKind(Microsoft.CodeAnalysis.SourceCodeKind.Script).Project;
        }
        await socket.SendStringAsync("OK");
        var msg = await socket.RecvStringAsync();
        if (msg != "OK") { await socket.SendStringAsync($"ERROR\tExpected 'OK' not '{msg}'"); return; }


        // Set up monitoring for diagnostics
        var queue = new BufferBlock<string>();
        var host = new ReplayHostManager();
        host.DiagnosticChanged += (s) => queue.Post(s);
        host.TriggerReplayAsync(project);


        // Run the conversation!s
        while (true)
        {
            if (queue.TryReceive(out msg)) { await socket.SendStringAsync(msg); continue; }

            msg = await socket.RecvStringAsync();
            if (msg == null) return;
            var cmds = msg.Split(new[] { '\t' });

            if (cmds[0] == "GET")
            {
                if (cmds.Length != 2) { await socket.SendStringAsync($"ERROR\tExpected 'GET fn', not '{msg}'"); return; }
                var fn = $@"{dir}\{cmds[1]}";
                var document = project.Documents.Where(d => Path.GetFileName(d.FilePath).ToLower() == Path.GetFileName(fn).ToLower()).FirstOrDefault();
                if (document == null) { await socket.SendStringAsync($"ERROR\tFile doesn't exist '{fn}'"); return; }
                var s = (await document.GetTextAsync()).ToString().Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
                await socket.SendStringAsync($"GOT\t{fn}\t{s}");
            }

            else if (cmds[0] == "CHANGE")
            {
                if (cmds.Length != 5) { await socket.SendStringAsync($"ERROR\tExpected 'CHANGE file offset length text', not '{msg}'"); return; }
                var fn = cmds[1];
                var document = project.Documents.Where(d => Path.GetFileName(d.FilePath).ToLower() == Path.GetFileName(fn).ToLower()).FirstOrDefault();
                if (document == null) { await socket.SendStringAsync($"ERROR\tFile doesn't exist '{fn}'"); return; }
                int offset; if (!int.TryParse(cmds[2], out offset)) { await socket.SendStringAsync($"ERROR\tCan't parse integer offset '{cmds[2]}'"); return; }
                int len; if (!int.TryParse(cmds[3], out len)) { await socket.SendStringAsync($"ERROR\tCan't parse integer length '{cmds[3]}'"); return; }
                var s = cmds[4].Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\\", "\\");
                //
                var change = new TextChange(new TextSpan(offset, len), s);
                var txt = await document.GetTextAsync();
                document = document.WithText(txt.WithChanges(change));
                project = document.Project;
                host.TriggerReplayAsync(project);
            }

            else if (cmds[0] == "WATCH")
            {
            }

            else
            {
                await socket.SendStringAsync($"ERROR\tServer doesn't recognize command '{msg}'");
            }
        }
    }
}

class ReplayHostManager : IDisposable
{
    CancellationTokenSource Cancel;
    Task Task;
    //
    ImmutableArray<Tuple<int,Diagnostic>> CurrentClientDiagnostics = ImmutableArray<Tuple<int,Diagnostic>>.Empty;
    public event Action<string> DiagnosticChanged;
    static int DiagnosticCount;
    //
    string WatchFile; int WatchLine, WatchLineCount; ImmutableArray<int> WatchMissing;
    TaskCompletionSource<object> WatchChanged;
    public event Action<int, string> LineChanged;

    public void TriggerReplayAsync(Project project)
    {
        Cancel?.Cancel();
        Cancel = new CancellationTokenSource();
        Task = ReplayInnerAsync(project, Task, Cancel.Token);
    }

    public void WatchAndMissing(Document document, int line, int lineCount, IEnumerable<int> missing)
    {
        WatchFile = document.FilePath;
        WatchLine = line;
        WatchLineCount = lineCount;
        WatchMissing = missing.ToImmutableArray();
        WatchChanged?.TrySetResult(null);
    }

    class IntDiagnosticComparer : IEqualityComparer<Tuple<int, Diagnostic>>
    {
        public string ToString(Diagnostic diagnostic)
        {
            var file = "";
            int offset = -1, length = -1;
            if (diagnostic.Location.IsInSource) { file = diagnostic.Location.SourceTree.FilePath; offset = diagnostic.Location.SourceSpan.Start; length = diagnostic.Location.SourceSpan.Length; }
            var dmsg = $"{diagnostic.Id}: {diagnostic.GetMessage()}";
            return $"{diagnostic.Severity}\t{file}\t{offset}\t{length}\t{dmsg}";
        }
        public bool Equals(Tuple<int, Diagnostic> x, Tuple<int, Diagnostic> y) => ToString(x.Item2) == ToString(y.Item2);
        public int GetHashCode(Tuple<int, Diagnostic> obj) => ToString(obj.Item2)?.GetHashCode() ?? 0;
    }
    static readonly IntDiagnosticComparer Comparer = new IntDiagnosticComparer();

    async Task ReplayInnerAsync(Project project, Task prevTask, CancellationToken cancel)
    {
        if (prevTask != null) try { await prevTask.ConfigureAwait(false); } catch (Exception) { }
        if (project == null) return;

        project = await ReplayHost.InstrumentProjectAsync(project, cancel).ConfigureAwait(false);
        var results = await ReplayHost.BuildAsync(project, cancel).ConfigureAwait(false);

        // Update warnings+errors
        var diagnostics = results.Diagnostics.Select(d => Tuple.Create(DiagnosticCount++, d)).ToImmutableArray();
        var toRemove = CurrentClientDiagnostics.Except(diagnostics, Comparer).ToImmutableArray();
        var toAdd = diagnostics.Except(CurrentClientDiagnostics, Comparer).ToImmutableArray();
        foreach (var id in CurrentClientDiagnostics.Except(diagnostics, Comparer)) DiagnosticChanged?.Invoke($"DIAGNOSTIC\tremove\t{id.Item1}\t{Comparer.ToString(id.Item2)}");
        foreach (var id in diagnostics.Except(CurrentClientDiagnostics, Comparer)) DiagnosticChanged?.Invoke($"DIAGNOSTIC\tadd\t{id.Item1}\t{Comparer.ToString(id.Item2)}");
        CurrentClientDiagnostics = diagnostics;
        if (!results.Success) return;

        var host = await ReplayHost.RunAsync(results.ReplayOutputFilePath, cancel).ConfigureAwait(false);
        if (WatchLineCount != 0) host.WatchAndMissing(WatchFile, WatchLine, WatchLineCount, null);
        WatchChanged = new TaskCompletionSource<object>();
        var replayTask = host.ReadReplayAsync(cancel);
        while (true)
        {
            await Task.WhenAny(WatchChanged.Task, replayTask).ConfigureAwait(false);
            if (WatchChanged.Task.IsCompleted)
            {
                host.WatchAndMissing(WatchFile, WatchLine, WatchLineCount, WatchMissing);
                WatchChanged = new TaskCompletionSource<object>();
            }
            else if (replayTask.IsCompleted)
            {
                var replay = await replayTask.ConfigureAwait(false);
                if (replay == null) return;
                LineChanged?.Invoke(replay.Item1, replay.Item2);
                replayTask = host.ReadReplayAsync(cancel);
            }
        }
    }

    public void Dispose()
    {
        Cancel?.Cancel();
    }
}



public class Startup
{
    public void Configure(IApplicationBuilder app)
    {
        app.UseWebSockets();
        app.UseDeveloperExceptionPage();
        app.Use(async (http, next) =>
        {
            if (!http.WebSockets.IsWebSocketRequest) { await next(); return; }
            using (var socket = await http.WebSockets.AcceptWebSocketAsync())
            {
                if (socket == null || socket.State != WebSocketState.Open) return;
                await Program.DoSocketAsync(http, socket);
            }
        });
        app.Run(async (context) =>
        {
            await context.Response.WriteAsync("Hello World!");
        });
    }
}

public static class Extensions
{
    public static async Task SendStringAsync(this WebSocket socket, string s, CancellationToken cancel = default(CancellationToken))
    {
        var buf = new ArraySegment<byte>(Encoding.UTF8.GetBytes(s));
        await socket.SendAsync(buf, WebSocketMessageType.Text, true, cancel);
    }

    public static async Task<string> RecvStringAsync(this WebSocket socket, CancellationToken cancel = default(CancellationToken))
    {
        var buf = new ArraySegment<byte>(new byte[4096]);
        var sb = new StringBuilder();
        while (true)
        {
            var red = await socket.ReceiveAsync(buf, cancel);
            if (red.MessageType == WebSocketMessageType.Close) return null;
            if (red.MessageType != WebSocketMessageType.Text) throw new InvalidOperationException("binary");
            sb.Append(Encoding.UTF8.GetString(buf.Array, buf.Offset, red.Count));
            if (red.EndOfMessage) return sb.ToString();
        }
    }
}
