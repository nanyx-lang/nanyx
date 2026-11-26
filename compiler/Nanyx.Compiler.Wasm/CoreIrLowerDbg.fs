/// The printers `dbg` prints its value through: for each concrete type `dbg` is called at, a function that writes a
/// value of the type as text, generated from the type the type checker resolved (see "Printing values" in the docs'
/// work in progress). A printer reads no `ToString` the type has: it shows what the value is.
///
/// A number, a bool and a string are written as a literal of their own is, a string quoted and escaped. A tag, a
/// tuple, a record, a list, a map and a set are written as they would be made, each part through the printer of its
/// own type: `#some(5)`, `(1, "a")`, `(age = 36, name = "Ada")`, `[1, 2]`, `Map["tea" => 3]`, `Set[1, 2]`.
/// A record's fields are in alphabetical order, as a record type keeps no other. A nominal type is written by its name
/// around its shape, `Money(250)`, inside its module or out; unit is `()`; and a function, which has nothing to show,
/// says that it is one and of what type, `<function int -> string>`.
module internal Transpiler.Wasm.CoreIrLowerDbg

open NyxCompiler
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes

/// Where a `dbg` at `span` is written, in a debug build that names files from `root` (see `DbgBuild`).
let site (root: string option) (span: Parser.Program.SourceSpan) : DbgSite =
    { File = PackageModules.shownFile root span.File; Line = span.StartLine + 1; Column = span.StartColumn + 1 }

/// A function that writes values as text.
type Printer =
    /// Writes a value of the type.
    | Showing of Ty
    /// Writes the elements of a list after its first, each after `, `, onto the text written so far, which it takes
    /// second. Each element is of the type; with `entries`, each is a map's entry, a tuple written `key => value`.
    | ShowingRest of Ty * entries: bool

/// What a printer needs from the lowering of the function around the `dbg` (see `CoreIrLower.FunctionLowering`).
type PrinterTools =
    { NewVar: string -> VarKind -> IrType -> Var
      NewTemp: IrType -> Var
      /// The name of the printer's function, which is lowered in turn if it was not asked for before.
      PrinterOf: Printer -> string
      ToIrType: Ty -> IrType }

/// The text of a value of `ty` that holds nothing to read, if `ty` is such a type: unit, a function, an error.
let private constantText (ty: Ty) : string option =
    match Unifier.unalias ty with
    | TyPrimitive "unit" -> Some "()"
    | TyRecord(fields, _) when fields.IsEmpty -> Some "()"
    | TyPrimitive "?error" -> Some "???"
    | TyFunc _ -> Some $"<function {Unifier.tyToDisplayString Ascii ty}>"
    | _ -> None

/// Whether the text of a value of `ty` is in brackets of its own, so that a tag or a nominal type around it needs none.
let private bracketed (ty: Ty) =
    match Unifier.unalias ty with
    | TyRecord _
    | TyPrimitive "unit" -> true
    | _ -> false

/// The tags of a union, each once, with their payloads' types. The rest of a union that nothing closed is a variable,
/// and adds no tags.
let rec private tagsOf (ty: Ty) : (string * Ty option) list =
    match Unifier.unalias ty with
    | TyTag(name, payload) -> [ name, payload ]
    | TyUnion items -> items |> List.collect tagsOf |> List.distinctBy fst
    | _ -> []

/// Whether a printer can write a value of `ty` (see `printerFunction`), whose type `toIrType` gives: a debug build's
/// probe writes only such locals (see `CoreIrProbes`).
let printable (toIrType: Ty -> IrType) (ty: Ty) =
    // A type that holds itself is written by printers that call each other, so its parts are looked at only so deep.
    let rec writable (depth: int) (ty: Ty) =
        let part = writable (depth + 1)
        if depth > 8 then true
        else
        match constantText ty with
        | Some _ -> true
        | None ->
        match ty with
        | RecursiveNominals.Nominal(_, _, shape, _) -> part shape
        | _ ->
        match Unifier.unalias ty with
        | TyPrimitive("int" | "bool" | "float" | "F32" | "string")
        | Chars.CharType
        | SizedIntegerType _ -> true
        | TyTag _
        | TyUnion _ as union -> tagsOf union |> List.forall (snd >> Option.forall part)
        | TyRecord(fields, _) -> fields.Values |> Seq.forall part
        | TyApply("list", [ elementTy ])
        | Sets.SetType elementTy
        | Arrays.ArrayType elementTy -> part elementTy
        | Maps.MapType(keyTy, valueTy) -> part keyTy && part valueTy
        | _ -> false
    try
        toIrType ty |> ignore
        writable 0 ty
    with CannotLower _ -> false

