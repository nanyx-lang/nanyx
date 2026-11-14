/// The standard library's reference, as the docs site shows it (docs/src/content/stdlib): a page for each file of the
/// library, and on it the names the file exports, in the order it declares them, each with its signature and doc
/// comment. It is read from the compile of each module that the compiler embeds. The reference sits on its page
/// between `beginMarker` and `endMarker`, and the rest of the page is written by hand.
module NyxCompiler.StandardLibraryDocs

open System
open System.IO
open System.Text.RegularExpressions
open Parser.Program

let beginMarker = "<!-- The reference below is generated from the doc comments in stdlib/ by `nanyx stdlib-docs`: edit those, not this. -->"

let endMarker = "<!-- End of the generated reference. -->"

/// The page of the library's file `file` (its resource name, as `nanyx/prelude-lists.nyx`): its name without the
/// package and the extension, with a `-` for each `.`, so `nanyx/console.host.nyx` is on `console-host`.
let pageOf (file: string) =
    Path.GetFileNameWithoutExtension(file.Substring StandardLibrary.importPrefix.Length).Replace('.', '-')

/// What a page's reference lists: an exported def, type or context, its doc comment, and, for a context, its
/// members, each with its doc comment.
type private Export =
    { Symbol: Symbol
      Written: string
      Doc: string option
      Members: (string * string option) list }

/// A name written as source writes it: an operator in backticks, after the type it is attached to if it is, as
/// `` `==` `` or `` int.`+` ``.
let private writtenName (name: string) =
    let at = name.LastIndexOf '.'
    let qualifier, own = if at > 0 then name.Substring(0, at + 1), name.Substring(at + 1) else "", name
    if Regex.IsMatch(own, @"^[A-Za-z_][A-Za-z0-9_]*$") then name else $"{qualifier}`{own}`"

/// `text` as inline code: in double backticks, with a space inside each, if it has a backtick of its own.
let private code (text: string) =
    if text.Contains '`' then $"`` {text} ``" else $"`{text}`"

/// The id the docs site gives a heading of `text`: lower case, without anything but letters, digits, `_`, `-` and
/// spaces, and with a `-` for each space, as GitHub's slugger makes it.
let private slug (text: string) =
    Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{M}\p{N}_\- ]", "").Replace(' ', '-')

/// The ids of the headings of `exports`, in order: a heading's slug, with `-1`, `-2`, ... after it when an earlier one
/// has the same, as the site numbers them.
let private anchors (exports: Export list) : (Export * string) list =
    exports
    |> List.mapFold (fun (seen: Map<string, int>) export ->
        let id = slug export.Symbol.Name
        match seen |> Map.tryFind id with
        | Some count -> (export, $"{id}-{count}"), seen |> Map.add id (count + 1)
        | None -> (export, id), seen |> Map.add id 1) Map.empty
    |> fst

/// A def's signature, as hover writes its declaration: `def name: type`, after the contexts it requires.
let private defSignature (typed: TypedModule) (symbol: Symbol) =
    match Hover.declaredType typed symbol with
    | Some (ty, contexts) ->
        let contexts = contexts |> FunctionContexts.flatten |> Unifier.writtenContexts
        $"def {writtenName symbol.Name}: {Signatures.typeWithContexts Source ty contexts}"
    | None -> $"def {writtenName symbol.Name}"

/// A type's definition, as hover writes it, but for a context's members: one to a line, in the order it declares them.
let private typeSignature (typed: TypedModule) (symbol: Symbol) (info: TypeDefInfo) (members: Symbol list) =
    let writeShape (shape: Ty) =
        match shape with
        | TyRecord fields when info.IsContext && not members.IsEmpty ->
            let written = members |> List.choose (fun memberSymbol -> fields |> Map.tryFind memberSymbol.Name |> Option.map (fun ty -> memberSymbol.Name, ty))
            let types = Unifier.tysToDisplayStrings Source (List.map snd written)
            let lines = List.map2 (fun (name, _) ty -> $"  {writtenName name}: {ty}") written types
            "(\n" + String.concat "\n" lines + "\n)"
        | _ -> Unifier.tyToDisplayString Source shape
    fst (Definition.writtenTypeDefinition writeShape symbol info true)

/// The compile of the standard-library module `moduleName`, as an import of it checks it.
let private compile (moduleName: string) : CompileResult =
    Compiler.compileFrom Linters.none (StandardLibraryModule moduleName) (StandardLibrary.tryLoadSource moduleName).Value

/// The names the module `moduleName`, compiled as `typed`, exports, by the file of it that declares each, in the order
/// the file declares them, with their doc comments as `docOf` writes them. The names it reserves are left out.
let private exportsOf (moduleName: string) (typed: TypedModule) (docOf: Symbol -> string option) : Map<string, Export list> =
    let files = StandardLibrary.moduleFiles moduleName |> Set.ofList
    typed.Symbols
    |> Map.values
    |> Seq.filter (fun symbol ->
        symbol.IsExported
        && (symbol.Kind = TopLevelValue || symbol.Kind = TypeDefinition)
        && files.Contains symbol.Declaration.File
        && not (ReservedNames.isHidden symbol))
    |> Seq.sortBy (fun symbol -> symbol.Declaration.File, symbol.Declaration.Start)
    |> Seq.map (fun symbol ->
        let export =
            match Definition.typeDefinitionOf typed symbol with
            | Some info when symbol.Kind = TypeDefinition ->
                let members =
                    info.Members
                    |> Map.values
                    |> Seq.choose typed.Symbols.TryFind
                    |> Seq.sortBy _.Declaration.Start
                    |> List.ofSeq
                { Symbol = symbol
                  Written = typeSignature typed symbol info members
                  Doc = docOf symbol
                  Members = members |> List.map (fun memberSymbol -> memberSymbol.Name, docOf memberSymbol) }
            | _ -> { Symbol = symbol; Written = defSignature typed symbol; Doc = docOf symbol; Members = [] }
        symbol.Declaration.File, export)
    |> Seq.groupBy fst
    |> Seq.map (fun (file, exports) -> file, exports |> Seq.map snd |> List.ofSeq)
    |> Map.ofSeq

