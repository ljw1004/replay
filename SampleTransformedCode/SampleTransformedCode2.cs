using Xunit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;


class Program2
{
    static void Main()
    {
        new Program2().MainAsync().GetAwaiter().GetResult();
    }

    async Task MainAsync()
    {
        AutorunMethods = new[] { "Program2\tTestMyFunction\tmethods.md\t999" };
        await Replay.InitAsync(() => AutorunMethods);
        //
        var txt = GetText();
        await Task.Delay(100);
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
        Assert.Equal(txt, "in a function");
    }
}