/// `ty` as part of a printer's name, such as `list_int` in `__dbg_show_list_int`. Types printed differently are
/// named differently, which their `IrType`s are not: a nominal type is the type of its shape, and a map is a list.
let rec private mangle (ty: Ty) : string =
    let all (tys: Ty list) = tys |> List.map mangle |> String.concat "_"
    match ty with
    | TyAlias(name, args, TyRecursive _) -> $"{name}_{all args}"
    | TyAlias(_, _, underlying) -> mangle underlying
    | TyNominal(nominal, args, TyRecursive _, _) -> $"{nominal.Name}_{all args}"
    | TyNominal(nominal, _, shape, _) -> $"{nominal.Name}_{mangle shape}"
    | TyPrimitive name -> name
    | TyTag(name, None) -> $"tag_{name}"
    | TyTag(name, Some payload) -> $"tag_{name}_{mangle payload}"
    | TyUnion _ ->
        let tag (name: string, payload: Ty option) = payload |> Option.map (fun payload -> $"{name}_{mangle payload}") |> Option.defaultValue name
        "tags_" + (tagsOf ty |> List.map tag |> String.concat "_")
    | TyRecord(fields, _) -> "rec_" + (fields |> Map.toList |> List.map (fun (name, fieldTy) -> $"{name}_{mangle fieldTy}") |> String.concat "_")
    | TyApply(name, args) -> $"{name}_{all args}"
    | TyScope scope -> $"scope_{mangle scope}"
    | TyMeasure(dimension, storage) ->
        let dimensionName = dimension |> List.map (fun (id, exponent) -> $"{id.Name}{exponent}") |> String.concat "_"
        $"measure_{dimensionName}_{mangle storage}"
    | TyFunc(input, output, _) -> $"fn_{mangle input}_to_{mangle output}"
    | TyContexts _ -> "contexts"
    | TyVar _
    | TyOpenRecord _ -> "unit"
    | TyRecursive _
    | TySelf _
    | TyParam _ -> internalError "a recursive alias's definition outside the alias"

/// The name a printer is asked for by, and its function's name.
let key (printer: Printer) : string =
    match printer with
    | Showing ty -> $"__dbg_show_{mangle ty}"
    | ShowingRest(ty, false) -> $"__dbg_rest_{mangle ty}"
    | ShowingRest(ty, true) -> $"__dbg_entries_{mangle ty}"

