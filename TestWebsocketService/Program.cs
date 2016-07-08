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
            var socket = await http.WebSockets.AcceptWebSocketAsync();
            if (socket == null || socket.State != WebSocketState.Open) return;
            //
            var buf1 = new ArraySegment<byte>(Encoding.UTF8.GetBytes("OK"));
            await socket.SendAsync(buf1, WebSocketMessageType.Text, true, CancellationToken.None);
            //
            var buf2 = new ArraySegment<byte>(new byte[4096]);
            while (socket.State == WebSocketState.Open)
            {
                var red = await socket.ReceiveAsync(buf2, CancellationToken.None);
                if (red.MessageType != WebSocketMessageType.Text) continue;
                var txt = Encoding.UTF8.GetString(buf2.Array, buf2.Offset, buf2.Count);
                System.Diagnostics.Debug.WriteLine(txt);
                //
                var buf3 = new ArraySegment<byte>(Encoding.UTF8.GetBytes("OK " + txt));
                await socket.SendAsync(buf3, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        });
        app.Run(async (context) =>
        {
            await context.Response.WriteAsync("Hello World!");
        });
    }
}
