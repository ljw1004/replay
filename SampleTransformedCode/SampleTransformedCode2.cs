using Xunit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

class Program2
{
    static void Main()
    {
        new Program2().MainAsync().GetAwaiter().GetResult();
    }

    TaskCompletionSource<int> tcs = new TaskCompletionSource<int>();

    async Task MainAsync()
    {
        AutorunMethods = new[] { "Program2\tTestMyFunction\tmethods.md\t999" };
        await Replay.InitAsync(() => AutorunMethods);

        var txt = GetText();
        await Task.Delay(1000);
        await tcs.Task;
        System.Console.WriteLine(txt);
    }

    string[] AutorunMethods;

    string GetText()
    {
        return "in a function plz send hlp";
    }

    [Fact, AutoRun]
    void TestMyFunction()
    {
        var txt = GetText();
        try
        {
            Assert.Equal(txt, "in a function");
        }
        finally
        {
            tcs.SetResult(1);
        }
    }
}
