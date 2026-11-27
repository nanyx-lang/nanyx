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
    /// A record of a named type, by its WIT name, with its fields in the order they are written, by their Nanyx names.
    | Record of name: string * fields: (string * WitType) list
    | Tuple of WitType list
    /// A variant of a named tag union, by its WIT name, with a case for each tag, by its Nanyx name, in the order they
    /// are written, each with its payload's type if it has one.
    | Variant of name: string * cases: (string * WitType option) list
    | List of WitType

module WitType =
    /// The types `ty` is made of, one level down.
    let children (ty: WitType) =
        match ty with
        | WitType.Record(_, fields) -> List.map snd fields
        | WitType.Tuple items -> items
        | WitType.Variant(_, cases) -> List.choose snd cases
        | WitType.List item -> [ item ]
        | _ -> []

    /// The names of the tags of every variant `ty` is made of, itself included.
    let rec tags (ty: WitType) =
        match ty with
        | WitType.Variant(_, cases) -> List.map fst cases @ (cases |> List.choose snd |> List.collect tags)
        | ty -> children ty |> List.collect tags

/// A function a component exports (see `ComponentExport`): its parameters, by their WIT names, and its result, none for
/// a function that returns `()`.
type ComponentFunction =
    { Export: ComponentExport
      Parameters: (string * WitType) list
      Result: WitType option
      /// Whether it can wait: its written type requires `Async`, so it is an `async func`, lifted with the callback ABI.
      Async: bool }

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

    /// Whether `name` is a WIT interface's full name, as an import names it: `acme:geometry/shapes`, with its
    /// version after an `@` if it has one.
    let isInterfaceName (name: string) =
        let colon = name.IndexOf ':'
        let slash = name.IndexOf '/'
        colon > 0 && slash > colon + 1 && slash < name.Length - 1 && not (name.Substring(slash + 1).StartsWith "@")

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
        /// An import names as many parameters as `named`, and its function takes `taken`.
        | Misnamed of named: int * taken: int
        /// A function requires contexts its type doesn't write, which the host can't provide it: only an export whose
        /// type writes them is run under the host's (see `Attributes.applyAttributes`).
        | Unwritten

    /// A WIT type as WIT writes it where it is used: a record or a variant by its name.
    let rec witTypeText (ty: WitType) =
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
        | WitType.Record(name, _)
        | WitType.Variant(name, _) -> name
        | WitType.Tuple items -> $"""tuple<{items |> List.map witTypeText |> String.concat ", "}>"""
        | WitType.List item -> $"list<{witTypeText item}>"

    /// A WIT type as a part of the name of a generic type applied to it: `list-s64` for `list<s64>`.
    let rec private label (ty: WitType) =
        match ty with
        | WitType.Tuple items -> "tuple-" + (items |> List.map label |> String.concat "-")
        | WitType.List item -> $"list-{label item}"
        | ty -> witTypeText ty

    /// Whether `fields` are a tuple's, by position.
    let private positional (fields: Map<string, Ty>) = fields |> Map.forall (fun key _ -> fst (Int32.TryParse key))

    /// The first error of `results`, or their values.
    let private all (results: Result<'T, Problem> list) =
        match results |> List.tryPick (function Error problem -> Some problem | Ok _ -> None) with
        | Some problem -> Error problem
        | None -> Ok (results |> List.choose Result.toOption)

    /// The WIT type of a value of type `ty`, or why it has none. `name` is the WIT name of the named type `ty` is what
    /// it stands for, which a record or a union must be: WIT names each. A named type applied to arguments is named with
    /// them, `outcome-string-s64` for `Outcome(string, int)`.
    let rec private witTypeOf (name: string option) (ty: Ty) : Result<WitType, Problem> =
        let rec hasVariable (ty: Ty) =
            match ty with
            | TyVar _
            | TyOpenRecord _ -> true
            | ty -> Ty.children ty |> List.exists hasVariable
        match ty with
        | ty when hasVariable ty -> Error (Never("a type variable", "write the type it stands for"))
        | TyAlias(typeName, args, underlying) -> namedType typeName args underlying
        | TyNominal(id, args, underlying, _) -> namedType id.Name args underlying
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
        | TyRecursive _
        | TySelf _ -> Error (Never("a recursive type", "pass its values in a list instead: WIT has no recursive types"))
        | TyRecord(fields, _) when positional fields ->
            fields |> Map.toList |> List.sortBy (fst >> int) |> List.map (snd >> witTypeOf None) |> all |> Result.map WitType.Tuple
        | TyRecord(fields, FieldOrder order) ->
            match name with
            | None -> Error (Never("a record with no name", "name it with a `type`, which names it in the WIT"))
            | Some name ->
                let written = order |> List.filter fields.ContainsKey
                let names = written @ (fields.Keys |> Seq.filter (fun field -> not (List.contains field written)) |> List.ofSeq)
                names
                |> List.map (fun field -> witTypeOf None fields.[field] |> Result.map (fun wit -> field, wit))
                |> all
                |> Result.map (fun fields -> WitType.Record(name, fields))
        | TyTag _
        | TyUnion _ ->
            let members = match ty with TyUnion members -> members | single -> [ single ]
            let isTag (member': Ty) = match Unifier.unalias member' with TyTag _ -> true | _ -> false
            match name with
            | _ when not (List.forall isTag members) ->
                Error (Never("a union of types other than tags", "tag each of its members, as `#circle(float) | #square(float)`"))
            | None -> Error (Never("a tag union with no name", "name it with a `type`, which names it in the WIT"))
            | Some name ->
                members
                |> List.map (fun member' ->
                    match Unifier.unalias member' with
                    | TyTag(tag, None) -> Ok (tag, None)
                    | TyTag(tag, Some payload) -> witTypeOf None payload |> Result.map (fun wit -> tag, Some wit)
                    | _ -> Error (Never("a union of types other than tags", "tag each of its members, as `#circle(float) | #square(float)`")))
                |> all
                |> Result.map (fun cases -> WitType.Variant(name, cases))
        | TyApply("list", [ item ]) -> witTypeOf None item |> Result.map WitType.List
        | TyPrimitive "unit" -> Error (NotYet "`()` as a value")
        | other -> Error (NotYet $"'{Unifier.typeText Map.empty other}'")

    /// The WIT type of a value of the type named `typeName` applied to `args`, which stands for `underlying`. A type
    /// named through an import's alias, `units.Length`, is named by its own name, as in its own module.
    and private namedType (typeName: string) (args: Ty list) (underlying: Ty) =
        let typeName = typeName.Substring(typeName.LastIndexOf '.' + 1)
        args
        |> List.map (witTypeOf None)
        |> all
        |> Result.bind (fun args -> witTypeOf (Some (witName typeName :: List.map label args |> String.concat "-")) underlying)

    /// The names of the parameters of the function a def's `value` is: its lambda's, or those its `@import` gives, if
    /// it gives any, and whether they are an import's, which must name every parameter.
    let private parameterNames (value: Expression) =
        match value.Kind with
        | Lambda(parameters, _, _) -> Some (parameters |> List.map fst, false)
        | ComponentImportSyntax.Import(_, _, (_ :: _ as names)) -> Some (names, true)
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
            let named = value |> Option.bind parameterNames
            let lambda = match named with Some (names, false) -> Some names.Length | _ -> None
            let inputs = parameterTypes lambda input
            let names, misnamed =
                match named with
                | Some (names, _) when names.Length = inputs.Length -> names, []
                | Some (names, true) -> inputs |> List.mapi (fun index _ -> $"p{index}"), [ Misnamed(names.Length, inputs.Length) ]
                | _ -> inputs |> List.mapi (fun index _ -> $"p{index}"), []
            let parameters = List.zip names inputs |> List.map (fun (name, ty) -> witTypeOf None ty |> Result.map (fun wit -> witName name, wit))
            let result =
                match Unifier.unalias output with
                | TyPrimitive "unit" -> Ok None
                | _ -> witTypeOf None output |> Result.map Some
            let contextProblems = if contexts.IsEmpty && not requiresContexts then [] else [ Unwritten ]
            let problems = (parameters @ [ result |> Result.map (fun _ -> "", WitType.Bool) ]) |> List.choose (function Error problem -> Some problem | Ok _ -> None)
            match misnamed @ contextProblems @ problems with
            | [] -> Ok { Export = export; Parameters = parameters |> List.choose Result.toOption; Result = result |> Result.toOption |> Option.flatten; Async = false }
            | problems -> Error problems
        | _ -> Error [ Never("a value that isn't a function", "export a function: a WIT interface has only functions and types") ]

    /// Whether the type `typeExpr` written for a def says it requires contexts, as `[Clock] int -> int` does.
    let rec writesContexts (typeExpr: TypeExpr) =
        match typeExpr.Kind with
        | TypeWithContext(contexts, inner) -> not contexts.IsEmpty || writesContexts inner
        | TypeConstraint(_, inner, _)
        | TypeWhere(inner, _) -> writesContexts inner
        | _ -> false

    /// The function type `ty` with no contexts: the type a component gives an export that runs under its host's
    /// (see `Hosts.providedExportName`), which requires the contexts `provide` does, and none the host doesn't provide.
    let withoutContexts (ty: Ty) =
        match Unifier.unalias ty with
        | TyFunc(input, output, _) -> TyFunc(input, output, [])
        | other -> other

    /// The definitions of `module'` by name.
    let private valuesOf (module': Module) =
        module'
        |> List.choose (function
            | Kind (Def (ValueDef(_, name, _, value))) -> Some (name, value)
            | _ -> None)
        |> Map.ofList

    /// The errors in the defs `module'` marks `@component`, `exports`, and `@import`, `imports`, each by name with where
    /// it is written, of the types
    /// `typeOf` gives, and whether `requiresContexts` says they require contexts: each type that can't cross a component's
    /// boundary, `NYX0277`, or that the compiler can't take across yet, `NYX0301`, as an export that requires contexts its
    /// type doesn't write is, and an import that names other than
    /// as many parameters as its function takes, `NYX0013`. A def whose type is unknown, after an error of its own, has
    /// none.
    let errors (typeOf: string -> Ty option) (requiresContexts: string -> bool) (module': Module) (exports: (string * SourceSpan) list) (imports: (string * SourceSpan) list) : Diagnostic list =
        let values = valuesOf module'
        let written =
            module'
            |> List.choose (function
                | Kind (Def (ValueDef(_, name, Some typeExpr, _))) when writesContexts typeExpr -> Some name
                | _ -> None)
            |> Set.ofList
        (exports |> List.map (fun (name, span) -> "export", name, span)) @ (imports |> List.map (fun (name, span) -> "import", name, span))
        |> List.collect (fun (verb, name, span) ->
            match typeOf name with
            | None -> []
            | Some ty ->
                let export = { Module = ""; Name = name; Definition = name; Waits = false }
                match functionOf export ty (values |> Map.tryFind name) (requiresContexts name) with
                | Ok _ -> []
                | Error problems ->
                    problems
                    |> List.distinct
                    |> List.map (fun problem ->
                        match problem with
                        | Never(what, fix) ->
                            Diagnostics.at DiagnosticCodes.ComponentBoundaryType span $"A component can't {verb} '{name}': {what} can't cross a component's boundary. To fix it, {fix}"
                        | NotYet what ->
                            Diagnostics.at DiagnosticCodes.UnsupportedByBackend span $"A component can't {verb} '{name}' yet: {what} can't cross a component's boundary yet"
                        | Unwritten when verb = "import" ->
                            Diagnostics.at DiagnosticCodes.UnsupportedByBackend span $"A component can't import '{name}' yet: a function that requires contexts can't cross a component's boundary yet"
                        | Unwritten when written.Contains name ->
                            Diagnostics.at DiagnosticCodes.UnsupportedByBackend span $"A component can't export '{name}' here: it requires contexts, and its package names no host to provide them"
                        | Unwritten ->
                            Diagnostics.at DiagnosticCodes.UnsupportedByBackend span $"A component can't export '{name}': it requires contexts its type doesn't write. To fix it, write them in its type, as `[Clock] int -> int`, and the host's component root provides them"
                        | Misnamed(named, taken) ->
                            let parameters = if named = 1 then "parameter" else "parameters"
                            Diagnostics.at DiagnosticCodes.UnexpectedAttributeArgument span $"The import '{name}' names {named} {parameters}, and its function takes {taken}"
                        |> Diagnostics.about name))

    /// Whether `contexts`, those a def requires, include the standard library's `Async`: whether the def can wait.
    let requiresAsync (contexts: Ty list) =
        contexts
        |> List.exists (function
            | TyNominal({ Home = StandardLibraryModule home; Name = "Async" }, [], _, _) -> home = ModuleOrigin.asyncName
            | _ -> false)

    /// The functions a component built from `typed` exports (see `TypedModule.ComponentExports`), each of the type it
    /// has there. One whose type can't cross the boundary, which `errors` reported, is left out. One that can wait
    /// (`ComponentExport.Waits`) is `Async`.
    let functions (typed: TypedModule) : ComponentFunction list =
        let values = valuesOf typed.Module
        typed.ComponentExports
        |> List.choose (fun export ->
            typed.Types
            |> Map.tryFind export.Definition
            |> Option.map (fun ty -> if export.Definition = Hosts.providedExportName export.Name then withoutContexts ty else ty)
            |> Option.bind (fun ty -> functionOf export ty (values |> Map.tryFind export.Definition) false |> Result.toOption)
            |> Option.map (fun func -> { func with Async = export.Waits }))

    /// The name of the world a package's WIT (see `render`) gives a component built from it: `component`, unless an
    /// interface of the package is named that, when it is `component-world`.
    let worldName (functions: ComponentFunction list) =
        if functions |> List.exists (fun func -> witName func.Export.Module = "component") then "component-world" else "component"

    /// The lines of the WIT interface `name` with the functions `members`, after the records and variants they are
    /// made of, each named once, after those it is made of.
    let private interfaceLines (name: string) (members: ComponentFunction list) =
        let defined = Collections.Generic.HashSet<string>()
        let rec definitions (ty: WitType) =
            let inner = WitType.children ty |> List.collect definitions
            match ty with
            | WitType.Record(name, _)
            | WitType.Variant(name, _) when defined.Add name -> inner @ [ ty ]
            | _ -> inner
        let typeDefinitions =
            members
            |> List.collect (fun func -> (List.map snd func.Parameters @ Option.toList func.Result) |> List.collect definitions)
            |> List.collect (fun ty ->
                match ty with
                | WitType.Record(name, fields) ->
                    [ $"  record {name} {{"
                      yield! fields |> List.map (fun (field, ty) -> $"    {witName field}: {witTypeText ty},")
                      "  }"
                      "" ]
                | WitType.Variant(name, cases) ->
                    [ $"  variant {name} {{"
                      yield!
                          cases
                          |> List.map (fun (case, payload) ->
                              match payload with
                              | Some ty -> $"    {witName case}({witTypeText ty}),"
                              | None -> $"    {witName case},")
                      "  }"
                      "" ]
                | _ -> [])
        let signature (func: ComponentFunction) =
            let parameters = func.Parameters |> List.map (fun (name, ty) -> $"{name}: {witTypeText ty}") |> String.concat ", "
            let result = func.Result |> Option.map (fun ty -> $" -> {witTypeText ty}") |> Option.defaultValue ""
            let kind = if func.Async then "async func" else "func"
            $"  {witName func.Export.Name}: {kind}({parameters}){result};"
        [ $"interface {name} {{"
          yield! typeDefinitions
          yield! members |> List.distinctBy (fun func -> func.Export.Name) |> List.map signature
          "}" ]

    let private text (lines: string list) = lines |> List.map (fun line -> line + "\n") |> String.concat ""

    /// The package of the WIT interface whose full name is `name`, and the interface's own name:
    /// `acme:geometry/shapes@1.0.0` is the interface `shapes` of `acme:geometry@1.0.0`.
    let private splitInterface (name: string) =
        let slash = name.IndexOf '/'
        match name.IndexOf('@', slash) with
        | at when at > slash -> name.Substring(0, slash) + name.Substring at, name.Substring(slash + 1, at - slash - 1)
        | _ -> name.Substring(0, slash), name.Substring(slash + 1)

    /// The WIT of the package `package` that a program's `functions` are exported from: an interface for each module
    /// they are in, and a world (`worldName`) that includes its host's world, `hostWorld`, imports the interfaces of
    /// `imports`, and exports its own.
    let render (package: string) (hostWorld: string) (functions: ComponentFunction list) (imports: ComponentFunction list) =
        let interfaces =
            functions
            |> List.groupBy (fun func -> witName func.Export.Module)
            |> List.sortBy fst
        text
            [ "/// Written by `nanyx build` from the program's definitions marked `@component` and `@import`."
              $"package {package};"
              for name, members in interfaces do
                  ""
                  yield! interfaceLines name members
              ""
              $"world {worldName functions} {{"
              $"  include {hostWorld};"
              for name in imports |> List.map _.Export.Module |> List.distinct |> List.sort do
                  $"  import {name};"
              for name, _ in interfaces do
                  $"  export {name};"
              "}" ]

    /// The WIT of each package that `imports` are imported from, by the package's name, as the program's declarations
    /// write it: an interface for each interface they are in, with their functions and the types they are made of.
    let importedPackages (imports: ComponentFunction list) =
        imports
        |> List.groupBy (fun func -> fst (splitInterface func.Export.Module))
        |> List.sortBy fst
        |> List.map (fun (package, members) ->
            package,
            text
                [ "/// Written by `nanyx build` from the program's definitions marked `@import`."
                  $"package {package};"
                  for name, functions in members |> List.groupBy (fun func -> snd (splitInterface func.Export.Module)) |> List.sortBy fst do
                      ""
                      yield! interfaceLines name functions ])

    /// Writes the WIT of a component built from a program whose `functions` are exported from `package` into the
    /// directory `target`, as wit-component and other languages' bindings generators read it: the package's own WIT
    /// (`render`), and under `deps`, the packages its host's world is made of, from the host's WIT directory, `hostWit`,
    /// and the packages of the functions it `imports` (`importedPackages`). A file already in `target` from an earlier
    /// build is replaced. Gives the name of the component's world.
    let write (hostWit: string) (hostWorld: string) (package: string) (functions: ComponentFunction list) (imports: ComponentFunction list) (target: string) =
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
        for imported, wit in importedPackages imports do
            let directory = IO.Path.Combine(deps, imported.Replace(':', '-').Replace('@', '-'))
            IO.Directory.CreateDirectory directory |> ignore
            IO.File.WriteAllText(IO.Path.Combine(directory, "package.wit"), wit)
        IO.File.WriteAllText(IO.Path.Combine(target, "component.wit"), render package hostWorld functions imports)
        qualified package (worldName functions)

    /// The functions a component built from `typed` imports: its defs marked `@import` that the program uses, each in
    /// the interface it names (`ComponentExport.Module`, for an import), by the function's name there, and of the type
    /// it has. One whose type can't cross the boundary, which `errors` reported, is left out.
    let imports (typed: TypedModule) : ComponentFunction list =
        typed.Module
        |> List.choose (function
            | Kind (Def (ValueDef(_, name, _, ({ Kind = ComponentImportSyntax.Import(witInterface, functionName, _) } as value)))) ->
                let import = { Module = witInterface; Name = functionName; Definition = name; Waits = false }
                typed.Types |> Map.tryFind name |> Option.bind (fun ty -> functionOf import ty (Some value) false |> Result.toOption)
            | _ -> None)

    /// The foreign function the backend calls a component import as, `func`: its interface and name, and the core value
    /// type each Nanyx value it takes and gives is held in, a record, a string or a list by its address. The module
    /// defines the function itself, lowering its arguments to the canonical ABI's and lifting its result (see
    /// `ComponentImports` in the backend).
    let externOf (func: ComponentFunction) : Extern =
        let valueOf (ty: WitType) =
            match ty with
            | WitType.S64
            | WitType.U64 -> ExternValue.I64
            | WitType.F32 -> ExternValue.F32
            | WitType.F64 -> ExternValue.F64
            | _ -> ExternValue.I32
        { Module = func.Export.Module
          Name = func.Export.Name
          Parameters = func.Parameters |> List.map (snd >> valueOf)
          Result = func.Result |> Option.map valueOf }
