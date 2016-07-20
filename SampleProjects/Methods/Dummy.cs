using Xunit;

public class Program
{
    static void Main()
    {
        var txt = GetText();
        System.Console.WriteLine(txt);
        try
        {
            TestMyFunction();
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine(ex.Message);
        }
    }

    static string GetText()
    {
        return "in a function plz send hlp";
    }

    [Fact, AutoRun]
    static void TestMyFunction()
    {
        var txt = GetText();
        Assert.Equal(txt, "in a function");
    }

}   

class AutoRunAttribute : System.Attribute
{
}