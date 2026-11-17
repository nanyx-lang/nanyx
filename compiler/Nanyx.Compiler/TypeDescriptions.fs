/// Type descriptions: what a `match` on a `Type(a)` finds (see `TypeMatches`). Each case of the match is a tag: one per
/// built-in type, `#int` or `#string`, which tells the checker what `a` is; `#list(elem)` and the other collections,
/// whose element type is one the checker never learns; and `#record(r)`, `#union(u)` and `#nominal(n)`, which carry a
/// value of a built-in description type. A record's fields and a union's cases each package a type of their own, which
/// the checker never learns either (see `memberType`).
module internal NyxCompiler.TypeDescriptions

open Parser.Program
open NyxCompiler.InferState
open NyxCompiler.TypeValues

/// A type the checker never learns, named `name` for a reader: a type of its own, which only itself is, so that a
/// value of it can only be given where the same type is expected. It is written as a type with no arguments whose name
/// no other type has (see `TypeValues.PackagedType`).
let skolem (name: string) (state: InferState) : Ty * InferState =
    match freshVar state with
    | TyVar var, next -> TyApply($"{name}{packagedMark}{var.Id}", []), next
    | _ -> failwith "Internal error: a fresh variable is a variable"

/// A type the checker never learns (see `skolem`).
let (|Skolem|_|) (ty: Ty) =
    match ty with
    | PackagedType id -> Some id
    | _ -> None

/// The type of the member `memberName` of a value of the type `ty`, if it is a description type that has the member,
/// and `state` with what it opened. A field, a case or a type argument packages a type: reading a member of one opens
/// it at a type of its own (see `skolem`). Read from the value a name stands for, `symbol`, it is opened once for every
/// member read through that name, so `f.get(value)` and `f.type` agree, as a walker over a record's fields needs. A field
/// `build` gives its function is already open, at the type it is given as a second argument (see `build`).
///
/// A record's `build` makes a value of the record from a function that gives each field's value, or fails. That function
/// must work for every field's type, which users never write: it is given a field opened at a type of its own, made
/// for this `build`, and must give a value of that type, so it gives one of any field's type. Its failure is the
/// `build`'s, at the first field it fails on.
let memberType (state: InferState) (symbol: SymbolId option) (ty: Ty) (memberName: string) : (Ty * InferState) option =
    match resolveSoFar state ty with
    | TyApply(name, [ described; opened ]) when name = field ->
        descriptionMembers name [ described ] opened |> List.tryFind (fst >> (=) memberName) |> Option.map (fun (_, memberTy) -> memberTy, state)
    | TyApply(name, [ described ]) when name = recordInfo && memberName = "build" ->
        let anyField, next = skolem "b" state
        let failure, next = freshVar next
        let given = TyFunc(TyApply(field, [ described; anyField ]), TyUnion [ TyTag("some", Some anyField); failure ], [])
        Some (TyFunc(given, TyUnion [ TyTag("some", Some described); failure ], []), next)
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
        descriptionMembers name args packaged |> List.tryFind (fst >> (=) memberName) |> Option.map (fun (_, memberTy) -> memberTy, next)
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
