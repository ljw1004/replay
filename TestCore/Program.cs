using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis.Text;
using System.Threading;
using Microsoft.CodeAnalysis.Diagnostics;

public class Program
{
    public static void Main()
    {
        //var workspace = new AdhocWorkspace();
        //var projInfo = CreateProjectInfo("projName", LanguageNames.CSharp, File.ReadAllLines(@"C:\Users\lwischik\source\Repos\replay\SampleProjects\Methods\obj\replay\dotnet-compile-csc.rsp"), null, workspace);
        //var project = workspace.AddProject(projInfo);
        var dir = Directory.GetCurrentDirectory();
        for (; dir != null; dir = Path.GetDirectoryName(dir)) if (Directory.Exists(dir + "/SampleProjects")) break;
        dir = Path.GetFullPath(dir + "/SampleProjects/Methods");
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);

        var project = ScriptWorkspace.FromDirectoryAsync(dir).Result;

        var dd = project.GetCompilationAsync().Result.GetDiagnostics();
        foreach (var d in dd)
        {
            if (d.Severity != DiagnosticSeverity.Warning && d.Severity != DiagnosticSeverity.Error) continue;
            Console.WriteLine(d);
        }


    }

}

