using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

class Program
{
    static void Main()
    {
        MainAsync().GetAwaiter().GetResult();
    }

    static async Task MainAsync()
    {
        var workspace = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(@"C:\Users\ljw10\Documents\Visual Studio 2015\Projects\ConsoleApplicationCS\ConsoleApplicationCS.sln");
        var originalProject = solution.Projects.Single();
        var newProject = await ReplayHost.InstrumentAsync(originalProject);

        foreach (var document in newProject.Documents)
        {
            var txt = await document.GetTextAsync();
            Console.WriteLine(document.FilePath);
            Console.WriteLine(txt);
        }

    }

}


