## Methods

```csharp
#r "xunit, 2.2.0-beta2-build3300"
using System;
using Xunit;
using System.Threading.Tasks;

public enum HttpStatusCode
{
    OK = 200,
    NotFound = 404
}

public class HttpClient
{
    public override string ToString() => "HttpClient";

    public async Task<string> GetStringAsync(string url)
    {
        WebHost.singleton_host.incoming.Post(url);
        var r = await WebHost.singleton_host.outgoing.ReceiveAsync();
        return $">HEADERS >PREVIEW >RESPONSE {r}";
    }
}

public class HttpContext
{
    public override string ToString() => "GET " + Request.Url;
    public HttpRequest Request = new HttpRequest();
    public HttpResponse Response = new HttpResponse();
}

public class HttpRequest
{
    public string Url;
}

public class HttpResponse
{
    public int? StatusCode;
    public async Task WriteAsync(string s)
    {
        await Task.Delay(0);
        WebHost.singleton_host.outgoing.Post(s);
    }
}

public class WebHost
{
    public override string ToString() => Url;

    public static WebHost singleton_host;

    private int Port;
    public Channel<string> incoming = new Channel<string>();
    public Channel<string> outgoing = new Channel<string>();

    public WebHost(int port)
    {
        Port = port;
        singleton_host = this;
    }

    public string Url => $"http://localhost:{Port}";

    public async Task<HttpContext> GetNextRequestAsync()
    {
        var req = await incoming.ReceiveAsync();
        var context = new HttpContext();
        context.Request.Url = req;
        return context;
    }

    public class Channel<T>
    {
        System.Collections.Generic.Queue<T> posts = new System.Collections.Generic.Queue<T>();
        System.Collections.Generic.Queue<TaskCompletionSource<T>> recvs = new System.Collections.Generic.Queue<TaskCompletionSource<T>>();

        public Task<T> ReceiveAsync()
        {
            lock (posts)
            {
                if (posts.Count > 0) return Task.FromResult(posts.Dequeue());
                else { var tcs = new TaskCompletionSource<T>(); recvs.Enqueue(tcs); return tcs.Task; }
            }
        }

        public void Post(T value)
        {
            lock (posts)
            {
                if (recvs.Count > 0) recvs.Dequeue().TrySetResult(value);
                else posts.Enqueue(value);
            }
        }
    }

}

```

A web-service is just a program that listens for requests from browsers, and gives a response. Here's the simplest:

```csharp
var listener = new WebHost(8080);
while (true)
{
    var request = await listener.GetNextRequestAsync();
    request.Response.StatusCode = (int)HttpStatusCode.OK;
    await request.Response.WriteAsync("hello!");
}

[Fact, AutoRun]
async void TestService()
{
    var client = new HttpClient();
    var result = await client.GetStringAsync(listener.Url); 
}
```

___Exercise 1:___ Explore the headers that got sent in the test request, and the headers that came back.
Can you make it deliver a "404 not found" response instead?



<br/>

Deploy this webservice
<span>
    <style>
        button {margin:0; border:0; padding:1ex; background-color:white; color:#333;}
        .downloadactive, button:hover {background-color:#0492c8; color:white;}
    </style>
    <button type="button" class="downloadactive">Azure</button>
</span>


<br/>

___[>> Proceed to the next tutorial, "Routing"...](NYI.html)___
