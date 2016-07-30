## Hello World

This is the simplest C# program:

```csharp
System.Console.WriteLine("hello world");
```

___Exercise 1:___ Edit the code! Try putting your name in there, and see how the output changes to the right.


<br/>

Here's something a bit more advanced. It uses a *variable* called `i` which can hold any integer:

```csharp
int i = 0;
while (i < 3)
{
    System.Console.WriteLine("hello");
    i = i + 1;
}
```

___Exercise 2:___ You can see it printed the message three times. Make it print `10` times. Make it print infinity times.
Hint: `while (true)`.

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

___[>> Proceed to the next tutorial, "Methods"...](Methods.html)___
