## Methods

```csharp
#r "xunit, 2.2.0-beta2-build3300"
using System;
using Xunit;
```

When we write code, we usually split it into *methods*. This code has a method named `MyMethod`:

```csharp
var result = MyMethod();
Console.WriteLine(result);

string MyMethod()
{
    return "in a method plz send hlp";
}
```

___Exercise 1:___ Change the name of the above method from `MyMethod` to `GetValue`. You'll have to change two places --
where you *invoke* the method, and where you *declare* it.

___Exercise 2:___ Change the method from returning the text string `"in a method..."` to returning the integer `42`. Hint:
also change the *declared return type* of the method from `string` to `int`.

<br/>

When we write a method, we usually also test that it's doing the right thing. Here's how we might test the above method.
(If you look at the line numbers, you'll see that this block of code continues on from the previous one).


```csharp
[Fact, AutoRun]
void TestMyFunction()
{
    var testResult = MyMethod();
    Assert.Equal(41, testResult);
}
```

___Exercise 3:___ This test is failing in several ways. Can you fix it?

Tip: `[Fact]` indicates a test, and `[AutoRun]` indicates the test gets run automatically while you're editing online.



<br/>

Download this app as a single-file native binary:
<span>
    <style>
        button {margin:0; border:0; padding:1ex; background-color:white; color:#333;}
        .downloadactive, button:hover {background-color:#0492c8; color:white;}
    </style>
    <button type="button" class="downloadactive">MacOS</button><button type="button">Linux</button><button type="button">Docker</button><button type="button">Windows</button>
</span>


<br/>

___[>> Proceed to the next tutorial, "Web service"...](NYI.html)___
