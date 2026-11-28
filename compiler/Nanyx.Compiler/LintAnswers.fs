/// What a linter written in Nanyx is told about the module it checks, and what it reports, as data (see `LintData`). A
/// question is a tag, the name of a member of `nanyx.analysis`'s `Analysis` context or `module`, with what it is asked
/// about as its payload; the answer is data of the type that member answers with. A linter's reports are a list of
/// `nanyx.analysis`'s `Report`s.
module NyxCompiler.LintAnswers

open Parser.Program
open NyxCompiler.LintData

/// `nanyx.analysis`'s `Ty`: what a linter is told about a type. An alias and a nominal type are named, not unfolded.
let rec tyData (ty: Ty) : LintData =
    let named name args isNominal = DTag("named", DRecord [ "name", DText name; "arguments", DList(List.map tyData args); "isNominal", DBool isNominal ])
    let fields (fields: Map<string, Ty>) = DList(fields |> Map.toList |> List.map (fun (name, fieldTy) -> tuple [ DText name; tyData fieldTy ]))
    match ty with
    | TyVar v -> DTag("variable", DText(v.Name |> Option.defaultValue $"t{v.Id}"))
    | TyPrimitive name -> DTag("primitive", DText name)
    | TyNominal(id, args, _, _) -> named id.Name args true
    | TyAlias(name, args, _) -> named name args false
    | TyFunc(input, output, contexts) -> DTag("function", DRecord [ "input", tyData input; "output", tyData output; "contexts", DList(List.map tyData contexts) ])
    | TyRecord(recordFields, _) -> DTag("record", fields recordFields)
    | TyOpenRecord(recordFields, _) -> DTag("openRecord", fields recordFields)
    | TyTag(name, payload) -> DTag("tag", DRecord [ "name", DText name; "payload", option "none" (Option.map tyData payload) ])
    | TyUnion items ->
        let rests, tags = items |> List.partition (function TyVar _ -> true | _ -> false)
        DTag("union", DRecord [ "tags", DList(List.map tyData tags); "isOpen", DBool(not rests.IsEmpty) ])
    | TyApply(name, args) -> DTag("applied", DRecord [ "name", DText name; "arguments", DList(List.map tyData args) ])
    | _ -> DTag("other", DText(Unifier.tyToString ty))

/// The type `data`, a `Ty` as `tyData` gives it, stands for, as near as it says: an alias stands for nothing but its
/// name, which is all printing it writes.
let rec private ofTyData (data: LintData) : Ty option =
    let all (items: LintData) =
        match items with
        | DList items ->
            let tys = items |> List.map ofTyData
            if tys |> List.forall Option.isSome then Some (tys |> List.choose id) else None
        | _ -> None
    let fieldsOf (items: LintData) =
        match items with
        | DList items ->
            let read =
                items
                |> List.map (fun item ->
                    match field "0" item, field "1" item |> Option.bind ofTyData with
                    | Some(DText name), Some fieldTy -> Some (name, fieldTy)
                    | _ -> None)
            if read |> List.forall Option.isSome then Some (read |> List.choose id |> Map.ofList) else None
        | _ -> None
    match data with
    | DTag("variable", DText name) -> Some (TyVar(TyVar.ordinary -1 (Some name)))
    | DTag("primitive", DText name) -> Some (TyPrimitive name)
    | DTag("named", named) ->
        match field "name" named, field "arguments" named |> Option.bind all, field "isNominal" named with
        | Some(DText name), Some args, Some(DBool true) -> Some (TyNominal({ Name = name; Home = InMemory }, args, TyUnion [], { IsPrivate = false; Variances = [] }))
        | Some(DText name), Some args, Some(DBool false) -> Some (TyAlias(name, args, TyUnion []))
        | _ -> None
    | DTag("function", value) ->
        match field "input" value |> Option.bind ofTyData, field "output" value |> Option.bind ofTyData, field "contexts" value |> Option.bind all with
        | Some input, Some output, Some contexts -> Some (TyFunc(input, output, contexts))
        | _ -> None
    | DTag("record", items) -> fieldsOf items |> Option.map (fun fields -> TyRecord(fields, FieldOrder.none))
    | DTag("openRecord", items) -> fieldsOf items |> Option.map (fun fields -> TyOpenRecord(fields, TyVar.ordinary -1 None))
    | DTag("tag", value) ->
        match field "name" value, field "payload" value with
        | Some(DText name), Some(DTag("some", payload)) -> ofTyData payload |> Option.map (fun payload -> TyTag(name, Some payload))
        | Some(DText name), Some(DTag("none", _)) -> Some (TyTag(name, None))
        | _ -> None
    | DTag("union", value) ->
        match field "tags" value |> Option.bind all, field "isOpen" value with
        | Some tags, Some(DBool isOpen) -> Some (TyUnion(if isOpen then tags @ [ TyVar(TyVar.ordinary -1 None) ] else tags))
        | _ -> None
    | DTag("applied", value) ->
        match field "name" value, field "arguments" value |> Option.bind all with
        | Some(DText name), Some args -> Some (TyApply(name, args))
        | _ -> None
    | _ -> None

