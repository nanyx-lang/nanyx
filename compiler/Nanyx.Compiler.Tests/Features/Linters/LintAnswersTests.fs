module Nanyx.Compiler.Tests.Features.Linters.LintAnswers

open Xunit
open NyxCompiler
open NyxCompiler.LintData

// What a linter written in Nanyx is told about a module, and how its reports are read, as data (see `LintAnswers`).

/// Runs a linter whose `lint` is `probe`, given the answers to the questions it asks about `source`, and gives what
/// `probe` found out, if it finished, with the compile's diagnostics.
let private probing (source: string) (probe: (LintData -> LintData) -> 'a * Report list) : 'a option * Diagnostic list =
    let mutable found = None
    let linter =
        { Name = "probe"
          Package = "probe.lint"
          Rules = [ { Id = "found"; Title = "Found"; DefaultSeverity = Severity.Warning; Unnecessary = false; Description = "" } ]
          Lint =
            fun model ->
                let answer = LintAnswers.answerer model
                let result, reports = probe (fun question -> match answer question with Ok data -> data | Error message -> failwith message)
                found <- Some result
                reports }
    let result = Compiler.compileWith { Linters = [ linter ]; Severities = Map.empty } (source.ReplaceLineEndings "\n")
    found, result.Diagnostics

/// What `probe` found out about `source` (see `probing`), which it must finish.
let private asking (source: string) (probe: (LintData -> LintData) -> 'a * Report list) : 'a * Diagnostic list =
    let found, diagnostics = probing source probe
    found.Value, diagnostics

let private ask name payload = DTag(name, payload)

/// The `index`th item of the list `data`.
let private item (index: int) (data: LintData) =
    match data with
    | DList items -> items.[index]
    | other -> failwith $"%A{other} is not a list"

/// The field `name` of the record `data`, which must have it.
let private get (name: string) (data: LintData) = (field name data).Value

/// The payload of the tag `data`, which must be `name`.
let private payloadOf (name: string) (data: LintData) =
    match data with
    | DTag(tag, payload) when tag = name -> payload
    | other -> failwith $"%A{other} is not #{name}"

let private idOf (node: LintData) =
    match get "id" node with
    | DInt id -> id
    | other -> failwith $"%A{other} is no id"

[<Fact>]
let ``Data reads back from its binary form as it was written`` () =
    let data =
        DRecord
            [ "id", DInt -42L
              "ratio", DFloat 0.1
              "name", DText "naïve ✓"
              "flags", DList [ DBool true; DBool false; DUnit ]
              "kind", DTag("call", tuple [ DText "f"; DTag("none", DUnit) ]) ]
    Assert.Equal(Ok data, LintData.ofBytes (LintData.toBytes data))
    Assert.Equal(Error "The value ends early", LintData.ofBytes (Array.truncate 5 (LintData.toBytes data)))

[<Fact>]
let ``The tree names each node by nanyx.analysis's syntax fields and tags`` () =
    let tree, _ =
        asking "def total = add(1, 2)\n\ndef add = { a: int, b: int -> a + b }\n" (fun answer -> answer (ask "module" DUnit), [])
    let total = item 0 tree
    let def = get "kind" total |> payloadOf "def"
    Assert.Equal(DText "total", get "name" def)
    Assert.Equal(DBool false, get "isExport" def)
    let call = get "value" def |> get "kind" |> payloadOf "call"
    Assert.Equal(DText "add", get "name" call)
    // The parser reads a call's arguments as one tuple.
    let arguments = get "arguments" call |> item 0 |> get "kind" |> payloadOf "tuple"
    Assert.Equal(DTag("literal", DTag("int", DInt 1L)), arguments |> item 0 |> get "kind")
    Assert.Equal(DRecord [ "start", DInt 0L; "end", DInt 21L; "line", DInt 0L; "column", DInt 0L ], get "span" total)
    let lambda = item 1 tree |> get "kind" |> payloadOf "def" |> get "value" |> get "kind" |> payloadOf "lambda"
    let parameter = get "parameters" lambda |> item 0
    Assert.Equal(DText "a", get "0" parameter)
    Assert.Equal(DTag("name", DText "int"), get "1" parameter |> payloadOf "some" |> get "kind")

[<Fact>]
let ``A node's type and symbol, and the nodes that refer to a symbol, are answered by its id`` () =
    let (typeOfCall, symbol, references, described), _ =
        asking "def one = 1\n\ndef two = one + one\n" (fun answer ->
            let tree = answer (ask "module" DUnit)
            let sum = item 1 tree |> get "kind" |> payloadOf "def" |> get "value"
            let left = get "kind" sum |> payloadOf "binary" |> get "left"
            let symbol = answer (ask "symbolOf" (DInt(idOf left))) |> payloadOf "some"
            let typeOfSum = answer (ask "typeOf" (DInt(idOf sum)))
            let references = answer (ask "referencesTo" (get "id" symbol))
            (typeOfSum, symbol, references, answer (ask "describe" (payloadOf "some" typeOfSum))), [])
    Assert.Equal(DTag("some", DTag("primitive", DText "int")), typeOfCall)
    Assert.Equal(DText "one", get "name" symbol)
    Assert.Equal(DTag("topLevel", DUnit), get "kind" symbol)
    Assert.Equal(DTag("some", DRecord [ "start", DInt 4L; "end", DInt 7L; "line", DInt 0L; "column", DInt 4L ]), get "declaration" symbol)
    match references with
    | DList nodes -> Assert.Equal(2, nodes.Length)
    | other -> failwith $"%A{other}"
    Assert.Equal(DText "int", described)

[<Fact>]
let ``The arms that cover an arm are answered from the pattern space`` () =
    let covering, _ =
        asking "def name: int -> string = { n ->\n  match n\n    | _ -> \"any\"\n    | 0 -> \"zero\"\n}\n" (fun answer ->
            let tree = answer (ask "module" DUnit)
            let lambda = item 0 tree |> get "kind" |> payloadOf "def" |> get "value" |> get "kind" |> payloadOf "lambda"
            // The lambda's body is a block of one statement, the match.
            let matchNode = get "body" lambda |> get "kind" |> payloadOf "block" |> item 0 |> get "kind" |> payloadOf "expr"
            [ 0L; 1L ] |> List.map (fun index -> answer (ask "coveringArms" (tuple [ DInt(idOf matchNode); DInt index ]))), [])
    Assert.Equal<LintData list>([ DList []; DList [ DInt 0L ] ], covering)

[<Fact>]
let ``A question no member of Analysis asks is the linter failing`` () =
    let found, diagnostics = probing "def one = 1\n" (fun answer -> answer (ask "whatever" DUnit), [])
    Assert.True(found.IsNone)
    let failed = Assert.Single(diagnostics)
    Assert.Equal("probe/linterFailed", failed.Code)
    Assert.Contains("'whatever'", failed.Message)

[<Fact>]
let ``Reports given back as data become the linter's diagnostics`` () =
    let reportData =
        DList
            [ DRecord
                  [ "rule", DText "found"
                    "span", DRecord [ "start", DInt 4L; "end", DInt 7L; "line", DInt 0L; "column", DInt 4L ]
                    "message", DText "A def named one"
                    "subject", DTag("some", DText "one")
                    "related", DList []
                    "fixes",
                    DList
                        [ DRecord
                              [ "title", DText "Rename it"
                                "edits", DList [ DRecord [ "span", DRecord [ "start", DInt 4L; "end", DInt 7L; "line", DInt 0L; "column", DInt 4L ]; "newText", DText "uno" ] ] ] ] ] ]
    let _, diagnostics =
        asking "def one = 1\n" (fun _ ->
            match LintAnswers.reports "" reportData with
            | Ok reports -> (), reports
            | Error message -> failwith message)
    let found = Assert.Single(diagnostics)
    Assert.Equal("probe/found", found.Code)
    Assert.Equal("A def named one", found.Message)
    Assert.Equal(Some "one", found.Subject)
    let fix = Assert.Single(found.Fixes)
    Assert.Equal("uno", (Assert.Single(fix.Edits)).NewText)
    Assert.Equal(Error "Its report at position 0 isn't a `Report`", LintAnswers.reports "" (DList [ DRecord [ "rule", DText "found" ] ]))

[<Fact>]
let ``The other questions about nodes are answered from the module's side tables`` () =
    let source = "import nanyx/math as m\n\n--| The answer.\ndef answer = 42\n\ndef root = m.sqrt(16.0)\n\ndef names = [\"a\"] \\list.map { s -> s }\n"
    let answers, _ =
        asking source (fun answer ->
            let tree = answer (ask "module" DUnit)
            let valueOf index = item index tree |> get "kind" |> payloadOf "def" |> get "value"
            let root = valueOf 2
            let pipe = valueOf 3
            [ "source", answer (ask "source" DUnit)
              "calleeTypeOf", answer (ask "calleeTypeOf" (DInt(idOf root)))
              "symbolOf", answer (ask "symbolOf" (DInt(idOf root)))
              "importOf", answer (ask "importOf" (DInt(idOf root)))
              "redundantTypeName", answer (ask "redundantTypeName" (DInt(idOf pipe)))
              "lowered", answer (ask "lowered" (DInt(idOf root)))
              "docComments", answer (ask "docComments" DUnit) ]
            |> Map.ofList, [])
    Assert.Equal(DText source, answers.["source"])
    match answers.["calleeTypeOf"] with
    | DTag("some", DTag("function", callee)) -> Assert.Equal(DTag("primitive", DText "float"), get "output" callee)
    | other -> failwith $"%A{other}"
    let sqrt = answers.["symbolOf"] |> payloadOf "some"
    Assert.Equal(DText "sqrt", get "name" sqrt)
    // Declared in another module, so nowhere in this one.
    Assert.Equal(DTag("none", DUnit), get "declaration" sqrt)
    Assert.Equal(DText "m", answers.["importOf"] |> payloadOf "some" |> get "name")
    Assert.Equal(DBool true, answers.["redundantTypeName"])
    Assert.Equal(DList [], answers.["lowered"])
    let comment = answers.["docComments"] |> item 0
    Assert.Equal(DText "The answer.", get "text" comment)
    Assert.Equal("declaration", (match get "documents" comment with DTag(name, _) -> name | other -> $"%A{other}"))

[<Fact>]
let ``A symbol, its type and the contexts it requires are answered by its id`` () =
    let answers, _ =
        asking "context Log = (log: string -> ())\n\ndef greet: [Log] string -> () = { name -> log(name) }\n" (fun answer ->
            let tree = answer (ask "module" DUnit)
            let call = item 1 tree |> get "kind" |> payloadOf "def" |> get "value" |> get "kind" |> payloadOf "lambda" |> get "body"
            let log = answer (ask "symbolOf" (DInt(idOf call))) |> payloadOf "some"
            let greet =
                match answer (ask "symbols" DUnit) with
                | DList symbols -> symbols |> List.find (fun symbol -> get "name" symbol = DText "greet")
                | other -> failwith $"%A{other}"
            Assert.Equal(DTag("some", greet), answer (ask "symbol" (get "id" greet)))
            [ "log", log
              "typeOfSymbol", answer (ask "typeOfSymbol" (get "id" greet))
              "contextsOf", answer (ask "contextsOf" (get "id" greet)) ]
            |> Map.ofList, [])
    Assert.Equal(DTag("contextMember", DUnit), get "kind" answers.["log"])
    match answers.["typeOfSymbol"] with
    | DTag("some", DTag("function", greet)) -> Assert.Equal(DTag("primitive", DText "unit"), get "output" greet)
    | other -> failwith $"%A{other}"
    match answers.["contextsOf"] with
    | DList [ context ] -> Assert.Contains("Log", $"%A{context}")
    | other -> failwith $"%A{other}"
[<Fact>]
let ``A def from another module is asked about by its symbol, as one of the module's own is`` () =
    // `println` comes from the prelude, whose symbols' ids don't fit 32 bits.
    let (symbol, contexts), _ =
        asking "def greet: [Console] () -> () = { () -> println(\"hi\") }\n" (fun answer ->
            let calls =
                answer (ask "module" DUnit)
                |> item 0
                |> get "kind"
                |> payloadOf "def"
                |> get "value"
                |> get "kind"
                |> payloadOf "lambda"
                |> get "body"
            let symbol = answer (ask "symbolOf" (DInt(idOf calls))) |> payloadOf "some"
            (symbol, answer (ask "contextsOf" (get "id" symbol))), [])
    Assert.Equal(DText "println", get "name" symbol)
    match contexts with
    | DList items -> Assert.Contains(Some(DText "Console"), items |> List.map (payloadOf "named" >> field "name"))
    | other -> failwith $"%A{other} isn't println's contexts"
