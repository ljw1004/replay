# Methods

```csharp
#region Pease ignore this preamble for now...
#r "xunit, 2.2.0-beta2-build3300"
using Xunit;
class AutoRunAttribute : System.Attribute { }
#endregion
```

Let's separate out the code into building-blocks, known in code as "functions":

```csharp
var result = GetValue();
System.Console.WriteLine(result);

string GetValue()
{
    return "in a function plz send hlp";
}
```

Try changing the name of the above function from `GetValue` to `GetSampleValue`. You have to change both
the place where you *invoke* the function, and the place where you *declare* it.
Try changing it to return the integer (`int`) `53` instead of the `string` `"in a function..."`.

<br/>

When we write a function, we always want to test that it's doing the right thing. Here's how we might test the above function:

```csharp
[Fact, AutoRun]
void TestMyFunction()
{
    var result = GetValue();
    Assert.Equal(result, "in a function");
}
```

The tests in your code get run automatically while editing. This one is failing in several ways. Can you fix it?


<br/>

Download this app as a single-file native binary:
<span>
    <style scoped>
        button {margin:0; border:0; padding:1ex; background-color:white; color:#333;}
        .active, button:hover {background-color:#0492c8; color:white;}
    </style>
    <button type="button" class="active">MacOS</button><button type="button">Linux</button><button type="button">Docker</button><button type="button">Windows</button>
</span>

