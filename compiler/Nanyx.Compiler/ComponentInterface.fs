namespace NyxCompiler

open System
open System.Text
open Parser.Program

/// A WIT type that a value crosses a component's boundary as (see `ComponentInterface`).
[<RequireQualifiedAccess>]
type WitType =
    | Bool
    | S8
    | S16
    | S32
    | S64
    | U8
    | U16
    | U32
    | U64
    | F32
    | F64
    | String

/// A function a component exports (see `ComponentExport`): its parameters, by their WIT names, and its result, none for
/// a function that returns `()`.
type ComponentFunction =
    { Export: ComponentExport
      Parameters: (string * WitType) list
      Result: WitType option }

/// The WIT interface a program gives a component built from it: the defs marked `@component`, each exported in the
/// interface its module names, with the types of their parameters and results written as WIT types (see the
/// Components page of the Deep dives).
module ComponentInterface =
    /// WIT's keywords, which a name written as one of them is escaped from with `%`.
    let private keywords =
        set
            [ "as"; "async"; "bool"; "borrow"; "char"; "constructor"; "enum"; "error-context"; "export"; "f32"; "f64"
              "flags"; "from"; "func"; "future"; "import"; "include"; "interface"; "list"; "map"; "option"; "own"
              "package"; "record"; "resource"; "result"; "s16"; "s32"; "s64"; "s8"; "static"; "stream"; "string"
              "tuple"; "type"; "u16"; "u32"; "u64"; "u8"; "use"; "variant"; "with"; "world" ]

    /// The words of `name`, in lower case: a capital starts one, unless it follows another and isn't followed by a
    /// lower-case letter (`toHTTPServer` is `to`, `http`, `server`), and a `.`, `_` or `-` ends one. Anything else that
    /// isn't a letter or a digit is left out, and a word that would start with a digit joins the one before it.
    let private words (name: string) =
        let words = ResizeArray<string>()
        let current = StringBuilder()
        let flush () =
            if current.Length > 0 then
                if Char.IsDigit current.[0] && words.Count > 0 then words.[words.Count - 1] <- words.[words.Count - 1] + current.ToString()
                else words.Add(current.ToString())
                current.Clear() |> ignore
        name
        |> Seq.iteri (fun index c ->
            if c = '.' || c = '_' || c = '-' then flush ()
            elif Char.IsUpper c then
                let afterCapital = index > 0 && Char.IsUpper name.[index - 1]
                let beforeLower = index + 1 < name.Length && Char.IsLower name.[index + 1]
                if not afterCapital || beforeLower then flush ()
                current.Append(Char.ToLowerInvariant c) |> ignore
            elif Char.IsLetterOrDigit c && c < '\u0080' then current.Append c |> ignore)
        flush ()
        List.ofSeq words

    /// `name` as a WIT name, in kebab case: `areaOf` is `area-of`, the module `geo.shapes` is `geo-shapes`, and a name
    /// that is one of WIT's keywords is escaped, `%type`.
    let witName (name: string) =
        let kebab = words name |> String.concat "-"
        if keywords.Contains kebab then "%" + kebab else kebab

    /// The WIT package a package named `name` at `version` gives its interfaces in: its name's first segment is the
    /// namespace and the rest the package's name, so `acme.shapes` 0.1.0 is `acme:shapes@0.1.0`, and a name of one
    /// segment is both, `greeter:greeter@0.1.0`.
    let packageName (name: string) (version: string) =
        let ``namespace``, rest =
            match name.IndexOf '.' with
            | dot when dot > 0 -> name.Substring(0, dot), name.Substring(dot + 1)
            | _ -> name, name
        $"{witName ``namespace``}:{witName rest}@{version}"

    /// The name the WIT package `package` (`acme:shapes@0.1.0`) gives an item of its own, `name`:
    /// `acme:shapes/name@0.1.0`.
    let qualified (package: string) (name: string) =
        match package.IndexOf '@' with
        | at when at > 0 -> $"{package.Substring(0, at)}/{name}{package.Substring at}"
        | _ -> $"{package}/{name}"

    /// The name a core module exports `export` under in the package `package`: its interface's name, then `#` and the
    /// function's, as wit-component expects.
    let exportName (package: string) (export: ComponentExport) =
        $"{qualified package (witName export.Module)}#{witName export.Name}"

    /// Why a type can't cross a component's boundary: it never can (`Never`), or the compiler can't take it across yet
    /// (`NotYet`), each with what it is, as a message says it.
    type private Problem =
        | Never of what: string * fix: string
        | NotYet of what: string

    /// The WIT type of a value of type `ty`, or why it has none. `named` says whether `ty` is what a named type stands
    /// for, which a record or a union must be: WIT names each.
    let rec private witTypeOf (named: bool) (ty: Ty) : Result<WitType, Problem> =
        let rec hasVariable (ty: Ty) =
            match ty with
            | TyVar _
            | TyOpenRecord _ -> true
            | ty -> Ty.children ty |> List.exists hasVariable
        match ty with
        | ty when hasVariable ty -> Error (Never("a type variable", "write the type it stands for"))
        | TyAlias(_, _, underlying)
        | TyNominal(_, _, underlying, _) -> witTypeOf true underlying
        | TyPrimitive "bool" -> Ok WitType.Bool
        | TyPrimitive "int" -> Ok WitType.S64
        | TyPrimitive "float" -> Ok WitType.F64
        | TyPrimitive "F32" -> Ok WitType.F32
        | TyPrimitive "string" -> Ok WitType.String
        | SizedIntegerType integer ->
            Ok (
                match integer with
                | SizedInteger.I8 -> WitType.S8
                | SizedInteger.I16 -> WitType.S16
                | SizedInteger.I32 -> WitType.S32
                | SizedInteger.U8 -> WitType.U8
                | SizedInteger.U16 -> WitType.U16
                | SizedInteger.U32 -> WitType.U32
                | SizedInteger.U64 -> WitType.U64)
        | TyFunc _ -> Error (Never("a function", "pass the values it would compute instead"))
        | TyContexts _ -> Error (Never("a context", "pass the values it would give instead"))
        | TyRecord(fields, _) when not named && not (fields |> Map.forall (fun key _ -> fst (Int32.TryParse key))) ->
            Error (Never("a record with no name", "name it with a `type`, which names it in the WIT"))
        | TyTag _
        | TyUnion _ when not named -> Error (Never("a tag union with no name", "name it with a `type`, which names it in the WIT"))
        | TyRecord(fields, _) when fields |> Map.forall (fun key _ -> fst (Int32.TryParse key)) -> Error (NotYet "a tuple")
        | TyRecord _ -> Error (NotYet "a record")
        | TyTag _
        | TyUnion _ -> Error (NotYet "a tag union")
        | TyApply("list", _) -> Error (NotYet "a list")
        | TyPrimitive "unit" -> Error (NotYet "`()` as a value")
        | other -> Error (NotYet $"'{TypePrinting.tyToString other}'")

    /// The most values a function's parameters are passed as before the canonical ABI passes them in memory instead.
    let private maxFlatParameters = 16

    /// The parameters of a def's lambda, `value`, by name, if its value is a lambda.
    let private lambdaParameters (value: Expression) =
        match value.Kind with
        | Lambda(parameters, _, _) -> Some (parameters |> List.map fst)
        | _ -> None

    /// The types of the parameters of a function of type `input`, which takes `arity` parameters if its lambda says
    /// so: a function of several takes a record of them by position, and one of none takes `()`.
    let private parameterTypes (arity: int option) (input: Ty) =
        let positional (fields: Map<string, Ty>) = fields |> Map.toList |> List.sortBy (fst >> int) |> List.map snd
        match Unifier.unalias input, arity with
        | TyPrimitive "unit", (None | Some 0) -> []
        | _, Some 1 -> [ input ]
        | TyRecord(fields, _), _ when fields.Count > 1 && fields |> Map.forall (fun key _ -> fst (Int32.TryParse key)) -> positional fields
        | _ -> [ input ]

    /// The function `export`, of type `ty` and defined as `value`, as a component exports it, or why it can't: what
    /// can't cross the boundary, and the fix a message suggests, for each problem.
    let private functionOf (export: ComponentExport) (ty: Ty) (value: Expression option) (requiresContexts: bool) : Result<ComponentFunction, Problem list> =
        match Unifier.unalias ty with
        | TyFunc(input, output, contexts) ->
            let names = value |> Option.bind lambdaParameters
            let inputs = parameterTypes (names |> Option.map List.length) input
            let names =
                match names with
                | Some names when names.Length = inputs.Length -> names
                | _ -> inputs |> List.mapi (fun index _ -> $"p{index}")
            let parameters = List.zip names inputs |> List.map (fun (name, ty) -> witTypeOf false ty |> Result.map (fun wit -> witName name, wit))
            let result =
                match Unifier.unalias output with
                | TyPrimitive "unit" -> Ok None
                | _ -> witTypeOf false output |> Result.map Some
            let contextProblems = if contexts.IsEmpty && not requiresContexts then [] else [ NotYet "a function that requires contexts" ]
            // The canonical ABI passes more values than this in memory, which the compiler doesn't yet.
            let flatValues = parameters |> List.sumBy (function Ok (_, WitType.String) -> 2 | _ -> 1)
            let contextProblems = contextProblems @ (if flatValues > maxFlatParameters then [ NotYet $"more than {maxFlatParameters} values as its parameters" ] else [])
            let problems = (parameters @ [ result |> Result.map (fun _ -> "", WitType.Bool) ]) |> List.choose (function Error problem -> Some problem | Ok _ -> None)
            match contextProblems @ problems with
            | [] -> Ok { Export = export; Parameters = parameters |> List.choose Result.toOption; Result = result |> Result.toOption |> Option.flatten }
            | problems -> Error problems
        | _ -> Error [ Never("a value that isn't a function", "export a function: a WIT interface has only functions and types") ]

    /// The definitions of `module'` by name.
    let private valuesOf (module': Module) =
        module'
        |> List.choose (function
            | Kind (Def (ValueDef(_, name, _, value))) -> Some (name, value)
            | _ -> None)
        |> Map.ofList

    /// The errors in the defs `module'` marks `@component`, each by name with where it is written, of the types
    /// `typeOf` gives, and whether `requiresContexts` says they require contexts: each type that can't cross a component's
    /// boundary, `NYX0277`, or that the compiler can't take across yet, `NYX0301`. A def whose type is unknown, after an
    /// error of its own, has none.
    let errors (typeOf: string -> Ty option) (requiresContexts: string -> bool) (module': Module) (exports: (string * SourceSpan) list) : Diagnostic list =
        let values = valuesOf module'
        exports
        |> List.collect (fun (name, span) ->
            match typeOf name with
            | None -> []
            | Some ty ->
                let export = { Module = ""; Name = name; Definition = name }
                match functionOf export ty (values |> Map.tryFind name) (requiresContexts name) with
                | Ok _ -> []
                | Error problems ->
                    problems
                    |> List.distinct
                    |> List.map (fun problem ->
                        match problem with
                        | Never(what, fix) ->
                            Diagnostics.at DiagnosticCodes.ComponentBoundaryType span $"A component can't export '{name}': {what} can't cross a component's boundary. To fix it, {fix}"
                        | NotYet what ->
                            Diagnostics.at DiagnosticCodes.UnsupportedByBackend span $"A component can't export '{name}' yet: {what} can't cross a component's boundary yet"
                        |> Diagnostics.about name))

    /// The functions a component built from `typed` exports (see `TypedModule.ComponentExports`), each of the type it
    /// has there. One whose type can't cross the boundary, which `errors` reported, is left out.
    let functions (typed: TypedModule) : ComponentFunction list =
        let values = valuesOf typed.Module
        typed.ComponentExports
        |> List.choose (fun export ->
            typed.Types
            |> Map.tryFind export.Definition
            |> Option.bind (fun ty -> functionOf export ty (values |> Map.tryFind export.Definition) false |> Result.toOption))

    /// A WIT type as WIT writes it.
    let witTypeText (ty: WitType) =
        match ty with
        | WitType.Bool -> "bool"
        | WitType.S8 -> "s8"
        | WitType.S16 -> "s16"
        | WitType.S32 -> "s32"
        | WitType.S64 -> "s64"
        | WitType.U8 -> "u8"
        | WitType.U16 -> "u16"
        | WitType.U32 -> "u32"
        | WitType.U64 -> "u64"
        | WitType.F32 -> "f32"
        | WitType.F64 -> "f64"
        | WitType.String -> "string"

    /// The name of the world a package's WIT (see `render`) gives a component built from it: `component`, unless an
    /// interface of the package is named that, when it is `component-world`.
    let worldName (functions: ComponentFunction list) =
        if functions |> List.exists (fun func -> witName func.Export.Module = "component") then "component-world" else "component"

    /// The WIT of the package `package` that a program's `functions` are exported from: an interface for each module
    /// they are in, and a world (`worldName`) that includes its host's world, `hostWorld`, and exports them all.
    let render (package: string) (hostWorld: string) (functions: ComponentFunction list) =
        let interfaces =
            functions
            |> List.groupBy (fun func -> witName func.Export.Module)
            |> List.sortBy fst
        let signature (func: ComponentFunction) =
            let parameters = func.Parameters |> List.map (fun (name, ty) -> $"{name}: {witTypeText ty}") |> String.concat ", "
            let result = func.Result |> Option.map (fun ty -> $" -> {witTypeText ty}") |> Option.defaultValue ""
            $"  {witName func.Export.Name}: func({parameters}){result};"
        [ "/// Written by `nanyx build` from the program's definitions marked `@component`."
          $"package {package};"
          for name, members in interfaces do
              ""
              $"interface {name} {{"
              yield! members |> List.distinctBy (fun func -> func.Export.Name) |> List.map signature
              "}"
          ""
          $"world {worldName functions} {{"
          $"  include {hostWorld};"
          for name, _ in interfaces do
              $"  export {name};"
          "}" ]
        |> List.map (fun line -> line + "\n")
        |> String.concat ""

    /// Writes the WIT of a component built from a program whose `functions` are exported from `package` into the
    /// directory `target`, as wit-component and other languages' bindings generators read it: the package's own WIT
    /// (`render`), and under `deps`, the packages its host's world is made of, from the host's WIT directory, `hostWit`.
    /// A file already in `target` from an earlier build is replaced. Gives the name of the component's world.
    let write (hostWit: string) (hostWorld: string) (package: string) (functions: ComponentFunction list) (target: string) =
        let deps = IO.Path.Combine(target, "deps")
        if IO.Directory.Exists target then IO.Directory.Delete(target, true)
        IO.Directory.CreateDirectory deps |> ignore
        let rec copy (source: string) (destination: string) =
            IO.Directory.CreateDirectory destination |> ignore
            for file in IO.Directory.GetFiles source do
                IO.File.Copy(file, IO.Path.Combine(destination, IO.Path.GetFileName file))
            for directory in IO.Directory.GetDirectories source do
                copy directory (IO.Path.Combine(destination, IO.Path.GetFileName directory))
        // The host's own package, whose world the component's includes, is a dependency like the ones it uses.
        let hostPackage = IO.Path.Combine(deps, "host")
        IO.Directory.CreateDirectory hostPackage |> ignore
        for file in IO.Directory.GetFiles(hostWit, "*.wit") do
            IO.File.Copy(file, IO.Path.Combine(hostPackage, IO.Path.GetFileName file))
        let hostDeps = IO.Path.Combine(hostWit, "deps")
        if IO.Directory.Exists hostDeps then
            for directory in IO.Directory.GetDirectories hostDeps do
                copy directory (IO.Path.Combine(deps, IO.Path.GetFileName directory))
        IO.File.WriteAllText(IO.Path.Combine(target, "component.wit"), render package hostWorld functions)
        qualified package (worldName functions)
