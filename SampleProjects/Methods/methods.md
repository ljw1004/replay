## Methods

```csharp
#r "xunit, 2.2.0-beta2-build3300"
using System;
using Xunit;
```

When we write code, we usually split it into *functions*. This code has a function named `GetValue`:

```csharp
var result = GetValue();
Console.WriteLine(result);

string GetValue()
{
    return "in a function plz send hlp";
}
```

___Exercise 1:___ Change the name of the above function from `GetValue` to `GetSampleValue`. You'll have to change two places --
where you *invoke* the function, and where you *declare* it.

___Exercise 2:___ Chang the function from returning the text string `"in a function..."` to returning the integer `42`. Hint:
also change the *declared return type* of the function from `string` to `int`.

<br/>

When we write a function, we usually also test that it's doing the right thing. Here's how we might test the above function:


```csharp
[Fact, AutoRun]
void TestMyFunction()
{
    var testResult = GetValue();
    Assert.Equal("in a function", testResult);
}
```

___Exercise 3:___ This test is failing in several ways. Can you fix it?

Tip: `[Fact]` indicates a test, and `[AutoRun]` indicates the test gets run automatically while you're editing online.



<br/>

Download this app as a single-file native binary:
<span>
    <style scoped>
        button {margin:0; border:0; padding:1ex; background-color:white; color:#333;}
        .active, button:hover {background-color:#0492c8; color:white;}
    </style>
    <button type="button" class="active">MacOS</button><button type="button">Linux</button><button type="button">Docker</button><button type="button">Windows</button>
</span>
