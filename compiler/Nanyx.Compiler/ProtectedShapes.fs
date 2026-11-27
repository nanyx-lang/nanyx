/// Why a value of a protected or private type's shape is no value of the type outside the type's home module, and how
/// to make one there: with the definitions attached to the type that return it, such as `Point.new`.
module internal NyxCompiler.ProtectedShapes

open Parser.Program
open NyxCompiler
open NyxCompiler.InferState

/// Whether `ty` is the nominal type `nominal`, or a tag or union that holds it in a tag's payload.
let rec private gives (nominal: NominalId) (ty: Ty) =
    match Unifier.unalias ty with
    | TyNominal(id, _, _, _) -> id = nominal
    | TyTag(_, Some payload) -> gives nominal payload
    | TyUnion items -> items |> List.exists (gives nominal)
    | _ -> false

/// The last part of a dotted name: `Point` of `Points.Point`.
let private lastPart (name: string) =
    match name.LastIndexOf '.' with
    | -1 -> name
    | dot -> name.Substring(dot + 1)

/// The ways to make a value of `nominal` among `names`: each definition attached to the type that returns it, directly
/// or in a tag, with its type, by the shortest name it has (`aliases` gives the names a `module name =` block's
/// definitions are imported under). One definition with several names is one way, and the first three are given.
let makers (aliases: Map<string, string>) (names: (string * TypeScheme) list) (nominal: NominalId) : (string * Ty) list =
    let qualified (name: string) = aliases |> Map.tryFind name |> Option.defaultValue name
    names
    |> List.filter (fun (name, scheme) ->
        let name = qualified name
        match name.LastIndexOf '.', Unifier.unalias scheme.Type with
        | dot, TyFunc(_, result, _) when dot > 0 -> lastPart (name.Substring(0, dot)) = lastPart nominal.Name && gives nominal result
        | _ -> false)
    |> List.groupBy (fun (name, scheme) ->
        match scheme.Symbol with
        | Some symbol -> Choice1Of2 symbol
        | None -> Choice2Of2 (qualified name))
    |> List.map (fun (_, named) ->
        let name, scheme = named |> List.minBy (fun (name, _) -> name.Length, name)
        let written = aliases |> Map.filter (fun _ target -> target = qualified name) |> Map.keys |> Seq.append [ name ] |> Seq.minBy (fun alias -> alias.Length, alias)
        written, scheme.Type)
    |> List.sortBy fst
    |> List.truncate 3

/// `makers` written as a reader is told them: `'a'`, `'a' or 'b'`, `'a', 'b' or 'c'`.
let private orList (makers: (string * Ty) list) =
    match makers |> List.map (fun (name, _) -> $"'{name}'") with
    | [] -> ""
    | [ only ] -> only
    | names -> String.concat ", " (List.truncate (names.Length - 1) names) + " or " + List.last names

/// What to add to an error about making a value of the type outside its home module: how to make one there.
let private makeOne (makers: (string * Ty) list) =
    match makers with
    | [] -> ""
    | _ -> $": make one with {orList makers}"

/// The message for the constructor of `info`, a nominal type or context named `name`, written outside its home module
/// (NYX0212): it names the ways among `names` to make one, if there are any.
let constructedOutside (state: InferState) (names: (string * TypeScheme) list) (name: string) (info: TypeDefInfo) =
    let kind = if info.IsPrivate then "private" else "protected"
    if info.IsContext then
        $"The {kind} context '{name}' can only be constructed in the module that defines it: call a function that module exports to supply it instead"
    else
        match makers state.BlockAliases names (TypeDefInfo.nominalId info) with
        | [] -> $"The {kind} type '{name}' can only be constructed in the module that defines it: use a function that module exports instead"
        | found -> $"The {kind} type '{name}' can only be constructed in the module that defines it{makeOne found}"

