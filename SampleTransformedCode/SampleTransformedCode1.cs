using Xunit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;


class Program1
{
    static void NotMain()
    {
        new Program1().MainAsync().GetAwaiter().GetResult();
    }

    async Task MainAsync()
    {
        AutorunMethods = new[] { "Program1\tTestMyFunction\tmethods.md\t999" };
        await Replay.InitAsync(() => AutorunMethods);
        //
        Replay.Log<object>(null, null, null, -1, -1);
        var txt = Replay.Log<string>(GetText(), "txt", "methods.md", 9, 1);
        await Task.Delay(100);
        System.Console.WriteLine(txt);
        Replay.Log<object>(null, null, "methods.md", 10, 3);
    }

    string[] AutorunMethods;

    string GetText()
    {
        Replay.Log<object>(null, null, null, -1, 9);
        return Replay.Log("in a function plz send hlp", "return", "methods.md", 25, 2);
    }

    [Fact, AutoRun]
    void TestMyFunction()
    {
        Replay.Log<object>(null, null, null, -1, 9);
        var txt = Replay.Log<string>(GetText(), "txt", "methods.md", 30, 1);
        Assert.Equal(txt, "in a function");
        Replay.Log<object>(null, null, "methods.md", 31, 3);
    }
}
