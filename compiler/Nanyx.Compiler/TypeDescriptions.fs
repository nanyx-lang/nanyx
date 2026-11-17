/// Type descriptions: what a `match` on a `Type(a)` finds (see `TypeMatches`). Each case of the match is a tag: one per
/// built-in type, `#int` or `#string`, which tells the checker what `a` is; `#list(elem)` and the other collections,
/// whose element type is one the checker never learns; and `#record(r)`, `#union(u)` and `#nominal(n)`, which carry a
/// value of a built-in description type. A record's fields and a union's cases each package a type of their own, which
/// the checker never learns either (see `open'`).
module internal NyxCompiler.TypeDescriptions

open Parser.Program
open NyxCompiler.InferState
open NyxCompiler.TypeValues

/// The description types that package a type of their own.
let private packaging = set [ field; case; someType ]

/// The mark a packaged type's name carries, before the number that makes it its own: `b#12`.
let private mark = '#'

/// A type the checker never learns, named `name` for a reader: a type of its own, which only itself is, so that a
/// value of it can only be given where the same type is expected. It is written as a type with no arguments whose name
/// no other type has.
let skolem (name: string) (state: InferState) : Ty * InferState =
    match freshVar state with
    | TyVar var, next -> TyApply($"{name}{mark}{var.Id}", []), next
    | _ -> failwith "Internal error: a fresh variable is a variable"

/// A type the checker never learns (see `skolem`).
let (|Skolem|_|) (ty: Ty) =
    match ty with
    | TyApply(name, []) when name.Contains mark -> Some name
    | _ -> None

/// The type of a value of the record `fields`, in their order.
let private recordOf (fields: (string * Ty) list) = TyRecord(Map.ofList fields, FieldOrder.ofList fields)

let private stringTy = TyPrimitive "string"

/// What a named type's description says of it besides its structure: its name, its module, the type arguments it is
/// applied to, and its doc comment. A record or union written out has an empty name and module.
let private naming =
    [ "name", stringTy; "module", stringTy; "parameters", Lists.ofType (TyApply(someType, [])); "doc", stringTy ]

/// The members of a value of the description type `name` applied to `args`, a packaged type opened as `packaged`.
let private membersOf (name: string) (args: Ty list) (packaged: Ty) : Map<string, Ty> =
    let described = List.tryHead args |> Option.defaultValue (TyPrimitive "unit")
    let fields =
        match name with
        | n when n = recordInfo -> naming @ [ "fields", Lists.ofType (TyApply(field, [ described ])) ]
        | n when n = unionInfo -> naming @ [ "cases", Lists.ofType (TyApply(case, [ described ])) ]
        | n when n = nominalInfo -> naming
        | n when n = field ->
            [ "name", stringTy; "type", TypeValues.ofType packaged; "get", TyFunc(described, packaged, []); "doc", stringTy ]
        | n when n = case ->
            let payload = TyUnion [ TyTag("some", Some packaged); TyTag("none", None) ]
            [ "name", stringTy; "type", TypeValues.ofType packaged; "get", TyFunc(described, payload, []); "doc", stringTy ]
        | _ -> [ "type", TypeValues.ofType packaged ]
    Map.ofList fields

/// The type of the member `memberName` of a value of the type `ty`, if it is a description type that has the member,
/// and `state` with what it opened. A field, a case or a type argument packages a type: reading a member of one opens
/// it at a type of its own (see `skolem`). Read from the value a name stands for, `symbol`, it is opened once for every
/// member read through that name, so `f.get(value)` and `f.type` agree, as a walker over a record's fields needs.
let memberType (state: InferState) (symbol: SymbolId option) (ty: Ty) (memberName: string) : (Ty * InferState) option =
    match resolveSoFar state ty with
    | TyApply(name, args) when isDescriptionType name ->
        let packaged, next =
            if not (packaging.Contains name) then TyPrimitive "unit", state
            else
                match symbol |> Option.bind (fun symbol -> state.OpenedPackages |> Map.tryFind symbol) with
                | Some opened -> opened, state
                | None ->
                    let opened, next = skolem "b" state
                    match symbol with
                    | Some symbol -> opened, { next with OpenedPackages = next.OpenedPackages |> Map.add symbol opened }
                    | None -> opened, next
        membersOf name args packaged |> Map.tryFind memberName |> Option.map (fun memberTy -> memberTy, next)
    | _ -> None

/// The types the checker never learns that `ty` holds.
let rec skolemsIn (ty: Ty) : Ty list =
    match ty with
    | Skolem _ -> [ ty ]
    | _ -> Ty.children ty |> List.collect skolemsIn

/// Whether `ty` holds the type the checker never learns `skolem`.
let rec private holdsSkolem (skolem: Ty) (ty: Ty) = ty = skolem || Ty.children ty |> List.exists (holdsSkolem skolem)

/// The error for `ty`, what the code at `span` gives, if it holds one of `skolems`, the types the checker never learns
/// that the code opened: one is only the type of the field, case or element it was opened for there, and outside it
/// would let a value of one field's type be given where another's is expected.
let escaping (state: InferState) (skolems: Ty list) (ty: Ty) (span: SourceSpan) : Diagnostic option =
    let resolved = resolveSoFar state ty
    skolems
    |> List.tryFind (fun skolem -> holdsSkolem skolem resolved)
    |> Option.map (fun _ ->
        Diagnostics.at DiagnosticCodes.PackagedTypeEscapes span
            $"This gives a value of '{Unifier.tyToDisplayString Ascii resolved}', whose type is the type of a field, a case or an element that is only known here: use it here, as in `walk(f.get(value), f.type)`, rather than giving it back")

/// The error for the lambda whose parameters are `parameters` giving back a value of the type `ty`, at `span`, if that
/// holds a type one of the parameters opened (see `memberType`).
let escapingParameters (state: InferState) (parameters: SymbolId option list) (ty: Ty) (span: SourceSpan) : Diagnostic option =
    let opened = parameters |> List.choose (Option.bind (fun symbol -> state.OpenedPackages |> Map.tryFind symbol))
    if opened.IsEmpty then None else escaping state opened ty span

/// Whether a value of the type `ty` is a description whose member `memberName` is a function, which a call of the
/// member calls, as `f.get(value)` does (see `FieldCalls`).
let holdsFunction (state: InferState) (ty: Ty) (memberName: string) =
    match memberType state None ty memberName with
    | Some (TyFunc _, _) -> true
    | _ -> false
