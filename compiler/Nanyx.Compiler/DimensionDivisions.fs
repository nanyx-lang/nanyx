/// A def that divides or multiplies two of its parameters and writes neither their types nor its own, `def speed =
/// { d, t -> d / t }`, reads the operator through `Num`, whose `/` and `*` take their operands as one type, so a call
/// with two dimensions, `speed(100cm, 2s)`, is a mismatch (#2880), and one with two values of one dimension a missing
/// `Num`, which a dimension doesn't have (#3238). Each says why, and gives the def with the parameters' dimensions
/// written, which divides any two: inferring a quotient's dimensions from an unwritten one is an open question (see the
/// docs' Units and dimensions work-in-progress page).
module internal NyxCompiler.DimensionDivisions

open Parser.Program
open NyxCompiler.InferState

/// Where a def divides or multiplies two of its parameters: the operator, where it is written, the positions of the
/// parameters it takes, the def's parameters, and its lambda's body.
type private Division =
    { Operator: string
      At: SourceSpan
      Left: int
      Right: int
      Parameters: Identifier list
      Body: Expression }

/// The first division or multiplication of two parameters of each top-level def in `items` that writes neither its type
/// nor its parameters', by the def's name.
let private divisions (items: TopLevelItem list) : Map<Identifier, Division> =
    let rec defsOf (item: TopLevelItem) =
        match item.Kind with
        | Def (ValueDef(_, name, None, Kind (Lambda(args, body, _)))) when not args.IsEmpty && args |> List.forall (snd >> Option.isNone) ->
            [ name, args |> List.map fst, body ]
        | ModuleBlock(_, items) -> items |> List.collect defsOf
        | _ -> []
    items
    |> List.collect defsOf
    |> List.choose (fun (name, parameters, body) ->
        SyntaxTree.expressionsWithin body
        |> List.tryPick (fun expr ->
            match expr.Kind with
            | BinaryOp(("/" | "*") as op, opSpan, Kind (IdentifierExpr left), Kind (IdentifierExpr right)) when left <> right ->
                match List.tryFindIndex ((=) left) parameters, List.tryFindIndex ((=) right) parameters with
                | Some leftAt, Some rightAt ->
                    Some (name, { Operator = op; At = opSpan |> Option.defaultValue expr.Span; Left = leftAt; Right = rightAt; Parameters = parameters; Body = body })
                | _ -> None
            | _ -> None))
    |> Map.ofList

/// The parameters of `division`, of the def `callee`, written with the types `leftTy` and `rightTy` for the two it
/// divides, as the def a message shows: its body as `source` has it, or `...` for one that is long or of several lines.
let private writtenDef (source: string) (typeText: Ty -> string) (callee: Identifier) (division: Division) (leftTy: Ty) (rightTy: Ty) =
    let written =
        division.Parameters
        |> List.mapi (fun index parameter ->
            if index = division.Left then $"{parameter}: {typeText leftTy}"
            elif index = division.Right then $"{parameter}: {typeText rightTy}"
            else parameter)
        |> String.concat ", "
    let body =
        let text = SourceSpan.textIn source division.Body.Span
        if SourceSpan.isNone division.Body.Span || text.Contains '\n' || text.Length > 60 then "..." else text
    $"`def {callee} = {{ {written} -> {body} }}`"

/// The verb a message says `division` does: `divide` or `multiply`.
let private verbOf (division: Division) = if division.Operator = "/" then "divide" else "multiply"

/// The dimension and type of each of `args` at the positions `division` divides, as `typeOf` resolves them, when both
/// are measured.
let private measuredPair (typeOf: NodeId -> Ty option) (division: Division) (args: Expression list) =
    let measured (index: int) =
        if index >= args.Length then None
        else
            match typeOf args.[index].Id |> Option.map Unifier.unalias with
            | Some (TyMeasure(dimension, _) as ty) -> Some (dimension, ty)
            | _ -> None
    match measured division.Left, measured division.Right with
    | Some left, Some right -> Some (left, right)
    | _ -> None

