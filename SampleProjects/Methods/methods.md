```csharp
#r "xunit, 2.2.0-beta2-build3300"
using Xunit;
class AutoRunAttribute : System.Attribute { }
```

```csharp
var txt = GetText();
System.Console.WriteLine(txt);

string GetText()
{
    return "in a function plz send hlp";
}
```

```csharp
[Fact, AutoRun]
void TestMyFunction()
{
    var txt = GetText();
    Assert.Equal(txt, "in a function");
}
```
