//"xunit": "2.2.0-beta2-build3300"
//using Xunit;
using System.Runtime.CompilerServices;

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

    //[Fact, AutoRun]
    //void TestMyFunction()
    //{
    //    Replay.Log<object>(null, null, null, -1, 9);
    //    var txt = Replay.Log<string>(GetText(), "txt", "methods.md", 30, 1);
    //    Assert.Equal(txt, "in a function");
    //    Replay.Log<object>(null, null, "methods.md", 31, 3);
    //}
}