/// The message for `with` on a value of `nominal`, private when `isPrivate`, outside the type's home module (NYX0212):
/// a value the update makes is one the module never made, so only the module updates one. It names the ways among
/// `names` to make one, if there are any.
let updatedOutside (state: InferState) (names: (string * TypeScheme) list) (nominal: NominalId) (isPrivate: bool) =
    let kind = if isPrivate then "private" else "protected"
    let name = lastPart nominal.Name
    match makers state.BlockAliases names nominal with
    | [] -> $"The {kind} type '{name}' can only be updated with `with` in the module that defines it: use a function that module exports instead"
    | found -> $"The {kind} type '{name}' can only be updated with `with` in the module that defines it{makeOne found}"

/// The private type `ty` is, if it is one whose representation code at `span` cannot see: outside its home module a
/// private type has no shape, so its fields are not read, matched or spread there (see "Private (opaque) types").
let hiddenRepresentation (state: InferState) (span: SourceSpan) (ty: Ty) : NominalId option =
    match Unifier.unalias ty with
    | TyNominal(nominal, _, _, true) when not (Visibility.inNominalHome state nominal span) -> Some nominal
    | _ -> None

/// The error (NYX0212) for taking a value of the private type `nominal` apart at `span`, outside its home module: by a
/// pattern that names its fields, or by spreading it into a record.
let takenApartOutside (nominal: NominalId) (span: SourceSpan) =
    Diagnostics.at DiagnosticCodes.NominalConstructedOutsideItsModule span
        $"The private type '{lastPart nominal.Name}' can only be taken apart in the module that defines it: use a function that module exports instead"

/// The error (NYX0212) for reading the field `fieldName` of a value of the private type `nominal` at `span`, outside its
/// home module.
let fieldReadOutside (nominal: NominalId) (fieldName: string) (span: SourceSpan) =
    Diagnostics.at DiagnosticCodes.NominalConstructedOutsideItsModule span
        $"The private type '{lastPart nominal.Name}' has no field '{fieldName}' outside the module that defines it: use a function that module exports instead"

/// The module that makes values of `nominal`, as a reader knows it: the `module name =` block it is defined in, or its
/// file's module.
let private homeModule (nominal: NominalId) =
    match nominal.Name.LastIndexOf '.' with
    | dot when dot > 0 -> Some (nominal.Name.Substring(0, dot))
    | _ ->
        match nominal.Home with
        | SourceFile path -> Some (System.IO.Path.GetFileNameWithoutExtension path)
        | StandardLibraryModule name -> Some name
        | InMemory -> None

/// The types a function of type `input` takes, in order: each item of a tuple, or the one input.
let private parameterTypes (input: Ty) =
    match Unifier.unalias input with
    | TyRecord(fields, _) when not fields.IsEmpty && fields |> Map.forall (fun name _ -> fst (System.Int32.TryParse name)) ->
        fields |> Map.toList |> List.sortBy (fst >> int) |> List.map snd
    | other -> [ other ]

/// The fields of `shape` in the order they are written, each with its type.
let private fieldsInOrder (shape: Ty) =
    match Unifier.unalias shape with
    | TyRecord(fields, order) ->
        let written = order.Names |> List.filter fields.ContainsKey
        let names = if written.Length = fields.Count then written else fields |> Map.keys |> List.ofSeq
        names |> List.map (fun name -> name, fields.[name])
    | _ -> []

