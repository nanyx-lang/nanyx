/// The standard library as an editor's library browser lists it: each module with its doc comment, and what each
/// exports, with its kind, its signature as hover writes it, its doc comment and the import it needs. It is read from
/// the same compile of each module as the library's reference (see `StandardLibraryDocs`), made once per process.
module NyxCompiler.LibraryCatalog

open System
open System.Collections.Concurrent
open Parser.Program

/// What kind of name a module exports.
type ExportKind =
    /// A def whose type is a function's.
    | Function
    /// A def of any other type.
    | Value
    /// A type that is not a context.
    | Type
    | Context
    /// A def attached to the type `attachedTo` (`string.trim` to `string`).
    | Attached of attachedTo: string

/// A name a module exports.
type LibraryExport =
    { /// As source writes it: `string.trim`.
      Name: string
      Kind: ExportKind
      /// As hover writes the declaration: `def trim: string -> string`, or the type's definition.
      Signature: string
      /// The doc comment, as Markdown with its links written out.
      Doc: string option
      /// The import that brings the name in, `import nanyx/text`; `None` for the prelude's, which need none.
      Import: string option
      /// Where it is declared, in the library's file named by its import path (`nanyx/text.nyx`).
      Declaration: SourceSpan }

/// A module of the standard library: its import path, `nanyx/text`, and its doc comment.
type LibraryModule = { Name: string; Doc: string option }

/// The compile of each module, by its name, made the first time it is asked for.
let private compiled = ConcurrentDictionary<string, Lazy<CompileResult>>()

let private compileOf (moduleName: string) =
    compiled.GetOrAdd(moduleName, fun name -> lazy (StandardLibraryDocs.compile name)).Value

/// Every module's name, the prelude first.
let private moduleNames () = StandardLibrary.preludeModuleName :: StandardLibrary.moduleNames

/// The reader of the doc comments of the module `moduleName`, with each link's target as `linkTo` gives it.
let private readerOf (moduleName: string) (linkTo: Func<SourceSpan, string>) =
    let result = compileOf moduleName
    Documentation.Reader(StandardLibrary.resourceName moduleName, result.DocComments.Value, result, 0, linkTo)

/// Every module of the library, the prelude first, with its doc comment, whose links lead where `linkTo` says.
let modules (linkTo: Func<SourceSpan, string>) : LibraryModule list =
    moduleNames ()
    |> List.map (fun moduleName ->
        { Name = StandardLibrary.importPrefix + moduleName
          Doc = (readerOf moduleName linkTo).ForModule(StandardLibrary.resourceName moduleName) })

/// The kind of `symbol`, an export of the module compiled as `typed`.
let private kindOf (typed: TypedModule) (symbol: Symbol) =
    let at = symbol.Name.LastIndexOf '.'
    match Definition.typeDefinitionOf typed symbol with
    | Some info when symbol.Kind = TypeDefinition -> if info.IsContext then Context else Type
    | _ when at > 0 -> Attached(symbol.Name.Substring(0, at))
    | _ ->
        match Hover.declaredType typed symbol with
        | Some (ty, _) when (match Unifier.unalias ty with TyFunc _ -> true | _ -> false) -> Function
        | _ -> Value

/// What the module at `importPath` (`nanyx/text`) exports, in the order its files declare them, without the names it
/// reserves, with doc comments whose links lead where `linkTo` says. `None` when no module has that path.
let exports (linkTo: Func<SourceSpan, string>) (importPath: string) : LibraryExport list option =
    let moduleName =
        if importPath.StartsWith(StandardLibrary.importPrefix, StringComparison.Ordinal) then
            Some (importPath.Substring StandardLibrary.importPrefix.Length)
        else
            None
    match moduleName with
    | Some moduleName when List.contains moduleName (moduleNames ()) ->
        let typed = (compileOf moduleName).Typed.Value
        let reader = readerOf moduleName linkTo
        let byFile = StandardLibraryDocs.exportsOf moduleName typed reader.ForSymbol
        let import = if moduleName = StandardLibrary.preludeModuleName then None else Some $"import {importPath}"
        StandardLibrary.moduleFiles moduleName
        |> List.collect (fun file -> byFile |> Map.tryFind file |> Option.defaultValue [])
        |> List.map (fun export ->
            { Name = export.Symbol.Name
              Kind = kindOf typed export.Symbol
              Signature = export.Written
              Doc = export.Doc
              Import = import
              Declaration = export.Symbol.Declaration })
        |> Some
    | _ -> None
