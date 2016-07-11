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
    var diagnosticIdToZoneId : number[] = [];

    var ok = await stdin.recv();
    if (ok != "OK") {log(`error: expected 'OK', got '${ok}'`); return;}
    connection.send("OK");
    connection.send("GET\tcode1.csx");
    while (true)
    {
        var resp = await stdin.recv();
        var cmds = resp.split('\t');
        if (cmds[0] === "GOT")
        {
            var fn = cmds[1];
            var txt = cmds[2]; 
            txt = txt.replace(/\\r/g,"\r").replace(/\\n/g,"\n").replace(/\\\\/g,"\\");
            ignoreModelDidChangeContent = true;
            editor.getModel().setValue(txt);
            ignoreModelDidChangeContent = false;
        }
        else if (cmds[0] === "DIAGNOSTIC")
        {
            // "DIAGNOSTIC add 7 Hidden file.cs 70 24 CS8019: Unnecessary using directive.
            var diagnosticId : number = Number(cmds[2]);
            if (cmds[1] == "remove" && diagnosticId in diagnosticIdToZoneId)
            {
                var zoneId : number = diagnosticIdToZoneId[diagnosticId];
                editor.changeViewZones( (accessor) => accessor.removeZone(zoneId));
            }
            else if (cmds[1] === "add" && (cmds[3] === "Error" || cmds[3] === "Warning")
                && (cmds[4].endsWith("code1.csx") || cmds[4] == ""))
            {
                var offset : number = cmds[5] == "" ? -1 : Number(cmds[5]);
                var length : number = cmds[6] == "" ? -1 : Number(cmds[6]);
                var domNode = document.createElement('div');
                domNode.style.background = cmds[3] === "Error" ? "darkred" : "darkgreen";
                domNode.style.fontSize = "small";
                domNode.innerText = cmds[7];
                var zone : monaco.editor.IViewZone = {afterLineNumber:0, heightInLines:2, domNode:domNode};
                if (offset !== -1 && length !== -1)
                {
                    var startPos = editor.getModel().getPositionAt(offset);
                    var endPos = editor.getModel().getPositionAt(offset + (length == 0 ? length : length - 1));
                    zone.afterLineNumber = endPos.lineNumber;
                }
                editor.changeViewZones( (accessor) => diagnosticIdToZoneId[diagnosticId] = accessor.addZone(zone));
            }
        }
        else
        {
            log(resp);
        }
    }
}