/// `text` with each line but the first indented by `indent` spaces, as a list item's continuation.
let private indented (indent: int) (text: string) =
    text.Split('\n') |> Array.mapi (fun i line -> if i = 0 || line = "" then line else String(' ', indent) + line) |> String.concat "\n"

/// The reference of one page: the module's doc comment, `moduleDoc`, if this is the file named for the module, then
/// each export with its heading (`anchors`).
let private render (moduleDoc: string option) (exports: (Export * string) list) =
    let entry (export: Export, _) =
        let members =
            export.Members
            |> List.choose (fun (name, doc) -> doc |> Option.map (fun doc -> $"- {code (writtenName name)}: {indented 2 doc}"))
        [ $"## {code (writtenName export.Symbol.Name)}"
          $"```nanyx fragment\n{export.Written}\n```"
          yield! Option.toList export.Doc
          if not members.IsEmpty then String.concat "\n" members ]
    Option.toList moduleDoc @ (exports |> List.collect entry) |> String.concat "\n\n"

/// The reference of each page, by page: one for each file of the library that exports a name, as the compile of its
/// module that the compiler embeds has them. A link in a doc comment, `` [`name`] ``,
/// leads to the name's entry, on its own page or another.
let references () : Map<string, string> =
    let modules = StandardLibrary.preludeModuleName :: StandardLibrary.moduleNames
    let compiled = modules |> List.map (fun moduleName -> moduleName, compile moduleName)
    // Every entry's page and heading, by where its name is declared, for the links.
    let targets =
        compiled
        |> List.collect (fun (moduleName, result) ->
            exportsOf moduleName result.Typed.Value (fun _ -> None)
            |> Map.toList
            |> List.collect (fun (file, exports) ->
                anchors exports |> List.map (fun (export, id) -> export.Symbol.Declaration, (pageOf file, id))))
        |> Map.ofList
    compiled
    |> List.collect (fun (moduleName, result) ->
        StandardLibrary.moduleFiles moduleName
        |> List.choose (fun file ->
            let page = pageOf file
            let linkTo =
                Func<SourceSpan, string>(fun span ->
                    match targets |> Map.tryFind span with
                    | Some (target, id) when target = page -> $"#{id}"
                    | Some (target, id) -> $"{target}#{id}"
                    | None -> null)
            let reader = Documentation.Reader(StandardLibrary.resourceName moduleName, result.DocComments.Value, result, 0, linkTo)
            let exports =
                exportsOf moduleName result.Typed.Value reader.ForSymbol
                |> Map.tryFind file
                |> Option.map anchors
                |> Option.defaultValue []
            let moduleDoc = if file = StandardLibrary.resourceName moduleName then reader.ForModule file else None
            if exports.IsEmpty then None else Some (page, render moduleDoc exports)))
    |> Map.ofList

/// `page`, a page's text, with its reference between the markers replaced by `reference`. An error when it has no
/// markers, or they are out of order.
let withReference (reference: string) (page: string) : Result<string, string> =
    let page = page.ReplaceLineEndings "\n"
    match page.IndexOf beginMarker, page.IndexOf endMarker with
    | start, finish when start >= 0 && finish > start ->
        Ok (page.Substring(0, start + beginMarker.Length) + "\n\n" + reference + "\n\n" + page.Substring finish)
    | _ -> Error $"has no reference: it needs a line {beginMarker} and, after it, a line {endMarker}"

/// What `update` finds for one page.
type PageUpdate =
    /// The page's reference is the library's.
    | UpToDate
    /// The page's text with its reference as the library has it.
    | OutOfDate of string
    /// The page cannot be written: there is no page for a file that exports names, or the page has no markers, or it
    /// has markers but no file of the library is its.
    | Unwritable of string

/// What each page of the library's reference in `directory` (docs/src/content/stdlib) needs, by its path: its text with
/// the library's reference, if that is not what it has.
let update (directory: string) : (string * PageUpdate) list =
    let references = references ()
    let pages =
        Directory.GetFiles(directory, "*.md")
        |> Array.sort
        |> Array.map (fun path -> Path.GetFileNameWithoutExtension path, path)
        |> Map.ofArray
    let ofLibrary =
        references
        |> Map.toList
        |> List.map (fun (page, reference) ->
            match pages |> Map.tryFind page with
            | None -> Path.Combine(directory, page + ".md"), Unwritable "is missing: a file of the standard library exports names it would list"
            | Some path ->
                let text = File.ReadAllText path
                match withReference reference text with
                | Error message -> path, Unwritable message
                | Ok written when written = text.ReplaceLineEndings "\n" -> path, UpToDate
                | Ok written -> path, OutOfDate written)
    let stale =
        pages
        |> Map.toList
        |> List.filter (fun (page, path) -> not (references.ContainsKey page) && File.ReadAllText(path).Contains beginMarker)
        |> List.map (fun (_, path) -> path, Unwritable "has a reference, but no file of the standard library is its")
    ofLibrary @ stale