/// The function `name` that is `printer`. `callee` gives the name to call a definition by at a type, as a map's and a
/// set's `toList` are called for their entries.
let printerFunction (tools: PrinterTools) (callee: string -> Ty -> string) (name: string) (printer: Printer) : Function =
    let text (parts: InterpolationPart list) = Expr.make (Interpolate parts) IrString
    let local (prefix: string) (ty: IrType) = tools.NewVar prefix Local ty

    /// Passes `k` the text of `value`, a value of `ty`: a local that holds it, or a constant's text.
    let show (ty: Ty) (value: Atom) (k: InterpolationPart -> Expr) : Expr =
        match constantText ty with
        | Some constant -> k (Text constant)
        | None ->
            let written = local "__dbg_part" IrString
            Expr.letIn written (Expr.make (Call(tools.PrinterOf (Showing ty), [ value ])) IrString) (k (Hole(written, HoleString)))

    /// The text of `value`, a value of `ty`, after `prefix`: in brackets unless it has its own.
    let wrapped (prefix: string) (ty: Ty) (value: Atom) : Expr =
        show ty value (fun part -> text (if bracketed ty then [ Text prefix; part ] else [ Text prefix; Text "("; part; Text ")" ]))

    /// Passes `k` the text of each of `fields` (by name, with their types) of the record `record`, each field read
    /// and written in turn.
    let showFields (record: Var) (fields: (string * Ty) list) (k: InterpolationPart list -> Expr) : Expr =
        let slots = match record.Type with IrRecord stored -> stored |> List.map fst | _ -> []
        let rec next (remaining: (string * Ty) list) (acc: InterpolationPart list) =
            match remaining with
            | [] -> k (List.rev acc)
            | (_, ty) :: rest when (constantText ty).IsSome -> show ty AUnit (fun part -> next rest (part :: acc))
            | (fieldName, ty) :: rest ->
                let slot = slots |> List.findIndex ((=) fieldName)
                let field = tools.NewTemp (tools.ToIrType ty)
                Expr.letIn field (Expr.make (Field(AVar record, slot)) field.Type) (show ty (AVar field) (fun part -> next rest (part :: acc)))
        next fields []

    /// `parts`, with `, ` between each two.
    let separated (parts: InterpolationPart list list) =
        parts |> List.mapi (fun index part -> if index = 0 then part else Text ", " :: part) |> List.concat

    /// Matches `value`, a parameter, against `arms`, as a `match` holds its scrutinee.
    let matching (value: Var) (arms: Arm list) (resultTy: IrType) =
        let scrutinee = tools.NewVar "__match_tmp_0" MatchTemp value.Type
        Expr.letIn scrutinee (Expr.make (Atom(AVar value)) value.Type) (Expr.make (Match([ scrutinee ], arms)) resultTy)

    let bound (prefix: string) (ty: IrType) = tools.NewVar prefix PatternBound ty

    /// The text of a map's entry, the tuple `entry`, of key and value types `keyTy` and `valueTy`.
    let entryText (entry: Var) (keyTy: Ty) (valueTy: Ty) (k: InterpolationPart list -> Expr) =
        showFields entry [ "0", keyTy; "1", valueTy ] (fun parts -> k [ parts.[0]; Text " => "; parts.[1] ])

    /// The text of the list `list` of `elementTy`s, after `prefix` and in square brackets, each element written by
    /// `first` and the rest by `rest`.
    let listText (list: Var) (elementTy: Ty) (prefix: string) (first: Var -> (InterpolationPart list -> Expr) -> Expr) (rest: Printer) =
        let elementIr = tools.ToIrType elementTy
        let head = bound "__dbg_head" elementIr
        let tail = bound "__dbg_tail" list.Type
        let nonEmpty =
            first head (fun parts ->
                let written = local "__dbg_first" IrString
                let others = local "__dbg_rest" IrString
                Expr.letIn written (text parts) (
                    Expr.letIn others (Expr.make (Call(tools.PrinterOf rest, [ AVar tail; AVar written ])) IrString) (
                        text [ Text $"{prefix}["; Hole(others, HoleString); Text "]" ])))
        matching list
            [ { Patterns = [ PList([], None) ]; Body = Expr.make (Atom(AString $"{prefix}[]")) IrString }
              { Patterns = [ PList([ PBind head ], Some (PBind tail)) ]; Body = nonEmpty } ]
            IrString

    let showElement (elementTy: Ty) (element: Var) (k: InterpolationPart list -> Expr) =
        show elementTy (AVar element) (fun part -> k [ part ])

    // A printer is specialised at the type it prints, as a generic function is at its type arguments, and so is not
    // exported.
    let make (parameters: Var list) (body: Expr) =
        { Name = name
          Source = name
          TypeArguments = [ parameters.Head.Type ]
          Params = parameters
          Result = IrString
          Body = body
          Place = None }

    match printer with
    | ShowingRest(elementTy, entries) ->
        let elementIr = tools.ToIrType elementTy
        let list = tools.NewVar "__dbg_list" Param (IrList elementIr)
        let written = tools.NewVar "__dbg_text" Param IrString
        let head = bound "__dbg_head" elementIr
        let tail = bound "__dbg_tail" list.Type
        let element (k: InterpolationPart list -> Expr) =
            if entries then
                match Unifier.unalias elementTy with
                | TyRecord(fields, _) -> entryText head fields.["0"] fields.["1"] k
                | _ -> internalError "a map's entry that is not a tuple"
            else showElement elementTy head k
        let next =
            element (fun parts ->
                let longer = local "__dbg_text" IrString
                Expr.letIn longer (text (Hole(written, HoleString) :: Text ", " :: parts)) (
                    Expr.make (Call(name, [ AVar tail; AVar longer ])) IrString))
        make
            [ list; written ]
            (matching list
                [ { Patterns = [ PList([], None) ]; Body = Expr.make (Atom(AVar written)) IrString }
                  { Patterns = [ PList([ PBind head ], Some (PBind tail)) ]; Body = next } ]
                IrString)
    | Showing ty ->
        let value = tools.NewVar "__dbg_value" Param (tools.ToIrType ty)
        // The entries or elements of `value`, a map or a set, in a local, as the list the prelude's `toList` gives.
        let listed (collection: Ty) (elementTy: Ty) (k: Var -> Expr) =
            let listTy = Lists.ofType elementTy
            let toList = callee (match collection with Sets.SetType _ -> Sets.toList | _ -> Maps.toList) (TyFunc(collection, listTy, []))
            let elements = local "__dbg_elements" (tools.ToIrType listTy)
            Expr.letIn elements (Expr.make (Call(toList, [ AVar value ])) elements.Type) (k elements)
        let body =
            match constantText ty with
            | Some constant -> Expr.make (Atom(AString constant)) IrString
            | None ->
            match ty with
            | RecursiveNominals.Nominal(nominal, _, shape, _) -> wrapped nominal.Name shape (AVar value)
            | _ ->
            match Unifier.unalias ty with
            | TyPrimitive "int" -> text [ Hole(value, HoleInt) ]
            | SizedIntegerType integer -> text [ Hole(value, HoleSized integer) ]
            | TyPrimitive "bool" -> text [ Hole(value, HoleBool) ]
            | TyPrimitive "float" -> Expr.make (CallRuntime(Helpers.floatToString, [ AVar value ], true)) IrString
            | TyPrimitive "F32" -> Expr.make (CallRuntime(Helpers.f32ToString, [ AVar value ], true)) IrString
            | TyPrimitive "string" -> Expr.make (CallRuntime(Helpers.stringQuote, [ AVar value ], true)) IrString
            // A char is written as a string literal of it.
            | Chars.CharType ->
                let text = tools.NewTemp IrString
                Expr.letIn text (Expr.make (CallRuntime(Helpers.charToString, [ AVar value ], true)) IrString) (Expr.make (CallRuntime(Helpers.stringQuote, [ AVar text ], true)) IrString)
            | TyTag _
            | TyUnion _ as union ->
                let arm (tagName: string, payload: Ty option) =
                    match payload with
                    | None -> { Patterns = [ PTag(tagName, None) ]; Body = Expr.make (Atom(AString $"#{tagName}")) IrString }
                    | Some payloadTy ->
                        let held = bound "__dbg_payload" (tools.ToIrType payloadTy)
                        { Patterns = [ PTag(tagName, Some (PBind held)) ]; Body = wrapped $"#{tagName}" payloadTy (AVar held) }
                match tagsOf union with
                // A union of no tags has no values.
                | [] -> Expr.make (Atom(AString "undefined")) IrString
                | tags -> matching value (tags |> List.map arm) IrString
            | TyRecord(fields, _) when Set.ofSeq fields.Keys = Set.ofList (List.init fields.Count string) ->
                let items = List.init fields.Count (fun index -> string index, fields.[string index])
                showFields value items (fun parts -> text (Text "(" :: separated (parts |> List.map List.singleton) @ [ Text ")" ]))
            | TyRecord(fields, _) ->
                let named = Map.toList fields
                showFields value named (fun parts ->
                    let written = List.map2 (fun (fieldName, _) part -> [ Text $"{fieldName} = "; part ]) named parts
                    text (Text "(" :: separated written @ [ Text ")" ]))
            | TyApply("list", [ elementTy ]) ->
                listText value elementTy "" (showElement elementTy) (ShowingRest(elementTy, false))
            // A map and a set are written as the list their `toList` gives, of their entries or elements, after their
            // type's name.
            | Maps.MapType(keyTy, valueTy) ->
                let entryTy = TyRecord(Map.ofList [ "0", keyTy; "1", valueTy ], FieldOrder.none)
                listed ty entryTy (fun entries ->
                    listText entries entryTy "Map" (fun entry k -> entryText entry keyTy valueTy k) (ShowingRest(entryTy, true)))
            | Sets.SetType elementTy ->
                listed ty elementTy (fun elements -> listText elements elementTy "Set" (showElement elementTy) (ShowingRest(elementTy, false)))
            // An array is written as the list of its elements is, after its type's name.
            | Arrays.ArrayType elementTy ->
                let elements = local "__dbg_elements" (IrList(tools.ToIrType elementTy))
                Expr.letIn elements (Expr.make (CallRuntime(Intrinsics.arrayToList, [ AVar value ], true)) elements.Type) (
                    listText elements elementTy "Array" (showElement elementTy) (ShowingRest(elementTy, false)))
            // A measured value is written as its dimension around the number of default storage units it is:
            // `Time(500000000)`.
            | TyMeasure(dimension, storage) -> wrapped (Dimension.text dimension) storage (AVar value)
            | other -> cannot $"`dbg` of a value of type '{Unifier.tyToString other}'"
        make [ value ] body