/// The answers to a linter's questions about `model`, the module it checks (see the module), or why a question can't
/// be answered. What it asks about more than once is worked out once.
let answerer (model: SemanticModel) : LintData -> Result<LintData, string> =
    let span (at: SourceSpan) = LintSyntax.span at
    let symbolData (symbol: Symbol) =
        let kind =
            match symbol.Kind with
            | TopLevelValue -> "topLevel"
            | LocalValue -> "local"
            | Parameter -> "parameter"
            | PatternVariable -> "patternVariable"
            | ImportedModule -> "importedModule"
            | TypeDefinition -> "typeDefinition"
            | ContextMember -> "contextMember"
            | UnitOfMeasure -> "unitOfMeasure"
        // A span is an offset into the module's text, so a symbol declared in another file is declared nowhere here.
        let declaration = if SourceSpan.isNone symbol.Declaration || symbol.Declaration.File <> model.File then None else Some symbol.Declaration
        DRecord
            [ "id", DInt(int64 symbol.Id)
              "name", DText symbol.Name
              "kind", tag kind
              "declaration", option "none" (Option.map span declaration)
              "isExported", DBool symbol.IsExported ]
    let symbolAt (table: Map<NodeId, SymbolId>) (node: NodeId) =
        table |> Map.tryFind node |> Option.bind (fun id -> model.Symbols |> Map.tryFind id) |> Option.map symbolData |> option "none"
    let tyAt (table: Map<'k, Ty>) (key: 'k) = table |> Map.tryFind key |> Option.map tyData |> option "unknown"
    let references =
        lazy (model.References |> Map.toList |> List.groupBy snd |> List.map (fun (symbol, nodes) -> symbol, nodes |> List.map fst) |> Map.ofList)
    let matches =
        lazy
            (model.Syntax
             |> List.collect SyntaxTree.ofItem
             |> List.collect SyntaxTree.expressionsWithin
             |> List.choose (fun expr ->
                 match expr.Kind with
                 | Match(values, arms) -> Some (expr.Id, (values, arms))
                 | _ -> None)
             |> Map.ofList)
    let docComment (comment: DocComment) =
        let documents =
            match comment.Target with
            | DocumentsDeclaration at -> DTag("declaration", span at)
            | DocumentsTag at -> DTag("tag", span at)
            | DocumentsModule -> tag "module"
            | DocumentsNothing -> tag "nothing"
        DRecord [ "span", span comment.Span; "text", DText comment.Text; "documents", documents ]
    let ints (values: int64 seq) = DList(values |> Seq.map DInt |> List.ofSeq)
    fun question ->
        match question with
        | DTag("module", _) -> Ok (LintSyntax.ofModule model.Syntax)
        | DTag("source", _) -> Ok (DText model.Source)
        | DTag("typeOf", DInt node) -> Ok (tyAt model.Types node)
        | DTag("calleeTypeOf", DInt node) -> Ok (tyAt model.CalleeTypes node)
        | DTag("symbolOf", DInt node) -> Ok (symbolAt model.References node)
        | DTag("importOf", DInt node) -> Ok (symbolAt model.ImportReferences node)
        | DTag("symbol", DInt id) -> Ok (model.Symbols |> Map.tryFind (int id) |> Option.map symbolData |> option "none")
        | DTag("symbols", _) ->
            let own = model.Symbols |> Map.toList |> List.map snd |> List.filter (fun symbol -> symbol.Declaration.File = model.File && not (SourceSpan.isNone symbol.Declaration))
            Ok (DList(own |> List.sortBy (fun symbol -> symbol.Declaration.Start) |> List.map symbolData))
        | DTag("typeOfSymbol", DInt id) -> Ok (tyAt model.SymbolTypes (int id))
        | DTag("contextsOf", DInt id) ->
            let contexts =
                match model.ContextTypes |> Map.tryFind (int id) with
                | Some contexts -> contexts
                | None -> model.ImportedContextTypes |> Map.tryFind (int id) |> Option.map snd |> Option.defaultValue []
            Ok (DList(List.map tyData contexts))
        | DTag("referencesTo", DInt id) -> Ok (ints (references.Value |> Map.tryFind (int id) |> Option.defaultValue []))
        | DTag("lowered", DInt node) -> Ok (ints (model.Lowered |> Map.tryFind node |> Option.defaultValue []))
        | DTag("describe", ty) ->
            match ofTyData ty with
            | Some ty -> Ok (DText(Unifier.tyToString ty))
            | None -> Error "It asked to describe something that isn't a type"
        | DTag("coveringArms", arm) ->
            match field "0" arm, field "1" arm with
            | Some(DInt node), Some(DInt index) ->
                match matches.Value |> Map.tryFind node with
                | Some(values, arms) ->
                    Ok (ints (MatchCoverage.covering model.Types values arms (int index) |> Option.defaultValue [] |> List.map int64))
                | None -> Ok (DList [])
            | _ -> Error "It asked which arms cover an arm without saying of which match and which arm"
        | DTag("redundantTypeName", DInt node) -> Ok (DBool(model.RedundantTypeNames.Contains node))
        | DTag("docComments", _) -> Ok (DList(model.DocComments.Value |> List.map docComment))
        | DTag(name, _) -> Error $"It asked '{name}', which isn't a question a linter can ask"
        | _ -> Error "It asked something that isn't a question"

