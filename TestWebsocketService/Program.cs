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
        if (!Directory.Exists(dir)) { await socket.SendStringAsync($"ERROR\tDirectory doesn't exist '{dir}'"); return; }
        await socket.SendStringAsync("OK");
        var msg = await socket.RecvStringAsync();
        if (msg != "OK") { await socket.SendStringAsync($"ERROR\tExpected 'OK' not '{msg}'"); return; }
        //
        while (true)
        {
            msg = await socket.RecvStringAsync();
            if (msg == null) return;
            if (msg.StartsWith("GET\t"))
            {
                var fn = $@"{dir}\{msg.Substring(4)}";
                if (!File.Exists(fn)) await socket.SendStringAsync($"ERROR\tFile doesn't exist '{fn}'");
                else { var s = File.ReadAllText(fn).Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n"); await socket.SendStringAsync(s); }
            }
            else
            {
                await socket.SendStringAsync("pingback " + msg);
            }
        }
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
