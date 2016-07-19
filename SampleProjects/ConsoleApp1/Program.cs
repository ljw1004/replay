using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

static class Program
{
    static void Main()
    {
        var mn = "I am in main";
        Console.WriteLine(mn);
        var x = mn.Length;
    }
}

public class Helpers
{
    public static void PrintLots(int count)
    {
        for (int i = 0; i < count; i++) Console.WriteLine(count);
    }
}
