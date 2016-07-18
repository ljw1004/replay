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

    public static Document GetDocumentByName(Project project, string name)
    {
        var dir = Path.GetDirectoryName(project.FilePath);
        var fn = Path.GetFullPath($"{dir}/{name}");
        foreach (var d in project.Documents)
        {
            var dfn = Path.GetFullPath(d.FilePath);
            if (string.Equals(fn, dfn, StringComparison.CurrentCultureIgnoreCase)) return d;
        }
        return null;
    }

    public static async Task DoSocketAsync(HttpContext http, WebSocket socket)
    {
        Exception _ex = null;
        try
        {

            if (!http.Request.Path.HasValue) { await socket.SendStringAsync("ERROR\tNo project specified"); return; }

            var dir = Directory.GetCurrentDirectory();
            for (; dir != null; dir = Path.GetDirectoryName(dir))
            {
                if (Directory.Exists(dir + "/SampleProjects")) break;
            }
            dir = Path.GetFullPath(dir + "/SampleProjects" + http.Request.Path.Value);
            if (!File.Exists(dir+"/project.json")) { await socket.SendStringAsync($"ERROR\tProject doesn't exist '{dir}/project.json'"); return; }

            // Load the project, and establish the "OK" handshake to show we've done it
            var workspace = new Microsoft.DotNet.ProjectModel.Workspaces.ProjectJsonWorkspace(dir);
            var solution = workspace.CurrentSolution;
            var project = solution.Projects.Single();
            // Add script files manually (since they're not done by ProjectJsonWorkspace)
            foreach (var csxfn in Directory.GetFiles(dir, "*.csx"))
            {
                project = project.AddDocument(csxfn, File.ReadAllText(csxfn), null, csxfn).WithSourceCodeKind(SourceCodeKind.Script).Project;
            }
            await socket.SendStringAsync("OK");
            var cmd = await socket.RecvStringAsync();
            if (cmd != "OK") { await socket.SendStringAsync($"ERROR\tExpected 'OK' not '{cmd}'"); return; }


            // Set up monitoring for diagnostics
            var queue = new BufferBlock<string>();
            var host = new ReplayHost(true);
            host.AdornmentChanged += async (isAdd, tag, line, content, deferral, cancel) =>
            {
                // ADORNMENT remove 7
                // ADORNMENT ADD 7 231 Hello world
                var msg = isAdd ? $"ADORNMENT\tadd\t{tag}\t{line}\t{content}" : $"ADORNMENT\tremove\t{tag}";
                await socket.SendStringAsync(msg);
                deferral.SetResult(null);
            };
            host.DiagnosticChanged += async (isAdd, tag, diagnostic, deferral, cancel) =>
            {
                // DIAGNOSTIC remove 7
                // DIAGNOSTIC add 7 Hidden file.cs 70 24 CS8019: Unnecessary using directive.
                var msg = isAdd ? $"DIAGNOSTIC\tadd\t{tag}\t{DiagnosticUserFacingComparer.ToString(diagnostic)}" : $"DIAGNOSTIC\tremove\t{tag}";
                await socket.SendStringAsync(msg);
                deferral.TrySetResult(null);
            };
            host.Erred += async (error, deferral, cancel) =>
            {
                await socket.SendStringAsync($"ERROR\tCLIENT: {error}");
                deferral.TrySetResult(null);
            };
            var dummy = host.ChangeDocumentAsync(project, null, -1, -1, -1);

            // Run the conversation!
            while (true)
            {
                if (queue.TryReceive(out cmd)) { await socket.SendStringAsync(cmd); continue; }

                cmd = await socket.RecvStringAsync();
                if (cmd == null) return;
                var cmds = cmd.Split(new[] { '\t' });

                if (cmds[0] == "GET")
                {
                    if (cmds.Length != 2) { await socket.SendStringAsync($"ERROR\tExpected 'GET fn', not '{cmd}'"); return; }
                    var document = GetDocumentByName(project, cmds[1]);
                    if (document == null) { await socket.SendStringAsync($"ERROR\tFile doesn't exist '{cmds[1]}'"); return; }
                    var s = (await document.GetTextAsync()).ToString().Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
                    await socket.SendStringAsync($"GOT\t{cmds[1]}\t{s}");
                }

                else if (cmds[0] == "CHANGE")
                {
                    string file, content; int position, length; Document document;
                    if (cmds.Length != 5
                        || (file = cmds[1]) == null
                        || (document = GetDocumentByName(project, file)) == null
                        || !int.TryParse(cmds[2], out position)
                        || !int.TryParse(cmds[3], out length)
                        || (content = cmds[4].Replace("\\n","\n").Replace("\\r","\r").Replace("\\\\","\\")) == null)
                    {
                        await socket.SendStringAsync($"ERROR\tExpected 'CHANGE file position length content', not '{cmd}'");
                        continue;
                    }
                    //
                    var change = new TextChange(new TextSpan(position, length), content);
                    var treeBefore = await document.GetSyntaxTreeAsync();
                    var txt = await document.GetTextAsync();
                    document = document.WithText(txt.WithChanges(change));
                    var treeAfter = await document.GetSyntaxTreeAsync();
                    project = document.Project;
                    //
                    var line = treeBefore.GetLineSpan(change.Span).StartLinePosition.Line;
                    var count = treeBefore.GetLineSpan(change.Span).EndLinePosition.Line - line + 1;
                    var newcount = treeAfter.GetLineSpan(new TextSpan(position, content.Length)).EndLinePosition.Line - line + 1;
                    dummy = host.ChangeDocumentAsync(project, file, line, count, newcount);
                }

                else if (cmds[0] == "WATCH")
                {
                    string file; int line, count; Document document;
                    if (cmds.Length != 4
                        || (file = cmds[1]) == null
                        || (document = GetDocumentByName(project, file)) == null
                        || !int.TryParse(cmds[2], out line)
                        || !int.TryParse(cmds[3], out count))
                    {
                        await socket.SendStringAsync($"ERROR\tExpected 'WATCH file line count', got '{cmd}'");
                        continue;
                    }
                    //
                    dummy = host.WatchAsync(document.FilePath, line, count);
                }

                else
                {
                    await socket.SendStringAsync($"ERROR\tServer doesn't recognize command '{cmd}'");
                }
            }

        }
        catch (Exception ex)
        {
            _ex = ex;
        }
        if (_ex != null) await socket.SendStringAsync($"ERROR\t{_ex.Message}");
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
