/// The printers `dbg` prints its value through: for each concrete type `dbg` is called at, a function that writes a
/// value of the type as text, generated from the type the type checker resolved (see "Printing values" in the docs'
/// work in progress). A printer reads no `ToString` the type has: it shows what the value is.
///
/// A number, a bool and a string are written as a literal of their own is, a string quoted and escaped. A tag, a
/// tuple, a record, a list, a map and a set are written as they would be made, each part through the printer of its
/// own type: `#some(5)`, `(1, "a")`, `(age = 36, name = "Ada")`, `[1, 2]`, `Map["tea" => 3]`, `Set[1, 2]`.
/// A record's fields are in alphabetical order, as a record type keeps no other. A nominal type is written by its name
/// around its shape, `$Money(250)`, inside its module or out; unit is `()`; and a function, which has nothing to show,
/// says that it is one and of what type, `<function int -> string>`.
module internal Transpiler.Wasm.CoreIrLowerDbg

open NyxCompiler
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes

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
    | TyRecord fields when fields.IsEmpty -> Some "()"
    | TyPrimitive "?error" -> Some "???"
    | TyFunc _ -> Some $"<function {Unifier.tyToDisplayString Ascii ty}>"
    | _ -> None

/// Whether the text of a value of `ty` is in brackets of its own, so that a tag or a nominal type around it needs none.
let private bracketed (ty: Ty) =
    match Unifier.unalias ty with
    | TyRecord _
    | TyPrimitive "unit" -> true
    | _ -> false

/// The tags of a union, each once, with their payloads' types. The rest of a union that nothing closed is unit by now,
/// and adds no tags.
let rec private tagsOf (ty: Ty) : (string * Ty option) list =
    match Unifier.unalias ty with
    | TyTag(name, payload) -> [ name, payload ]
    | TyUnion items -> items |> List.collect tagsOf |> List.distinctBy fst
    | _ -> []

/// `ty` as part of a printer's name, such as `list_int` in `__dbg_show_list_int`. Types printed differently are
/// named differently, which their `IrType`s are not: a nominal type is the type of its shape, and a map is a list.
let rec private mangle (ty: Ty) : string =
    let all (tys: Ty list) = tys |> List.map mangle |> String.concat "_"
    match ty with
    | RecursiveAliases.RecursiveAlias _ ->
        match ty with
        | TyAlias(name, args, _) -> $"{name}_{all args}"
        | _ -> internalError "a recursive alias that is not an alias"
    | TyAlias(_, _, underlying) -> mangle underlying
    | TyNominal(nominal, shape, _) -> $"{nominal.Name}_{mangle shape}"
    | TyPrimitive name -> name
    | TyTag(name, None) -> $"tag_{name}"
    | TyTag(name, Some payload) -> $"tag_{name}_{mangle payload}"
    | TyUnion _ ->
        let tag (name: string, payload: Ty option) = payload |> Option.map (fun payload -> $"{name}_{mangle payload}") |> Option.defaultValue name
        "tags_" + (tagsOf ty |> List.map tag |> String.concat "_")
    | TyRecord fields -> "rec_" + (fields |> Map.toList |> List.map (fun (name, fieldTy) -> $"{name}_{mangle fieldTy}") |> String.concat "_")
    | TyApply(name, args) -> $"{name}_{all args}"
    | TyFunc(input, output, _) -> $"fn_{mangle input}_to_{mangle output}"
    | TyVar _
    | TyOpenRecord _ -> "unit"

/// The name a printer is asked for by, and its function's name.
let key (printer: Printer) : string =
    match printer with
    | Showing ty -> $"__dbg_show_{mangle ty}"
    | ShowingRest(ty, false) -> $"__dbg_rest_{mangle ty}"
    | ShowingRest(ty, true) -> $"__dbg_entries_{mangle ty}"

