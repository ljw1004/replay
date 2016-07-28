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

var stdin = new Channel<string>();
var connection : WebSocket;
var adornments: { [file:string]: { [tag:string]: monaco.editor.IContentWidget } } = { };
var diagnosticTagToZoneId : number[] = [];
var isRequirementMet : Promise<void>;
var currentFile : string;

require.config({ paths: { 'vs': 'node_modules/monaco-editor/dev/vs'}});
isRequirementMet = new Promise<void>(resolve => require(['vs/editor/editor.main'], resolve));

async function openDocument(project:string, file:string) : Promise<void>
{ 
    await isRequirementMet;
    currentFile = file;

    connection = new WebSocket(`ws://localhost:60828/${project}`);
    connection.onopen = function() {};
    connection.onmessage = function(ev:MessageEvent) { stdin.post(ev.data);}
    connection.onerror = function(ev:Event) {stdin.post(null); }
    connection.onclose = function() { stdin.post(null); }

    var cmd = await stdin.recv();
    if (cmd !== "OK") {log(`error: expected 'OK', got '${cmd}'`); return;}
    connection.send("OK");
    connection.send(`GET\tfile}`);
    cmd = await stdin.recv();
    var cmds = cmd.split('\t');
    if (cmds[0] !== "GOT") {log(`error: expected 'GOT contents', got '${cmd}'`); return;}
    var txt = cmds[2].replace(/\\r/g,"\r").replace(/\\n/g,"\n").replace(/\\\\/g,"\\");

    var docNode = document.getElementById("document");
    if (file.endsWith(".cs") || file.endsWith(".csx"))
    {
        var div = document.createElement("div");
        div.className = "container";
        docNode.appendChild(div);
        var editor = monaco.editor.create(div, {language:"csharp", roundedSelection:true, theme:"vs-dark", scrollbar: {verticalScrollbarSize:0, handleMouseWheel:false}});
        editor.getModel().setValue(txt);
        editor["startLine"] = 1;
        layout2(editor);
        editor.getModel().onDidChangeContent((e) => modelDidChangeContent(e,editor));
    }
    else if (file.endsWith(".md"))
    {
        var mdParse = new commonmark.Parser();
        var mdHtml = new commonmark.HtmlRenderer();
        var mdDocNode = mdParse.parse(txt);
        for (var node = mdDocNode.firstChild; node !== null; node = node.next)
        {
            if (node.type === "code_block")
            {
                var div = document.createElement("div");
                div.className = "container";
                docNode.appendChild(div);
                var editor = monaco.editor.create(div, {language:"csharp", roundedSelection:true, theme:"vs-dark", scrollbar: {verticalScrollbarSize:0, handleMouseWheel:false}});
                editor.getModel().setValue(node.literal);
                editor["startLine"] = node.sourcepos[0][0];
                layout2(editor);
                editor.getModel().onDidChangeContent((e) => modelDidChangeContent(e,editor));
            }
            else
            {
                docNode.insertAdjacentHTML("beforeend",mdHtml.render(node));
            }
        }
    }

    
    // window.onresize = () => { for (var item of editors) item.editor.layout(); };
    startDialog();
}



function layout2(editor:monaco.editor.IStandaloneCodeEditor):void
{
    var nlines = editor.getModel().getLineCount()+1;
    var lineheight = editor.getTopForLineNumber(2);
    if (lineheight == 0) lineheight = 19;
    editor.getDomNode().parentElement.style.height = '' + (nlines*lineheight)+'px';
    editor.layout();
}

