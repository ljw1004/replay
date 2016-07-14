using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

public class Program
{
    public static void Main()
    {
        Replay.Log<object>(null, null, null, -1, -1);

        Replay.Log("hello", "x", "file.cs", 10, 1);
        Thread.Sleep(TimeSpan.FromSeconds(2));
        Replay.Log("world", "x", "file.cs", 10, 1);

    }
}