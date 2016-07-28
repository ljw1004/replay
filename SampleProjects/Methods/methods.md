# Methods

```csharp
#r "xunit, 2.2.0-beta2-build3300"
using Xunit;
class AutoRunAttribute : System.Attribute { }
```

Let's separate out the code into building-blocks, known in code as "functions":

```csharp
var txt = GetText();
System.Console.WriteLine(txt);

string GetText()
{
    return "in a function plz send hlp";
}
```

Try changing the name of the function to `GetSampleText`. You have to change both
the place where you *invoke* the function, and the place where you *declare* it.
Try changing it to return an integer.

<br/>

When we write a function, we always want to test what it's doing. Here's how:

```csharp
[Fact, AutoRun]
void TestMyFunction()
{
    var txt = GetText();
    Assert.Equal(txt, "in a function");
}
```

The tests in your code get run automatically while editing. This one failed. Can you fix it?


<br/>

Download this app as a single-file native binary:
<span>
    <style scoped>
        button {margin:0; border:0; padding:1ex; background-color:white; color:#333;}
        .active, button:hover {background-color:#0492c8; color:white;}
    </style>
    <button type="button" class="active">MacOS</button><button type="button">Linux</button><button type="button">Docker</button><button type="button">Windows</button>
</span>