function modelDidChangeContent(e:monaco.editor.IModelContentChangedEvent2, editor:monaco.editor.IStandaloneCodeEditor):void
{
    layout2(editor);
    var pos = new monaco.Position(e.range.startLineNumber, e.range.startColumn);
    var offset = editor.getModel().getOffsetAt(pos);
    var s = e.text.replace(/\\/g,"\\\\").replace(/\r/g,"\\r").replace(/\n/g,"\\n");
    connection.send(`CHANGE\t${currentFile}\t${offset}\t${e.rangeLength}\t${s}`);
    //
    var line = e.range.startLineNumber;
    var count = e.range.endLineNumber - e.range.startLineNumber + 1;
    var newEndPos = ed.editor.getModel().getPositionAt(offset + e.text.length);
    var newCount = newEndPos.lineNumber - e.range.startLineNumber + 1;
    //
    if (!(ed.file in adornments)) adornments[ed.file] = {}
    var db0 = adornments[ed.file];
    var db : { [tag:string] : monaco.editor.IContentWidget } = {}
    Object.keys(db0).forEach(tag =>
    {
        var widget = db0[tag];
        var wpos = widget.getPosition();
        var wline = wpos.position.lineNumber;
        if (wline < line) db[tag] = widget;
        else if (line <= wline && wline < line + count)
        {
            ed.editor.removeContentWidget(widget);
        }
        else
        {
            var wnode2 = widget.getDomNode();
            var wpos2 : monaco.editor.IContentWidgetPosition = { position:{lineNumber:wline+newCount-count, column:0}, preference: [monaco.editor.ContentWidgetPositionPreference.EXACT]};
            var wtag2 = tag;
            var widget2 : monaco.editor.IContentWidget = {getId:()=>wtag2, getDomNode:()=>wnode2, getPosition:()=>wpos2};
            db[wtag2] = widget2;
            ed.editor.layoutContentWidget(widget2);
        }
    });
    adornments[ed.file] = db;
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
    while (true)
    {
        var cmd = await stdin.recv();
        var cmds = cmd.split('\t');
        // DIAGNOSTIC remove 7 file.cs
        if (cmds[0] === "DIAGNOSTIC" && cmds[1] === "remove") diagnosticRemove(cmds[3], Number(cmds[2]));
        // DIAGNOSTIC add 7 file.cs Hidden 70 24 CS8019: Unnecessary using directive
        else if (cmds[0] === "DIAGNOSTIC" && cmds[1] === "add") diagnosticAdd(cmds[3], Number(cmds[2]), cmds[4], cmds[5] === "" ? -1 : Number(cmds[5]), cmds[6] === "" ? -1 : Number(cmds[6]), cmds[7]);
        // ADORNMENT remove 12 Main.csx
        else if (cmds[0] === "ADORNMENT" && cmds[1] === "remove") adornmentRemove(cmds[3], cmds[2]);
        // ADORNMENT add 12 Main.csx 9 t=2
        else if (cmds[0] === "ADORNMENT" && cmds[1] === "add") adornmentAdd(cmds[3], cmds[2], Number(cmds[4]), cmds[5]);
        else log(cmd);
    }
}


function diagnosticRemove(file:string, tag: number)
{
    var zoneId = diagnosticTagToZoneId[tag];
    var editor = findEditor(file).editor;
	editor.changeViewZones( (accessor) => accessor.removeZone(zoneId));
}

function diagnosticAdd(file:string, tag:number, severity:string, offset:number, length:number, content:string):void
{
    if (severity !== "Error" && severity !== "Warning") return;
    var editor = findEditor(file).editor;
    if (editor === null) return;
    var domNode = document.createElement('div');
    domNode.style.background = (severity === "Error" ? "darkred" : "darkgreen");
    domNode.style.fontSize = "small";
    domNode.innerText = content;
    var zone : monaco.editor.IViewZone = {afterLineNumber:0, heightInLines:2, domNode:domNode};
    if (offset !== -1 && length !== -1)
    {
        var startPos = editor.getModel().getPositionAt(offset);
        var endPos = editor.getModel().getPositionAt(offset + (length == 0 ? length : length - 1));
        zone.afterLineNumber = endPos.lineNumber;
    }
    editor.changeViewZones( (accessor) => diagnosticTagToZoneId[tag] = accessor.addZone(zone));
    
}


function adornmentAdd(file:string, tag:string, line:number, content:string):void
{
    var editor = findEditor(file).editor;
    if (editor === null) return;
    if (!(file in adornments)) adornments[file] = {};
    var pos : monaco.editor.IContentWidgetPosition = { position:{lineNumber:line+1, column:0}, preference: [monaco.editor.ContentWidgetPositionPreference.EXACT]};
    var node = document.createElement("div");
    node.innerHTML = '<span style="background:#333833; font-size:small; position:absolute; left:30em; width:20em; overflow:hidden;">// '+content+'</span>';
    var widget : monaco.editor.IContentWidget = {getId:()=>tag, getDomNode:()=>node, getPosition:()=>pos};
    editor.addContentWidget(widget);
    adornments[file][tag] = widget;                 
}

function adornmentRemove(file:string, tag:string)
{
    var editor = findEditor(file).editor;
    if (editor === null) return;
    var db = adornments[file];
    editor.removeContentWidget(db[tag]);
    delete db[tag];
}
