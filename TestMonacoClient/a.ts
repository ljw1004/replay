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
var adornments: { [file:string]: { [tag:string]: monaco.editor.IContentWidget } } = { };


require.config({ paths: { 'vs': 'node_modules/monaco-editor/dev/vs'}});
require(['vs/editor/editor.main'], function() {
    editor = monaco.editor.create(document.getElementById('container'), {
        language:"csharp",
        roundedSelection:true,
        theme:"vs-dark",
    	scrollbar: {verticalScrollbarSize:0, handleMouseWheel:false}
    });
    connection = new WebSocket('ws://localhost:60828/ConsoleApp1');
    connection.onopen = function() {};
    connection.onmessage = function(ev:MessageEvent) { stdin.post(ev.data);}
    connection.onerror = function(ev:Event) {stdin.post(null); }
    connection.onclose = function() { stdin.post(null); }
    editor.getModel().onDidChangeContent(modelDidChangeContent);
    window.onresize = function() { editor.layout(); }
    layout();
    startDialog();
});


function layout():void
{
    var nlines = editor.getModel().getLineCount()+1;
    var lineheight = editor.getTopForLineNumber(2);
    document.getElementById("container").style.height = '' + (nlines*lineheight)+'px';
    editor.layout();
}

function modelDidChangeContent(e:monaco.editor.IModelContentChangedEvent2):void
{
    if (ignoreModelDidChangeContent) return;

    layout();
    var pos = new monaco.Position(e.range.startLineNumber, e.range.startColumn);
    var offset = editor.getModel().getOffsetAt(pos);
    var s = e.text.replace(/\\/g,"\\\\").replace(/\r/g,"\\r").replace(/\n/g,"\\n");
    connection.send(`CHANGE\tMain.csx\t${offset}\t${e.rangeLength}\t${s}`);
    //
    var file = "Main.csx";
    var line = e.range.startLineNumber;
    var count = e.range.endLineNumber - e.range.startLineNumber + 1;
    var newEndPos = editor.getModel().getPositionAt(offset + e.text.length);
    var newCount = newEndPos.lineNumber - e.range.startLineNumber + 1;
    //
    if (!(file in adornments)) adornments[file] = {}
    var db0 = adornments[file];
    var db : { [tag:string] : monaco.editor.IContentWidget } = {}
    Object.keys(db0).forEach(tag =>
    {
        var widget = db0[tag];
        var wpos = widget.getPosition();
        var wline = wpos.position.lineNumber;
        if (wline < line) db[tag] = widget;
        else if (line <= wline && wline < line + count)
        {
            editor.removeContentWidget(widget);
        }
        else
        {
            var wnode2 = widget.getDomNode();
            var wpos2 : monaco.editor.IContentWidgetPosition = { position:{lineNumber:wline+newCount-count, column:0}, preference: [monaco.editor.ContentWidgetPositionPreference.EXACT]};
            var wtag2 = tag;
            var widget2 : monaco.editor.IContentWidget = {getId:()=>wtag2, getDomNode:()=>wnode2, getPosition:()=>wpos2};
            db[wtag2] = widget2;
            editor.layoutContentWidget(widget2);
        }
    });
    adornments[file] = db;
}

function dumpAdornments():void
{
    log("ADORNMENTS");
    for (var file in adornments)
    {
        var db = adornments[file];
        for (var tag in db)
        {
            var widget = db[tag];
            var pos = widget.getPosition().position;
            var node = widget.getDomNode().innerText;
            log(`file=${file} line=${pos.lineNumber} tag=${tag} content=${node}`);
        }
    }
}

async function startDialog():Promise<void>
{
    var diagnosticIdToZoneId : number[] = [];

    var ok = await stdin.recv();
    if (ok != "OK") {log(`error: expected 'OK', got '${ok}'`); return;}
    connection.send("OK");
    connection.send("GET\tMain.csx");
    connection.send("WATCH\tMain.csx\t0\t50");
    while (true)
    {
        var cmd = await stdin.recv();
        var cmds = cmd.split('\t');

        if (cmds[0] === "GOT")
        {
            var fn = cmds[1];
            var txt = cmds[2]; 
            txt = txt.replace(/\\r/g,"\r").replace(/\\n/g,"\n").replace(/\\\\/g,"\\");
            ignoreModelDidChangeContent = true;
            editor.getModel().setValue(txt);
            ignoreModelDidChangeContent = false;
            layout();
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
                && (cmds[4].endsWith("Main.csx") || cmds[4] == ""))
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

        else if (cmds[0] === "ADORNMENT")
        {
            // "ADORNMENT add 12 9 t=2"
            // "ADORNMENT remove 12"
            var file = "Main.csx";
            if (!(file in adornments)) adornments[file] = {}
            if (cmds[1] === "add") AdornmentAdd(adornments[file], cmds[2], Number(cmds[3]), cmds[4]);
            else AdornmentRemove(adornments[file], cmds[2]);
        }

        else
        {
            log(cmd);
        }
    }
}

function AdornmentAdd(db:{ [tag:string]:monaco.editor.IContentWidget}, tag:string, line:number, content:string):void
{
    var pos : monaco.editor.IContentWidgetPosition = { position:{lineNumber:line+1, column:0}, preference: [monaco.editor.ContentWidgetPositionPreference.EXACT]};
    var node = document.createElement("div");
    node.innerHTML = '<span style="background:#333833; font-size:small; position:absolute; left:30em; width:10em;">// '+content+'</span>';
    var widget : monaco.editor.IContentWidget = {getId:()=>tag, getDomNode:()=>node, getPosition:()=>pos};
    editor.addContentWidget(widget);
    db[tag] = widget;                 
}

function AdornmentRemove(db:{ [tag:string]:monaco.editor.IContentWidget}, tag:string)
{
    editor.removeContentWidget(db[tag]);
    delete db[tag];
}