/// The fix that makes the value `value`, a record or tuple of `shape`, with `maker` instead: offered when it is the one
/// way to make a value of `nominal`, takes the fields as its parameters in order, and returns a value of the type.
let private fixFor (source: string) (nominal: NominalId) (shape: Ty) (value: Expression) (makers: (string * Ty) list) : Fix list =
    let fields = fieldsInOrder shape
    let valueText (expr: Expression) =
        if expr.Span.File = value.Span.File && expr.Span.Start >= 0 && expr.Span.End <= source.Length && expr.Span.Start < expr.Span.End then
            Some (source.Substring(expr.Span.Start, expr.Span.End - expr.Span.Start))
        else
            None
    let values =
        match value.Kind with
        | RecordExpr written ->
            let named = written |> List.choose (function NamedField(name, value) -> Some (name, value) | PositionalField _ -> None) |> Map.ofList
            let positional = written |> List.choose (function PositionalField value -> Some value | NamedField _ -> None)
            if written.Length <> fields.Length then None
            else
                fields
                |> List.mapi (fun index (name, _) -> named |> Map.tryFind name |> Option.orElse (List.tryItem index positional))
                |> fun found -> if List.forall Option.isSome found then Some (List.map Option.get found) else None
        | TupleExpr items when items.Length = fields.Length -> Some items
        | _ -> None
    match makers, values with
    | [ name, makerTy ], Some values ->
        match Unifier.unalias makerTy with
        | TyFunc(input, result, _) ->
            let takesFields =
                let parameters = parameterTypes input
                parameters.Length = fields.Length
                && List.forall2 (fun parameter (_, field) -> Unifier.unalias parameter = Unifier.unalias field) parameters fields
            let givesType =
                match Unifier.unalias result with
                | TyNominal(id, _, _, _) -> id = nominal
                | _ -> false
            let texts = values |> List.map valueText
            if takesFields && givesType && List.forall Option.isSome texts then
                let call = $"""{name}({texts |> List.map Option.get |> String.concat ", "})"""
                [ { Title = $"Make it with `{name}`"; Edits = [ { Span = value.Span; NewText = call } ] } ]
            else []
        | _ -> []
    | _ -> []

/// The note for a mismatch where a value of no nominal type is given for `nominal`, private when `isPrivate`, outside
/// the type's home module, whatever the value's shape: only that module makes values of it, and the ways among `names`
/// to make one, or that no code outside the module can when it exports none (#1804). A private type's representation is
/// not named.
let homeNote (state: InferState) (names: (string * TypeScheme) list) (nominal: NominalId) (isPrivate: bool) : string =
    let typeName = lastPart nominal.Name
    let kind = if isPrivate then "private" else "protected"
    let home = homeModule nominal
    let maker =
        match home with
        | Some home -> $"only the module '{home}'"
        | None -> "only its own module"
    match makers state.BlockAliases names nominal with
    | [] ->
        let outside = match home with Some home -> $"outside '{home}'" | None -> "outside its module"
        $"'{typeName}' is {kind}, so {maker} makes values of it, and it exports nothing that makes one: no code {outside} can make a '{typeName}'"
    | found -> $"'{typeName}' is {kind}, so {maker} makes values of it{makeOne found}"

/// The error for a value of the shape `shape` of the nominal type `nominal`, private when `isPrivate`, given for it at
/// `span`, outside the type's home module: only that module makes values of it. It names the ways to make one in
/// `names`, and offers to make the value with the one way when there is one that fits (see `fixFor`).
let shapeError
    (state: InferState)
    (names: (string * TypeScheme) list)
    (source: string)
    (module': Module)
    (nominal: NominalId)
    (isPrivate: bool)
    (shape: Ty)
    (span: SourceSpan)
    : Diagnostic =
    let typeName = lastPart nominal.Name
    let kind = if isPrivate then "private" else "protected"
    let maker =
        match homeModule nominal with
        | Some home -> $"only the module '{home}'"
        | None -> "only its own module"
    let what =
        match Unifier.unalias shape with
        | TyRecord _ -> "a record"
        | _ -> "a value"
    let found = makers state.BlockAliases names nominal
    let message =
        $"'{typeName}' is {kind}, so {maker} makes values of it, and {what} of its shape isn't a '{typeName}' here{makeOne found}"
    let value =
        module'
        |> List.collect SyntaxTree.ofItem
        |> List.collect SyntaxTree.expressionsWithin
        |> List.tryFind (fun expr -> expr.Span = span)
    let fixes = value |> Option.map (fun value -> fixFor source nominal shape value found) |> Option.defaultValue []
    { Diagnostics.at DiagnosticCodes.ProtectedShape span message with Fixes = fixes }
