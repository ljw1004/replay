/// <reference path="node_modules/monaco-editor/monaco.d.ts"/>

function log(o:any):void
{
    let s = JSON.stringify(o);
    console.log(s);
    let e = document.getElementById("log");
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

let stdin = new Channel<string>();
let connection : WebSocket;
let editors : monaco.editor.IStandaloneCodeEditor[] = [];
let adornments : { [tag:string]: {editor:monaco.editor.IStandaloneCodeEditor, widget:monaco.editor.IContentWidget} } = { };
let isRequirementMet : Promise<void>;
let currentFile : string;

require.config({ paths: { 'vs': 'node_modules/monaco-editor/dev/vs'}});
isRequirementMet = new Promise<void>(resolve => require(['vs/editor/editor.main'], resolve));

async function openDocument(project:string, file:string) : Promise<void>
{ 
    await isRequirementMet;
    currentFile = file;

    connection = new WebSocket(`ws://localhost:5000/${project}`);
    connection.onopen = function() {};
    connection.onmessage = function(ev:MessageEvent) { stdin.post(ev.data);}
    connection.onerror = function(ev:Event) {stdin.post(null); }
    connection.onclose = function() { stdin.post(null); }

    let cmd = await stdin.recv();
    if (cmd !== "OK") {log(`error: expected 'OK', got '${cmd}'`); return;}
    connection.send("OK");
    connection.send(`GET\t${currentFile}`);
    cmd = await stdin.recv();
    let cmds = cmd.split('\t');
    if (cmds[0] !== "GOT") {log(`error: expected 'GOT contents', got '${cmd}'`); return;}
    let txt = cmds[2].replace(/\\r/g,"\r").replace(/\\n/g,"\n").replace(/\\\\/g,"\\");

    let docNode = document.getElementById("document");
    if (currentFile.endsWith(".cs") || currentFile.endsWith(".csx"))
    {
        let div = document.createElement("div");
        div.className = "container";
        docNode.appendChild(div);
        let editor = monaco.editor.create(div, {language:"csharp", roundedSelection:true, theme:"vs-dark"});
        editor.getModel().setValue(txt);
        editor["startLine"] = 1;
        layout2(editor);
        editors.push(editor);
        editor.getModel().onDidChangeContent((e) => modelDidChangeContent(e,editor));
    }
    else if (currentFile.endsWith(".md"))
    {
        docNode.style.overflowY = "scroll";
        let mdParse = new commonmark.Parser();
        let mdHtml = new commonmark.HtmlRenderer();
        let mdDocNode = mdParse.parse(txt);
        let codeLine = 0;
        for (let node = mdDocNode.firstChild; node !== null; node = node.next)
        {
            if (node.type === "code_block")
            {
                let code = node.literal.replace(/\n$/, "");
                let div = document.createElement("div");
                div.className = "container";
                docNode.appendChild(div);
                let editor = monaco.editor.create(div, {language:"csharp", roundedSelection:true, theme:"vs-dark", scrollbar: {verticalScrollbarSize:0, handleMouseWheel:false}, lineNumbers: (i) => i + editor["visualStartLine"]});
                editor.getModel().setValue(code);
                editor["visualStartLine"] = codeLine;
                editor["mdStartLine"] = node.sourcepos[0][0];
                codeLine += editor.getModel().getLineCount();
                layout2(editor);
                editors.push(editor);
                editor.getModel().onDidChangeContent((e) => modelDidChangeContent(e,editor));
            }
            else
            {
                let div = document.createElement("div");
                div.className = "markdown";
                div.innerHTML = mdHtml.render(node);
                docNode.appendChild(div);
            }
        }
    }
    else
    {
        log(`Unrecognized filetype - expected 'csx | cs | md', got '${currentFile}'`);
    }

    
    window.onresize = () => { for (let editor of editors) editor.layout(); };
    connection.send("WATCH\t*");
    startDialog();
}


function layout2(editor:monaco.editor.IStandaloneCodeEditor):void
{
    let nlines = editor.getModel().getLineCount()+1;
    let lineheight = editor.getTopForLineNumber(2);
    if (lineheight == 0) lineheight = 19;
    editor.getDomNode().parentElement.style.height = '' + (nlines*lineheight)+'px';
    editor.layout();
}

function findEditor(line:number):monaco.editor.IStandaloneCodeEditor
{
    let r : monaco.editor.IStandaloneCodeEditor = null;
    for (let editor of editors)
    {
        if (editor["mdStartLine"] > line) return r; else r = editor;
    }
    return r;
}

function modelDidChangeContent(e:monaco.editor.IModelContentChangedEvent2, editor:monaco.editor.IStandaloneCodeEditor):void
{
    // If you added a newline, we might have to recompute the height of this control
    layout2(editor);

    // Send a change notification to the server, using md-relative startLine (not editor-relative) 
    let startLine = e.range.startLineNumber + editor["mdStartLine"];
    let startColumn = e.range.startColumn;
    let startOffset = editor.getModel().getOffsetAt({lineNumber:e.range.startLineNumber, column:startColumn});
    let oldLength = e.rangeLength;
    let oldLineCount = e.range.endLineNumber - e.range.startLineNumber + 1;
    let newEndOffset = editor.getModel().getPositionAt(startOffset + e.text.length);
    let newLineCount = newEndOffset.lineNumber - e.range.startLineNumber + 1;
    let s = e.text.replace(/\\/g,"\\\\").replace(/\r/g,"\\r").replace(/\n/g,"\\n");
    connection.send(`CHANGE\t${currentFile}\t${startLine}\t${e.range.startColumn}\t${oldLineCount}\t${newLineCount}\t${oldLength}\t${s}`);

    // If the line count has changed, we have to adjust line numbers of all subsequent editors
    if (newLineCount !== oldLineCount)
    {
        for (let other of editors)
        {
            if (other["mdStartLine"] <= editor["mdStartLine"]) continue;
            other["mdStartLine"] += newLineCount - oldLineCount;
            other["visualStartLine"] += newLineCount - oldLineCount;
            other.updateOptions({lineNumbers: (i) => i + other["visualStartLine"]}); // to refresh line numbers
        }
    }

    // Remove any adornments in affected area, and shift down any adornments below the affected area
    let newAdornments : { [tag:string]: {editor:monaco.editor.IStandaloneCodeEditor, widget:monaco.editor.IContentWidget} } = { };
    Object.keys(adornments).forEach(tag =>
    {
        let editor = adornments[tag].editor;
        let widget = adornments[tag].widget;
        let wpos = widget.getPosition();
        let wline = wpos.position.lineNumber + editor["mdStartLine"];
        if (wline < startLine) newAdornments[tag] = {editor:editor, widget:widget};
        else if (startLine <= wline && wline < startLine + oldLineCount) editor.removeContentWidget(widget);
        else
        {
            let wnode2 = widget.getDomNode();
            let wpos2 : monaco.editor.IContentWidgetPosition = { position:{lineNumber:wpos.position.lineNumber+newLineCount-oldLineCount, column:0}, preference: [monaco.editor.ContentWidgetPositionPreference.EXACT]};
            let wtag2 = tag;
            let widget2 : monaco.editor.IContentWidget = {getId:()=>wtag2, getDomNode:()=>wnode2, getPosition:()=>wpos2};
            newAdornments[wtag2] = {editor:editor, widget:widget2};
            editor.layoutContentWidget(widget2);
        }
    });
    adornments = newAdornments;
}

function dump(o:any):void
{
    let div = document.createElement("div");
    for (let key in o)
    {
        if (key in div) continue;
        let val = o[key];
        if (val === null || typeof val === 'undefined') continue;
        console.log(`${key}: ${val.constructor.name}`);
    }
}

function dumpAdornments():void
{
    log("ADORNMENTS");
    for (let tag in adornments)
    {
        let editor = adornments[tag].editor;
        let widget = adornments[tag].widget;
        let lineRelativeToEditor = widget.getPosition().position.lineNumber;
        let lineRelativeToAllEditors = lineRelativeToEditor + editor["visualStartLine"];
        let lineRelativeToMd = lineRelativeToEditor + editor["mdStartLine"];
        let node = widget.getDomNode().innerText;
        log(`lineNumber=${lineRelativeToAllEditors} mdLine=${lineRelativeToMd} tag=${tag} content=${node}`);
    }
}

async function startDialog():Promise<void>
{
    while (true)
    {
        let cmd = await stdin.recv();
        log(cmd);
        let cmds = cmd.split('\t');
        // DIAGNOSTIC remove 7 file.cs
        if (cmds[0] === "DIAGNOSTIC" && cmds[1] === "remove") diagnosticRemove(cmds[3], cmds[2]);
        // DIAGNOSTIC add 7 file.cs Hidden startLine startCol length CS8019: Unnecessary using directive
        else if (cmds[0] === "DIAGNOSTIC" && cmds[1] === "add") diagnosticAdd(cmds[3], cmds[2], cmds[4], Number(cmds[5]), Number(cmds[6]), Number(cmds[7]), cmds[8]);
        // ADORNMENT remove 12 Main.csx
        else if (cmds[0] === "ADORNMENT" && cmds[1] === "remove") adornmentRemove(cmds[3], cmds[2]);
        // ADORNMENT add 12 Main.csx 9 t=2
        else if (cmds[0] === "ADORNMENT" && cmds[1] === "add") adornmentAdd(cmds[3], cmds[2], Number(cmds[4]), cmds[5]);
        else log(cmd);
    }
}


function diagnosticRemove(file:string, tag: string):void
{
    tag = "d"+tag;
    if (!(tag in adornments)) return;
    let d = adornments[tag];
    d.editor.removeContentWidget(d.widget);
    delete adornments[tag];
}

function diagnosticAdd(file:string, tag:string, severity:string, startLine:number, startCol:number, length:number, content:string):void
{
    tag = "d"+tag;
    if (severity !== "Error" && severity !== "Warning") return;
    let editor = findEditor(startLine);
    let editorStartLine = startLine - editor["mdStartLine"];
    
    let pos : monaco.editor.IContentWidgetPosition = { position:{lineNumber:editorStartLine, column:0}, preference: [monaco.editor.ContentWidgetPositionPreference.EXACT]};
    let node = document.createElement("div");
    node.innerHTML = '<span style="background:darkred; color:white; font-size:small; position:absolute; left:30em; width:30em; overflow:hidden;">// '+content+'</span>';
    let widget : monaco.editor.IContentWidget = {getId:()=>tag, getDomNode:()=>node, getPosition:()=>pos};
    editor.addContentWidget(widget);
    adornments[tag] = {editor:editor, widget:widget};                 
}


function adornmentAdd(file:string, tag:string, line:number, content:string):void
{
    tag = "a"+tag;
    let editor = findEditor(line);
    let editorLine = line - editor["mdStartLine"]

    let pos : monaco.editor.IContentWidgetPosition = { position:{lineNumber:editorLine, column:0}, preference: [monaco.editor.ContentWidgetPositionPreference.EXACT]};
    let node = document.createElement("div");
    node.innerHTML = '<span style="background:#333833; font-size:small; position:absolute; left:30em; width:20em; overflow:hidden;">// '+content+'</span>';
    let widget : monaco.editor.IContentWidget = {getId:()=>tag, getDomNode:()=>node, getPosition:()=>pos};
    editor.addContentWidget(widget);
    adornments[tag] = {editor:editor, widget:widget};                 
}

function adornmentRemove(file:string, tag:string)
{
    tag = "a"+tag;
    if (!(tag in adornments)) return;
    let a = adornments[tag];
    a.editor.removeContentWidget(a.widget);
    delete adornments[tag];
}
