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

public class TestWebsocketService
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
            if (!Directory.Exists(dir)) { await socket.SendStringAsync($"ERROR\tProject doesn't exist '{dir}'"); return; }

            // Load the project, and establish the "OK" handshake to show we've done it
            var project = await ScriptWorkspace.FromDirectoryScanAsync(dir);

            await socket.SendStringAsync("OK");
            var cmd = await socket.RecvStringAsync();
            if (cmd != "OK") { await socket.SendStringAsync($"ERROR\tExpected 'OK' not '{cmd}'"); return; }

            // Set up monitoring for diagnostics
            var host = new ReplayHost(true);
            ReplayHost.AdornmentChangedHandler lambdaAdornmentChanged = async (isAdd, tag, file, line, content, deferrable, cancel) =>
            {
                // ADORNMENT remove 7
                // ADORNMENT ADD 7 231 Hello world
                var deferral = deferrable.GetDeferral();
                var msg = isAdd ? $"ADORNMENT\tadd\t{tag}\t{file}\t{line+1}\t{content}" : $"ADORNMENT\tremove\t{tag}\t{file}";
                if (socket.State != WebSocketState.Closed) await socket.SendStringAsync(msg);
                deferral.Complete();
            };
            ReplayHost.DiagnosticChangedHandler lambdaDiagnosticChanged = async (isAdd, tag, diagnostic, deferrable, cancel) =>
            {
                // DIAGNOSTIC remove 7 file.cs
                // DIAGNOSTIC add 7 file.cs Hidden startLine startCol length msg
                var deferral = deferrable.GetDeferral();
                string msg;
                if (isAdd)
                {
                    var file = ""; int startLine = -1, startCol = -1, length = 0;
                    if (diagnostic.Location.IsInSource)
                    {
                        var loc = diagnostic.Location.GetMappedLineSpan();
                        file = loc.HasMappedPath ? loc.Path : diagnostic.Location.SourceTree.FilePath;
                        startLine = loc.StartLinePosition.Line + 1;
                        startCol = loc.StartLinePosition.Character + 1;
                        length = diagnostic.Location.SourceSpan.Length;
                    }
                    msg = $"DIAGNOSTIC\tadd\t{tag}\t{file}\t{diagnostic.Severity}\t{startLine}\t{startCol}\t{length}\t{diagnostic.Id}: {diagnostic.GetMessage()}";
                }
                else
                {
                    msg = $"DIAGNOSTIC\tremove\t{tag}";
                }
                if (socket.State != WebSocketState.Closed) await socket.SendStringAsync(msg);
                deferral.Complete();
            };
            ReplayHost.ReplayHostError lambdaErred = async (error, deferrable, cancel) =>
            {
                var deferral = deferrable.GetDeferral();
                if (socket.State != WebSocketState.Closed) await socket.SendStringAsync($"ERROR\tCLIENT: {error}");
                deferral.Complete();
            };

            host.AdornmentChanged += lambdaAdornmentChanged;
            host.DiagnosticChanged += lambdaDiagnosticChanged;
            host.Erred += lambdaErred;

            var dummy = host.ChangeDocumentAsync(project, null, -1, -1, -1);

            // Run the conversation!
            while (true)
            {
                cmd = await socket.RecvStringAsync();
                if (cmd == null) { host.AdornmentChanged -= lambdaAdornmentChanged; host.DiagnosticChanged -= lambdaDiagnosticChanged; host.Erred -= lambdaErred; return; }
                var cmds = cmd.Split(new[] { '\t' });

                if (cmds[0] == "GET")
                {
                    if (cmds.Length != 2) { await socket.SendStringAsync($"ERROR\tExpected 'GET fn', not '{cmd}'"); return; }
                    var document = project.Documents.SingleOrDefault(d => d.Name == cmds[1]);
                    if (document == null) { await socket.SendStringAsync($"ERROR\tFile doesn't exist '{cmds[1]}'"); return; }
                    var s = (await document.GetTextAsync()).ToString().Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
                    await socket.SendStringAsync($"GOT\t{cmds[1]}\t{s}");
                }

                else if (cmds[0] == "CHANGE")
                {
                    string file, newContent; int startLine, startCol, oldLineCount, newLineCount, oldLength; Document document;
                    if (cmds.Length != 8
                        || (file = cmds[1]) == null
                        || (document = project.Documents.SingleOrDefault(d => d.Name == file)) == null
                        || !int.TryParse(cmds[2], out startLine)
                        || !int.TryParse(cmds[3], out startCol)
                        || !int.TryParse(cmds[4], out oldLineCount)
                        || !int.TryParse(cmds[5], out newLineCount)
                        || !int.TryParse(cmds[6], out oldLength)
                        || (newContent = cmds[7].Replace("\\n","\n").Replace("\\r","\r").Replace("\\\\","\\")) == null)
                    {
                        await socket.SendStringAsync($"ERROR\tExpected 'CHANGE file startLine startCol oldLineCount newLineCount oldLength newContent', not '{cmd}'");
                        continue;
                    }
                    //
                    var txt = await document.GetTextAsync();
                    var startPosition = txt.Lines[startLine - 1].Start + startCol - 1;
                    var change = new TextChange(new TextSpan(startPosition, oldLength), newContent);
                    txt = txt.WithChanges(change);
                    document = document.WithText(txt);
                    project = document.Project;
                    if (file.EndsWith(".md"))
                    {
                        var csx = ScriptWorkspace.Md2Csx(file, txt.ToString());
                        var csxDocument = project.Documents.Single(d => d.Name == file + ".csx");
                        csxDocument = csxDocument.WithText(SourceText.From(csx));
                        project = csxDocument.Project;
                    }
                    //
                    dummy = host.ChangeDocumentAsync(project, document.FilePath, startLine-1, oldLineCount, newLineCount);
                }

                else if (cmds[0] == "WATCH")
                {
                    string file; int line=-1, count=-1; Document document = null;
                    if ((cmds.Length != 4 && cmds.Length != 2)
                        || (file = cmds[1]) == null
                        || (file != "*" && (document = project.Documents.Single(d => d.Name == file)) == null)
                        || (cmds.Length == 4 && !int.TryParse(cmds[2], out line))
                        || (cmds.Length == 4 && !int.TryParse(cmds[3], out count)))
                    {
                        await socket.SendStringAsync($"ERROR\tExpected 'WATCH file line count', got '{cmd}'");
                        continue;
                    }
                    //
                    dummy = host.WatchAsync(file == "*" ? file : document.FilePath, line, count);
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
                await TestWebsocketService.DoSocketAsync(http, socket);
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
