[Fact, AutoRun]
void TestMyFunction()
{
    var txt = GetText();
    Assert.Equal(txt, "in a function");
}