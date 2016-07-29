#region preamble into the night my good fellow
using Xunit;
using System.Runtime.CompilerServices;
#endregion

class AutoRunAttribute : System.Attribute { }


class Program
{
    static void Main()
    {
        new Program().Main2();
    }

    void Main2()
    { 
        Replay.Log<object>(null, null, null, -1, -1);
        var txt = Replay.Log<string>(GetText(), "txt", "methods.md", 9, 1);
        System.Console.WriteLine(txt);
        Replay.Log<object>(null, null, "methods.md", 10, 3);
        //
        AutorunMethods = new[] { "Program\tTestMyFunction\tmethods.md\t999" };
        Replay.AutorunMethods(() => AutorunMethods);
    }

    string[] AutorunMethods;

    string GetText()
    {
        Replay.Log<object>(null, null, null, -1, 9); return "in a function plz send hlp";
    }

#line 1000 "methods.md"
    [Fact, AutoRun]
    void TestMyFunction()
    {
        Replay.Log<object>(null, null, null, -1, 9);
        var txt = Replay.Log<string>(GetText(), "txt", "methods.md", 30, 1);
        Assert.Equal(txt, "in a function");
        Replay.Log<object>(null, null, "methods.md", 31, 3);
    }
}

public static class GetAutorunMethods
{
    public static string[] Get()
    {
        return new[] { "Program\tTestMyFunction\tSampleTransformedCode.cs\t23" };
    }
}