/// The function `name` that is `printer`.
let printerFunction (tools: PrinterTools) (name: string) (printer: Printer) : Function =
    let mk kind ty : Expr = { Kind = kind; Type = ty }
    let text (parts: InterpolationPart list) = mk (Interpolate parts) IrString
    let local (prefix: string) (ty: IrType) = tools.NewVar prefix Local ty
    let bind (var: Var) (value: Expr) (body: Expr) = mk (Let(var, value, body)) body.Type

    /// Passes `k` the text of `value`, a value of `ty`: a local that holds it, or a constant's text.
    let show (ty: Ty) (value: Atom) (k: InterpolationPart -> Expr) : Expr =
        match constantText ty with
        | Some constant -> k (Text constant)
        | None ->
            let written = local "__dbg_part" IrString
            bind written (mk (Call(tools.PrinterOf (Showing ty), [ value ])) IrString) (k (Hole(written, HoleString)))

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
                bind field (mk (Field(AVar record, slot)) field.Type) (show ty (AVar field) (fun part -> next rest (part :: acc)))
        next fields []

    /// `parts`, with `, ` between each two.
    let separated (parts: InterpolationPart list list) =
        parts |> List.mapi (fun index part -> if index = 0 then part else Text ", " :: part) |> List.concat

    /// Matches `value`, a parameter, against `arms`, as a `match` holds its scrutinee.
    let matching (value: Var) (arms: Arm list) (resultTy: IrType) =
        let scrutinee = tools.NewVar "__match_tmp_0" MatchTemp value.Type
        bind scrutinee (mk (Atom(AVar value)) value.Type) (mk (Match([ scrutinee ], arms)) resultTy)

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
                bind written (text parts) (
                    bind others (mk (Call(tools.PrinterOf rest, [ AVar tail; AVar written ])) IrString) (
                        text [ Text $"{prefix}["; Hole(others, HoleString); Text "]" ])))
        matching list
            [ { Patterns = [ PList([], None) ]; Body = mk (Atom(AString $"{prefix}[]")) IrString }
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
          Body = body }

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
                | TyRecord fields -> entryText head fields.["0"] fields.["1"] k
                | _ -> internalError "a map's entry that is not a tuple"
            else showElement elementTy head k
        let next =
            element (fun parts ->
                let longer = local "__dbg_text" IrString
                bind longer (text (Hole(written, HoleString) :: Text ", " :: parts)) (
                    mk (Call(name, [ AVar tail; AVar longer ])) IrString))
        make
            [ list; written ]
            (matching list
                [ { Patterns = [ PList([], None) ]; Body = mk (Atom(AVar written)) IrString }
                  { Patterns = [ PList([ PBind head ], Some (PBind tail)) ]; Body = next } ]
                IrString)
    | Showing ty ->
        let value = tools.NewVar "__dbg_value" Param (tools.ToIrType ty)
        let body =
            match constantText ty with
            | Some constant -> mk (Atom(AString constant)) IrString
            | None ->
            match ty with
            | TyNominal(nominal, shape, _) -> wrapped nominal.Name shape (AVar value)
            | _ ->
            match Unifier.unalias ty with
            | TyPrimitive "int" -> text [ Hole(value, HoleInt) ]
            | SizedIntegerType integer -> text [ Hole(value, HoleSized integer) ]
            | TyPrimitive "bool" -> text [ Hole(value, HoleBool) ]
            | TyPrimitive "float" -> mk (CallRuntime(Helpers.floatToString, [ AVar value ], true)) IrString
            | TyPrimitive "F32" -> mk (CallRuntime(Helpers.f32ToString, [ AVar value ], true)) IrString
            | TyPrimitive "string" -> mk (CallRuntime(Helpers.stringQuote, [ AVar value ], true)) IrString
            | TyTag _
            | TyUnion _ as union ->
                let arm (tagName: string, payload: Ty option) =
                    match payload with
                    | None -> { Patterns = [ PTag(tagName, None) ]; Body = mk (Atom(AString $"#{tagName}")) IrString }
                    | Some payloadTy ->
                        let held = bound "__dbg_payload" (tools.ToIrType payloadTy)
                        { Patterns = [ PTag(tagName, Some (PBind held)) ]; Body = wrapped $"#{tagName}" payloadTy (AVar held) }
                match tagsOf union with
                // A union of no tags has no values.
                | [] -> mk (Atom(AString "undefined")) IrString
                | tags -> matching value (tags |> List.map arm) IrString
            | TyRecord fields when Set.ofSeq fields.Keys = Set.ofList (List.init fields.Count string) ->
                let items = List.init fields.Count (fun index -> string index, fields.[string index])
                showFields value items (fun parts -> text (Text "(" :: separated (parts |> List.map List.singleton) @ [ Text ")" ]))
            | TyRecord fields ->
                let named = Map.toList fields
                showFields value named (fun parts ->
                    let written = List.map2 (fun (fieldName, _) part -> [ Text $"{fieldName} = "; part ]) named parts
                    text (Text "(" :: separated written @ [ Text ")" ]))
            | TyApply("list", [ elementTy ]) ->
                listText value elementTy "" (showElement elementTy) (ShowingRest(elementTy, false))
            // A map and a set are written as a list of their entries or elements, after their type's name.
            | Maps.MapType(keyTy, valueTy) ->
                let entryTy = TyRecord(Map.ofList [ "0", keyTy; "1", valueTy ])
                listText value entryTy "Map" (fun entry k -> entryText entry keyTy valueTy k) (ShowingRest(entryTy, true))
            | Sets.SetType elementTy -> listText value elementTy "Set" (showElement elementTy) (ShowingRest(elementTy, false))
            | other -> cannot $"`dbg` of a value of type {Unifier.tyToString other}"
        make [ value ] body