/// The mismatch reported at `span`, an argument of a call, if the def it is given to divides or multiplies it with
/// another argument of a different dimension, as `typeOf` resolves their types: the def's parameters taken as one type
/// because neither is written, and the def with their dimensions written, which `typeText` writes and `source` gives
/// the body of.
let diagnostic
    (state: InferState)
    (items: TopLevelItem list)
    (source: string)
    (typeOf: NodeId -> Ty option)
    (typeText: Ty -> string)
    (span: SourceSpan)
    : Diagnostic option =
    state.ArgumentCallees
    |> Map.tryFind span
    |> Option.bind (fun (callee, args) ->
        divisions items
        |> Map.tryFind callee
        |> Option.bind (fun division ->
            match measuredPair typeOf division args with
            | Some ((leftDimension, leftTy), (rightDimension, rightTy)) when leftDimension <> rightDimension ->
                let verb = verbOf division
                let left, right = division.Parameters.[division.Left], division.Parameters.[division.Right]
                let message =
                    $"'{callee}' takes '{left}' and '{right}' as one type, since it {verb}s them with `{division.Operator}` and doesn't write their types, "
                    + $"but is given a '{typeText leftTy}' and a '{typeText rightTy}'. To {verb} two dimensions, write the parameters' types: "
                    + writtenDef source typeText callee division leftTy rightTy
                Diagnostics.at DiagnosticCodes.TypeMismatch span message
                |> Diagnostics.withRelated division.At $"the `{division.Operator}` that takes its operands as one type"
                |> Some
            | _ -> None))

/// `diagnostic`, a missing `Num` at a call of a def that divides or multiplies two of its parameters with their types
/// unwritten, given two values of one dimension: a dimension has no `Num`, whose `*` would make `m * m` a length, so the
/// def's `/` can't take them, and the error shows the def with their types written, whose `/` divides a dimension's
/// values (#3238). Any other diagnostic is as it is.
let withoutNum
    (items: TopLevelItem list)
    (source: string)
    (typeOf: NodeId -> Ty option)
    (typeText: Ty -> string)
    (diagnostic: Diagnostic)
    : Diagnostic =
    let missingNum =
        diagnostic.Code = DiagnosticCodes.MissingContextMembers.Code
        && diagnostic.Subject |> Option.exists (fun subject -> subject = "Num" || subject.StartsWith "Num(")
    match diagnostic.Span with
    | Some span when missingNum ->
        let known = divisions items
        items
        |> List.collect SyntaxTree.ofItem
        |> List.collect SyntaxTree.expressionsWithin
        |> List.tryPick (fun expr ->
            match expr.Kind with
            | FunctionCall(callee, calleeSpan, args) when SyntaxTree.nameSpan calleeSpan expr = span ->
                known |> Map.tryFind callee |> Option.map (fun division -> callee, division, SyntaxTree.writtenArguments args)
            | _ -> None)
        |> Option.bind (fun (callee, division, args) ->
            match measuredPair typeOf division args with
            | Some ((leftDimension, leftTy), (rightDimension, rightTy)) when leftDimension = rightDimension ->
                let verb = verbOf division
                let left, right = division.Parameters.[division.Left], division.Parameters.[division.Right]
                let message =
                    $"'{callee}' {verb}s '{left}' and '{right}' with `{division.Operator}` and doesn't write their types, so it reads `{division.Operator}` "
                    + $"through 'Num', which a dimension doesn't have: here both are '{typeText leftTy}'. To {verb} values of a dimension, "
                    + $"write the parameters' types: {writtenDef source typeText callee division leftTy rightTy}"
                Some
                    { diagnostic with
                        Message = message
                        Related = diagnostic.Related @ [ division.At, $"the `{division.Operator}` read through 'Num'" ] }
            | _ -> None)
        |> Option.defaultValue diagnostic
    | _ -> diagnostic
