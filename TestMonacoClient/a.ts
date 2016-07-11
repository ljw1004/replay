/// <reference path="node_modules/monaco-editor/monaco.d.ts"/>

function log(o:any):void
{
    var s = JSON.stringify(o);
    console.log(s);
    var e = document.getElementById("log");
    e.innerText = s + "\n" + e.innerText;
}

class Channel<T>
{
    _posts:T[] = [];  
    _recvs:((value:T)=>void)[] = [];
    recv():Promise<T>
    {
       if (this._posts.length > 0) return new Promise<T>(resolve => resolve(this._posts.shift()));
       else return new Promise<T>(resolve => this._recvs.push(resolve));
    }
    post(value:T):void
    {
        if (this._recvs.length > 0) this._recvs.shift()(value);
        else this._posts.push(value);
    }
}

var editor : monaco.editor.IStandaloneCodeEditor;
var stdin = new Channel<string>();
var connection : WebSocket;
var ignoreModelDidChangeContent : boolean = false;


require.config({ paths: { 'vs': 'node_modules/monaco-editor/dev/vs'}});
require(['vs/editor/editor.main'], function() {
    editor = monaco.editor.create(document.getElementById('container'), {
        language:"csharp",
        roundedSelection:true,
        theme:"vs-dark"
    });
    connection = new WebSocket('ws://localhost:60828/project1');
    connection.onopen = function() {};
    connection.onmessage = function(ev:MessageEvent) { stdin.post(ev.data);}
    connection.onerror = function(ev:Event) {stdin.post(null); }
    connection.onclose = function() { stdin.post(null); }
    editor.getModel().onDidChangeContent(modelDidChangeContent);
    window.onresize = function() { editor.layout(); }
    startDialog();
});


function modelDidChangeContent(e:monaco.editor.IModelContentChangedEvent2):void
{
    if (ignoreModelDidChangeContent) return;
    var pos = new monaco.Position(e.range.startLineNumber, e.range.startColumn);
    var offset = editor.getModel().getOffsetAt(pos);
    var s = e.text.replace(/\\/g,"\\\\").replace(/\r/g,"\\r").replace(/\n/g,"\\n");
    connection.send(`CHANGE\tcode1.csx\t${offset}\t${e.rangeLength}\t${s}`);
}

async function startDialog():Promise<void>
{
    var ok = await stdin.recv();
    if (ok != "OK") {log(`error: expected 'OK', got '${ok}'`); return;}
    connection.send("OK");
    connection.send("GET\tcode1.csx");
    var txt = await stdin.recv();
    txt = txt.replace(/\\r/g,"\r").replace(/\\n/g,"\n").replace(/\\\\/g,"\\");
    ignoreModelDidChangeContent = true;
    editor.getModel().setValue(txt);
    ignoreModelDidChangeContent = false;
    while (true)
    {
        var resp = await stdin.recv();
        if (resp === null) return;
        log(resp);
    }
}