/// The reports `data`, a list of `nanyx.analysis`'s `Report`s, says a linter made about the module in `file`, or why it
/// isn't a list of reports.
let reports (file: string) (data: LintData) : Result<Report list, string> =
    let spanOf (data: LintData option) =
        match data |> Option.map (fun span -> field "start" span, field "end" span, field "line" span, field "column" span) with
        | Some(Some(DInt start), Some(DInt finish), Some(DInt line), Some(DInt column)) ->
            Some { File = file; Start = int start; End = int finish; StartLine = int line; StartColumn = int column }
        | _ -> None
    let text (data: LintData option) = match data with Some(DText value) -> Some value | _ -> None
    let edit (data: LintData) =
        match spanOf (field "span" data), text (field "newText" data) with
        | Some span, Some newText -> Some ({ Span = span; NewText = newText }: TextEdit)
        | _ -> None
    let fix (data: LintData) =
        match text (field "title" data), field "edits" data with
        | Some title, Some(DList edits) when edits |> List.forall (edit >> Option.isSome) ->
            Some ({ Title = title; Edits = edits |> List.choose edit }: Fix)
        | _ -> None
    let related (data: LintData) =
        match spanOf (field "0" data), text (field "1" data) with
        | Some span, Some what -> Some (span, what)
        | _ -> None
    let report (data: LintData) =
        match text (field "rule" data), spanOf (field "span" data), text (field "message" data), field "subject" data, field "related" data, field "fixes" data with
        | Some rule, Some span, Some message, Some subject, Some(DList relatedTo), Some(DList fixes) when
            (relatedTo |> List.forall (related >> Option.isSome)) && (fixes |> List.forall (fix >> Option.isSome)) ->
            let subject =
                match subject with
                | DTag("some", DText name) -> Some (Some name)
                | DTag("none", _) -> Some None
                | _ -> None
            subject
            |> Option.map (fun subject ->
                { Rule = rule
                  Span = span
                  Message = message
                  Subject = subject
                  Related = relatedTo |> List.choose related
                  Fixes = fixes |> List.choose fix })
        | _ -> None
    match data with
    | DList items ->
        let read = items |> List.map report
        match read |> List.tryFindIndex Option.isNone with
        | Some index -> Error $"Its report at position {index} isn't a `Report`"
        | None -> Ok (read |> List.choose id)
    | _ -> Error "What it gave back isn't a list of reports"

/// What a linter written in Nanyx reports about `model`, run by `run`: given the answers to its questions, in their
/// binary form, it gives its reports in theirs, or why it gave none. A question that can't be answered, or reports that
/// aren't `Report`s, fail it, as the linter host reports (see `Linters`).
let lintWith (run: (byte array -> Result<byte array, string>) -> Result<byte array, string>) (model: SemanticModel) : Report list =
    let answer = answerer model
    let answering (question: byte array) = LintData.ofBytes question |> Result.bind answer |> Result.map LintData.toBytes
    match run answering |> Result.bind LintData.ofBytes |> Result.bind (reports model.File) with
    | Ok reports -> reports
    | Error reason -> failwith reason
