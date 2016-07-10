using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class Program
{

    static void Main()
    {
        Replay.Log<object>(null, null, null, -1, -1);

        Replay.Log("hello", "x", "file.cs", 10, 1);
        Thread.Sleep(TimeSpan.FromSeconds(20));
        Replay.Log("world", "x", "file.cs", 10, 1);
    }

}
