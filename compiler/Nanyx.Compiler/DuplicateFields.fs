/// A record names each of its fields once (#1283): a record type, a record value, a call's named arguments, a
/// `with`'s updates and a record pattern by member. A name written twice would be dropped or overwritten, and any error
/// that followed would come later and somewhere else, so the module is checked for one before anything else is.
/// Positional fields are named by their positions, which cannot repeat, and the fields a spread brings in may be
/// replaced by the fields written after it: `(...defaults, port = 80)`.
module NyxCompiler.DuplicateFields

open Parser.Program

/// A named field: its name, and the node it is written with (its value, type or pattern).
type private Field = Identifier * NodeId * SourceSpan

/// The error at each field of `fields` whose name a field before it has: at the field's name, which the node it is
/// written with declares (see `NamedFieldValue`), or failing that at the node.
let private repeated (declared: DeclaredNames) (fields: Field list) : Diagnostic list =
    let nameSpan (holder: NodeId) (fallback: SourceSpan) =
        declared |> Map.tryFind (holder, NamedFieldValue) |> Option.bind List.tryHead |> Option.map snd |> Option.defaultValue fallback
    fields
    |> List.fold (fun (seen, errors) (name, holder, span) ->
        if Set.contains name seen then
            seen, errors @ [ Diagnostics.at DiagnosticCodes.DuplicateField (nameSpan holder span) $"'{name}' is named twice in this record" ]
        else
            Set.add name seen, errors) (Set.empty, [])
    |> snd

let rec private typesWithin (typeExpr: TypeExpr) : TypeExpr list =
    typeExpr :: (TypeExpr.children typeExpr |> List.collect typesWithin)

/// The named fields of each record value, call's named arguments and `with`'s updates in `expr`.
let private ofExpression (expr: Expression) : Field list list =
    match expr.Kind with
    | RecordExpr fields -> [ fields |> List.choose (function NamedField(name, value) -> Some (name, value.Id, value.Span) | PositionalField _ -> None) ]
    | _ -> []

/// The named fields of each record pattern by member in the arms of `expr`, if it is a match.
let private ofArms (expr: Expression) : Field list list =
    match expr.Kind with
    | Match(_, arms)
    | Except(_, arms) ->
        arms
        |> List.collect fst
        |> List.collect Pattern.within
        |> List.choose (fun pattern ->
            match pattern.Kind with
            | RecordMemberPattern(members, _) -> Some (members |> List.map (fun (name, inner) -> name, inner.Id, inner.Span))
            | _ -> None)
    | _ -> []

/// The named fields of `typeExpr`, if it is a record type.
let private ofType (typeExpr: TypeExpr) : Field list list =
    match typeExpr.Kind with
    | TypeRecord fields
    | TypeOpenRecord(fields, _) ->
        [ fields
          |> List.choose (function
              | TypeField(name, _, Some fieldType, _)
              | TypeMember(name, Some fieldType) -> Some (name, fieldType.Id, fieldType.Span)
              | TypeField _
              | TypeMember _ -> None) ]
    | _ -> []

/// The error for each field of a record in `module'` that a field before it in the record names already. `declared`
/// has where the parser saw each field's name written.
let check (declared: DeclaredNames) (module': Module) : Diagnostic list =
    let expressions = module' |> List.collect SyntaxTree.ofItem |> List.collect SyntaxTree.expressionsWithin
    let types =
        (module' |> List.collect SyntaxTree.topLevelTypes) @ (expressions |> List.collect SyntaxTree.expressionTypes)
        |> List.collect typesWithin
    (expressions |> List.collect ofExpression) @ (expressions |> List.collect ofArms) @ (types |> List.collect ofType)
    |> List.collect (repeated declared)
    |> List.sortBy (fun diagnostic -> diagnostic.Span |> Option.map _.Start |> Option.defaultValue 0)
