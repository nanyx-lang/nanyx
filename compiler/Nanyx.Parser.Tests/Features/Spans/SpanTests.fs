module ParserTests.Features.Spans

open System.IO
open Xunit
open FsUnit.Xunit
open Parser.Program

let private parse (source: string) =
    match parseModuleFile "t.nyx" source with
    | Result.Ok module' -> module'
    | Result.Error err -> failwith $"Parse failed: {err}"

/// The value of the top-level `def name = ...` in `module'`.
let private valueOf name (module': Module) =
    module'
    |> List.tryPick (function
        | Kind (Def (ValueDef (_, n, _, value))) when n = name -> Some value
        | _ -> None)
    |> Option.defaultWith (fun () -> failwith $"No def {name} in %A{module'}")

/// The source text a span covers.
let private textOf (source: string) (span: SourceSpan) = source.Substring(span.Start, span.End - span.Start)

let private someSpan (span: SourceSpan option) =
    match span with
    | Some span -> span
    | None -> failwith "Expected a span"

[<Fact>]
let ``an identifier spans its name`` () =
    let source = "def x = someValue"
    let value = parse source |> valueOf "x"
    textOf source value.Span |> should equal "someValue"
    value.Span.File |> should equal "t.nyx"
    value.Span.Start |> should equal 8
    value.Span.StartLine |> should equal 0
    value.Span.StartColumn |> should equal 8

[<Fact>]
let ``a call spans from its name to the closing paren, and its arguments span the tuple inside`` () =
    let source = "def x = foo(1, bar)"
    let value = parse source |> valueOf "x"
    textOf source value.Span |> should equal "foo(1, bar)"
    match value.Kind with
    | FunctionCall ("foo", nameSpan, [ args ]) ->
        textOf source (someSpan nameSpan) |> should equal "foo"
        textOf source args.Span |> should equal "1, bar"
        match args.Kind with
        | TupleExpr [ one; bar ] ->
            textOf source one.Span |> should equal "1"
            textOf source bar.Span |> should equal "bar"
            bar.Span.StartColumn |> should equal 15
        | other -> failwith $"Expected a tuple of two arguments, got %A{other}"
    | other -> failwith $"Expected a call to foo, got %A{other}"

[<Fact>]
let ``trailing whitespace is not part of a span`` () =
    let source = "def x = foo(1)   \ndef y = a + b   \ndef z = 2"
    let module' = parse source
    textOf source (valueOf "x" module').Span |> should equal "foo(1)"
    textOf source (valueOf "y" module').Span |> should equal "a + b"
    // At the end of the file too (the parser trims the source, so the span stays within it).
    let atEnd = "def x = foo(1)   "
    textOf atEnd (parse atEnd |> valueOf "x").Span |> should equal "foo(1)"

[<Fact>]
let ``a binary operator chain spans from its first operand to its last`` () =
    let source = "def x = a + b * c - d"
    let value = parse source |> valueOf "x"
    textOf source value.Span |> should equal "a + b * c - d"
    match value.Kind with
    | BinaryOp ("-", left, right) ->
        textOf source left.Span |> should equal "a + b * c"
        textOf source right.Span |> should equal "d"
        match left.Kind with
        | BinaryOp ("+", _, product) -> textOf source product.Span |> should equal "b * c"
        | other -> failwith $"Expected an addition, got %A{other}"
    | other -> failwith $"Expected a subtraction, got %A{other}"

[<Fact>]
let ``not spans from the keyword to its operand`` () =
    let source = "def x = not d\ndef y = not   a == b"
    let module' = parse source
    let x = valueOf "x" module'
    textOf source x.Span |> should equal "not d"
    x.Span.StartColumn |> should equal 8
    let y = valueOf "y" module'
    match y.Kind with
    | BinaryOp ("==", notA, _) ->
        textOf source notA.Span |> should equal "not   a"
        notA.Span.StartLine |> should equal 1
        notA.Span.StartColumn |> should equal 8
    | other -> failwith $"Expected an equality, got %A{other}"

[<Fact>]
let ``member access spans the whole chain, and its field span is the field name`` () =
    let source = "def x = a.b.c"
    let value = parse source |> valueOf "x"
    textOf source value.Span |> should equal "a.b.c"
    match value.Kind with
    | MemberAccess (inner, "c", fieldSpan) ->
        textOf source (someSpan fieldSpan) |> should equal "c"
        textOf source inner.Span |> should equal "a.b"
        match inner.Kind with
        | MemberAccess (a, "b", innerFieldSpan) ->
            textOf source (someSpan innerFieldSpan) |> should equal "b"
            textOf source a.Span |> should equal "a"
        | other -> failwith $"Expected a.b, got %A{other}"
    | other -> failwith $"Expected a member access, got %A{other}"

[<Fact>]
let ``a pipe chain spans from its input to its last target, and a lambda spans its braces`` () =
    let source = "def x = xs \\map { x -> x + 1 } \\sum"
    let value = parse source |> valueOf "x"
    textOf source value.Span |> should equal "xs \\map { x -> x + 1 } \\sum"
    match value.Kind with
    | Pipe (inner, "sum", sumSpan, []) ->
        textOf source (someSpan sumSpan) |> should equal "sum"
        textOf source inner.Span |> should equal "xs \\map { x -> x + 1 }"
        match inner.Kind with
        | Pipe (xs, "map", mapSpan, [ lambda ]) ->
            textOf source (someSpan mapSpan) |> should equal "map"
            textOf source xs.Span |> should equal "xs"
            textOf source lambda.Span |> should equal "{ x -> x + 1 }"
            match lambda.Kind with
            | Lambda ([ ("x", None) ], body) -> textOf source body.Span |> should equal "x + 1"
            | other -> failwith $"Expected a lambda, got %A{other}"
        | other -> failwith $"Expected the map pipe, got %A{other}"
    | other -> failwith $"Expected the sum pipe, got %A{other}"

[<Fact>]
let ``an if spans from the if keyword to its last branch`` () =
    let source = "def x = if a then 1 else 2"
    let value = parse source |> valueOf "x"
    textOf source value.Span |> should equal "if a then 1 else 2"
    value.Span.StartColumn |> should equal 8
    match value.Kind with
    | IfExpr (cond, thenExpr, elseExpr) ->
        textOf source cond.Span |> should equal "a"
        textOf source thenExpr.Span |> should equal "1"
        textOf source elseExpr.Span |> should equal "2"
    | other -> failwith $"Expected an if, got %A{other}"

[<Fact>]
let ``spans in a multi-line lambda have the lines and columns they were written at`` () =
    let source =
        "def f = { x ->\n" +
        "  def y = x + 1\n" +
        "  y * 2\n" +
        "}"
    let value = parse source |> valueOf "f"
    textOf source value.Span |> should equal source.[8..]
    value.Span.StartLine |> should equal 0
    value.Span.StartColumn |> should equal 8
    match value.Kind with
    | Lambda ([ ("x", None) ], Kind (Block [ Kind (DefStatement (_, "y", None, yValue)); Kind (ExprStatement result) ])) ->
        textOf source yValue.Span |> should equal "x + 1"
        yValue.Span.StartLine |> should equal 1
        yValue.Span.StartColumn |> should equal 10
        textOf source result.Span |> should equal "y * 2"
        result.Span.StartLine |> should equal 2
        result.Span.StartColumn |> should equal 2
    | other -> failwith $"Expected a lambda with a two-statement block, got %A{other}"

/// Every expression in a module, including those inside patterns and types.
let rec private expressionsOfKind (kind: ExpressionKind) : Expression list =
    match kind with
    | LiteralExpr _
    | UnitExpr
    | IdentifierExpr _
    | ContextMemberAccess _ -> []
    | FunctionCall (_, _, args) -> args |> List.collect expressions
    | Lambda (parameters, body) ->
        (parameters |> List.collect (snd >> Option.toList >> List.collect expressionsOfType)) @ expressions body
    | BinaryOp (_, left, right) -> expressions left @ expressions right
    | Pipe (value, _, _, args) -> expressions value @ (args |> List.collect expressions)
    | Block statements -> statements |> List.collect expressionsOfStatement
    | Match (values, arms) ->
        (values |> List.collect expressions)
        @ (arms |> List.collect (fun (patterns, body) -> (patterns |> List.collect expressionsOfPattern) @ expressions body))
    | Except (value, arms) -> expressionsOfKind (Match ([ value ], arms))
    | TupleExpr items
    | ListExpr items -> items |> List.collect expressions
    | RecordExpr fields ->
        fields |> List.collect (function
            | NamedField (_, value)
            | PositionalField value -> expressions value)
    | TagExpr (_, payload) -> payload |> Option.toList |> List.collect expressions
    | IfExpr (cond, ifTrue, ifFalse) -> expressions cond @ expressions ifTrue @ expressions ifFalse
    | MemberAccess (value, _, _) -> expressions value
    | ContextMemberCall (_, _, args) -> args |> List.collect expressions
    | UseIn (binding, body) -> expressionsOfUse binding @ expressions body
    | WorkflowBindExpr (_, value)
    | WorkflowReturnExpr value -> expressions value
    | InterpolatedString parts ->
        parts |> List.collect (function
            | StringText _ -> []
            | StringExpr value -> expressions value)

and private expressions (expr: Expression) : Expression list = expr :: expressionsOfKind expr.Kind

and private expressionsOfStatement (statement: Statement) =
    match statement.Kind with
    | DefStatement (_, _, typeOpt, value) ->
        (typeOpt |> Option.toList |> List.collect expressionsOfType) @ expressions value
    | TypeDefStatement (_, _, parameters, typeExpr) ->
        (parameters |> List.collect (snd >> Option.toList >> List.collect expressionsOfType)) @ expressionsOfType typeExpr
    | ImportStatement _ -> []
    | UseStatement binding -> expressionsOfUse binding
    | ExprStatement value -> expressions value

and private expressionsOfUse (binding: UseBinding) =
    match binding with
    | UseValue value -> expressions value
    | UseContextInstance (typeExpr, value) -> expressionsOfType typeExpr @ expressions value

and private expressionsOfPattern (pattern: Pattern) =
    match pattern.Kind with
    | LiteralPattern _
    | IdentifierPattern _
    | WildcardPattern
    | UnitPattern
    | ElsePattern -> []
    | TuplePattern patterns
    | AndPattern patterns
    | RecordPattern (_, patterns) -> patterns |> List.collect expressionsOfPattern
    | RecordMemberPattern members -> members |> List.collect (snd >> expressionsOfPattern)
    | ListPattern (patterns, rest) -> (patterns @ Option.toList rest) |> List.collect expressionsOfPattern
    | ListSplatMiddle (before, after) -> (before @ after) |> List.collect expressionsOfPattern
    | RangePattern (low, high) -> expressions low @ expressions high
    | GuardPattern predicate -> expressions predicate
    | TagPattern (_, payload) -> payload |> Option.toList |> List.collect expressionsOfPattern

and private expressionsOfType (typeExpr: TypeExpr) =
    match typeExpr.Kind with
    | TypeName _
    | TypeVar _
    | TypeUnit -> []
    | TypeTuple types
    | TypeUnion types
    | TypeIntersection types
    | TypeContext types
    | TypeApply (_, types) -> types |> List.collect expressionsOfType
    | TypeRecord fields ->
        fields |> List.collect (function
            | TypeField (_, _, _, fieldType, defaultValue) ->
                (fieldType |> Option.toList |> List.collect expressionsOfType)
                @ (defaultValue |> Option.toList |> List.collect expressions)
            | TypeMember (_, memberType) -> memberType |> Option.toList |> List.collect expressionsOfType)
    | TypeFunc (input, output) -> expressionsOfType input @ expressionsOfType output
    | TypeTag (_, payload) -> payload |> Option.toList |> List.collect expressionsOfType
    | TypeOptional inner -> expressionsOfType inner
    | TypeWithContext (contexts, inner) -> (contexts |> List.collect expressionsOfType) @ expressionsOfType inner
    | TypeConstraint (_, constrained, value) -> expressionsOfType constrained @ expressions value
    | TypeWhere (constrained, value) -> expressionsOfType constrained @ expressions value
    | TypeOf value -> expressions value

let rec private expressionsOfItem (item: TopLevelItem) =
    match item.Kind with
    | ModuleDecl _
    | Import _
    | Attribute _ -> []
    | ModuleBlock (_, items) -> items |> List.collect expressionsOfItem
    | Def (ValueDef (_, _, typeOpt, value)) -> (typeOpt |> Option.toList |> List.collect expressionsOfType) @ expressions value
    | Def (TypeDef (_, _, parameters, typeExpr)) ->
        (parameters |> List.collect (snd >> Option.toList >> List.collect expressionsOfType)) @ expressionsOfType typeExpr
    | Expr value -> expressions value

/// `pattern` and every pattern nested in it.
let rec private patternsWithin (pattern: Pattern) : Pattern list =
    pattern ::
        match pattern.Kind with
        | TuplePattern patterns
        | AndPattern patterns
        | RecordPattern (_, patterns) -> patterns |> List.collect patternsWithin
        | RecordMemberPattern members -> members |> List.collect (snd >> patternsWithin)
        | ListPattern (patterns, rest) -> (patterns @ Option.toList rest) |> List.collect patternsWithin
        | ListSplatMiddle (before, after) -> (before @ after) |> List.collect patternsWithin
        | TagPattern (_, payload) -> payload |> Option.toList |> List.collect patternsWithin
        | LiteralPattern _
        | IdentifierPattern _
        | WildcardPattern
        | UnitPattern
        | ElsePattern
        | RangePattern _
        | GuardPattern _ -> []

/// Every pattern in the arms of `expr`, if it is a match.
let private armPatterns (expr: Expression) =
    match expr.Kind with
    | Match (_, arms) -> arms |> List.collect (fst >> List.collect patternsWithin)
    | _ -> []

/// The statements of `expr`, if it is a block.
let private blockStatements (expr: Expression) =
    match expr.Kind with
    | Block statements -> statements
    | _ -> []

/// The types annotating parameters.
let private parameterTypes (parameters: (Identifier * TypeExpr option) list) = parameters |> List.collect (snd >> Option.toList)

let private typesOfUse (binding: UseBinding) =
    match binding with
    | UseContextInstance (contextType, _) -> [ contextType ]
    | UseValue _ -> []

/// The type expressions an expression itself is written with (not those of the expressions in it).
let private typesOfExpression (expr: Expression) =
    match expr.Kind with
    | Lambda (parameters, _) -> parameterTypes parameters
    | ContextMemberAccess (contextType, _)
    | ContextMemberCall (contextType, _, _) -> [ contextType ]
    | UseIn (binding, _) -> typesOfUse binding
    | _ -> []

/// The type expressions a statement itself is written with.
let private typesOfStatement (statement: Statement) =
    match statement.Kind with
    | DefStatement (_, _, typeOpt, _) -> Option.toList typeOpt
    | TypeDefStatement (_, _, parameters, body) -> parameterTypes parameters @ [ body ]
    | UseStatement binding -> typesOfUse binding
    | ImportStatement _
    | ExprStatement _ -> []

/// The type expressions a top-level item is written with, and those of the items in a module block.
let rec private typesOfItem (item: TopLevelItem) =
    match item.Kind with
    | Def (ValueDef (_, _, typeOpt, _)) -> Option.toList typeOpt
    | Def (TypeDef (_, _, parameters, body)) -> parameterTypes parameters @ [ body ]
    | ModuleBlock (_, items) -> items |> List.collect typesOfItem
    | ModuleDecl _
    | Import _
    | Expr _
    | Attribute _ -> []

/// `typeExpr` and every type expression nested in it.
let rec private typesWithin (typeExpr: TypeExpr) : TypeExpr list =
    typeExpr ::
        match typeExpr.Kind with
        | TypeName _
        | TypeVar _
        | TypeUnit
        | TypeOf _ -> []
        | TypeTuple types
        | TypeUnion types
        | TypeIntersection types
        | TypeContext types
        | TypeApply (_, types) -> types |> List.collect typesWithin
        | TypeRecord fields ->
            fields |> List.collect (function
                | TypeField (_, _, _, fieldType, _)
                | TypeMember (_, fieldType) -> fieldType |> Option.toList |> List.collect typesWithin)
        | TypeFunc (input, output) -> typesWithin input @ typesWithin output
        | TypeTag (_, payload) -> payload |> Option.toList |> List.collect typesWithin
        | TypeOptional inner
        | TypeConstraint (_, inner, _)
        | TypeWhere (inner, _) -> typesWithin inner
        | TypeWithContext (contexts, inner) -> (contexts |> List.collect typesWithin) @ typesWithin inner

/// `item` and the items of the module block it is, if it is one.
let rec private itemsWithin (item: TopLevelItem) : TopLevelItem list =
    item ::
        match item.Kind with
        | ModuleBlock (_, items) -> items |> List.collect itemsWithin
        | _ -> []

/// Checks that every top-level item, expression, pattern, statement and type expression parsed from `source` has a
/// span within it that starts and ends on a non-whitespace character, whose line and column agree with its offset.
/// Returns how many nodes it checked.
let private checkAllSpans (file: string) (rawSource: string) =
    // Spans are offsets into the source as the parser normalises it.
    let source = rawSource.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd()
    let module' =
        match parseModuleFile file rawSource with
        | Result.Ok module' -> module'
        | Result.Error err -> failwith $"Parse failed for {file}: {err}"
    let expressions = module' |> List.collect expressionsOfItem
    let patterns = expressions |> List.collect armPatterns
    let statements = expressions |> List.collect blockStatements
    let types =
        (module' |> List.collect typesOfItem)
        @ (expressions |> List.collect typesOfExpression)
        @ (statements |> List.collect typesOfStatement)
        |> List.collect typesWithin
    let check (span: SourceSpan) (describe: unit -> string) =
        if SourceSpan.isNone span then failwith $"No span: {describe ()}"
        if span.File <> file then failwith $"Wrong file: {describe ()}"
        if span.Start < 0 || span.End > source.Length || span.End <= span.Start then
            failwith $"Span outside the source or empty: {describe ()}"
        if System.Char.IsWhiteSpace source.[span.Start] || System.Char.IsWhiteSpace source.[span.End - 1] then
            failwith $"Span starts or ends with whitespace ('{textOf source span}'): {describe ()}"
        let before = source.Substring(0, span.Start)
        let line = before |> Seq.filter ((=) '\n') |> Seq.length
        let column = span.Start - (before.LastIndexOf('\n') + 1)
        if span.StartLine <> line || span.StartColumn <> column then
            failwith $"Line/column {span.StartLine}:{span.StartColumn} should be {line}:{column}: {describe ()}"
    for expr in expressions do
        check expr.Span (fun () -> $"{file}: %A{expr.Span} of %A{expr.Kind}")
    for pattern in patterns do
        check pattern.Span (fun () -> $"{file}: %A{pattern.Span} of pattern %A{pattern.Kind}")
    for statement in statements do
        check statement.Span (fun () -> $"{file}: %A{statement.Span} of statement %A{statement.Kind}")
    for typeExpr in types do
        check typeExpr.Span (fun () -> $"{file}: %A{typeExpr.Span} of type %A{typeExpr.Kind}")
    let items = module' |> List.collect itemsWithin
    for item in items do
        check item.Span (fun () -> $"{file}: %A{item.Span} of item %A{item.Kind}")
    items.Length + expressions.Length + patterns.Length + statements.Length + types.Length

[<Fact>]
let ``an operator over parentheses or a lone spread spans the delimiters, and what is inside keeps its own span`` () =
    let source = "def x = (1 + 2) * 3\ndef y = [...xs] ++ ys\ndef z = (a + b).c \\f\ndef w = not (n)"
    let module' = parse source
    let x = valueOf "x" module'
    textOf source x.Span |> should equal "(1 + 2) * 3"
    match x.Kind with
    | BinaryOp ("*", sum, _) -> textOf source sum.Span |> should equal "1 + 2"
    | other -> failwith $"Expected a product, got %A{other}"
    textOf source (valueOf "y" module').Span |> should equal "[...xs] ++ ys"
    let z = valueOf "z" module'
    textOf source z.Span |> should equal "(a + b).c \\f"
    match z.Kind with
    | Pipe (access, "f", _, []) -> textOf source access.Span |> should equal "(a + b).c"
    | other -> failwith $"Expected a pipe, got %A{other}"
    let w = valueOf "w" module'
    textOf source w.Span |> should equal "not (n)"
    match w.Kind with
    | FunctionCall ("not", None, [ n ]) -> textOf source n.Span |> should equal "n"
    | other -> failwith $"Expected not, got %A{other}"

[<Fact>]
let ``a block ends at its last expression, not at a comment after it`` () =
    let source = "def f = { x ->\n  def y = x + 1\n  y * 2 -- doubled\n}"
    match (parse source |> valueOf "f").Kind with
    | Lambda (_, body) -> textOf source body.Span |> should equal "def y = x + 1\n  y * 2"
    | other -> failwith $"Expected a lambda, got %A{other}"
    // `-` does not take the first dash of a comment.
    let topLevel = "def a = 5 - 1 -- four"
    textOf topLevel (parse topLevel |> valueOf "a").Span |> should equal "5 - 1"

[<Fact>]
let ``expressions are equal wherever they and the names in them were written`` () =
    let first = parse "def x = foo(a).b \\bar" |> valueOf "x"
    let second = parse "def x =     foo(a).b  \\bar" |> valueOf "x"
    first.Span |> should not' (equal second.Span)
    first |> should equal second
    hash first |> should equal (hash second)
    compare first second |> should equal 0

[<Fact>]
let ``nodes made up for for-arms, record updates, tag payloads and multi-branch ifs start on their own text`` () =
    let source =
        "def a = for xs\n  | 1 -> f(1)\n  | _ -> g()\n" +
        "def b = alice with age = 31\n" +
        "def c = #pair( 1, 2)\n" +
        "def d = if\n  | p -> 1\n  | q -> 2\n  | else -> 3\n"
    checkAllSpans "t.nyx" source |> should be (greaterThan 10)
    let module' = parse source
    match (valueOf "b" module').Kind with
    | FunctionCall ("__with", None, [ _; updates ]) -> textOf source updates.Span |> should equal "age = 31"
    | other -> failwith $"Expected a record update, got %A{other}"
    match (valueOf "d" module').Kind with
    | IfExpr (_, _, nested) -> textOf source nested.Span |> should equal "| q -> 2\n  | else -> 3"
    | other -> failwith $"Expected an if, got %A{other}"

[<Fact>]
let ``every expression in the comprehensive sample has a real span within the source`` () =
    // The parser gives the nodes it makes up when desugaring (shorthand lambda parameters, `else ()`, `__range`,
    // `__with`, `__for`, destructuring temporaries, ...) the span of the construct they came from, so every node
    // has one.
    let path = Path.Combine(__SOURCE_DIRECTORY__, "..", "Integration", "comprehensive.nyx")
    checkAllSpans path (File.ReadAllText path) |> should be (greaterThan 20)

[<Fact>]
let ``every expression in every parseable feature file has a real span within the source`` () =
    let featureRoot = Path.Combine(__SOURCE_DIRECTORY__, "..")
    let checkedFiles =
        Directory.GetFiles(featureRoot, "*.nyx", SearchOption.AllDirectories)
        |> Array.filter (fun path -> parseModuleFile path (File.ReadAllText path) |> Result.isOk)
    checkedFiles.Length |> should be (greaterThan 50)
    for path in checkedFiles do
        checkAllSpans path (File.ReadAllText path) |> ignore

/// The patterns of each arm of the match that is `def name`'s value.
let private armsOf name (module': Module) =
    match (valueOf name module').Kind with
    | Match (_, arms) -> arms |> List.map fst
    | other -> failwith $"Expected a match, got %A{other}"

[<Fact>]
let ``each pattern spans its text, and nested patterns span theirs`` () =
    let source =
        "def f = match v\n" +
        "  | #some((a, _)) -> a\n" +
        "  | [x, ...rest] -> x\n" +
        "  | [first, ..., last] -> first\n" +
        "  | 1..10 -> 0\n" +
        "  | { < 0 } -> 0\n" +
        "  | Point(px, py) -> px\n" +
        "  | (name = n) -> n\n" +
        "  | \"text\" -> 0\n" +
        "  | other -> 0"
    let texts = armsOf "f" (parse source) |> List.map (List.map (fun pattern -> textOf source pattern.Span))
    texts
    |> should equal [ [ "#some((a, _))" ]; [ "[x, ...rest]" ]; [ "[first, ..., last]" ]; [ "1..10" ]; [ "{ < 0 }" ]
                      [ "Point(px, py)" ]; [ "(name = n)" ]; [ "\"text\"" ]; [ "other" ] ]
    match armsOf "f" (parse source) with
    | [ tag ] :: [ list ] :: [ middle ] :: [ range ] :: _ :: [ record ] :: [ members ] :: _ ->
        match tag.Kind with
        | TagPattern ("some", Some tuple) ->
            textOf source tuple.Span |> should equal "(a, _)"
            match tuple.Kind with
            | TuplePattern [ a; wildcard ] ->
                textOf source a.Span |> should equal "a"
                a.Span.StartLine |> should equal 1
                a.Span.StartColumn |> should equal 11
                textOf source wildcard.Span |> should equal "_"
            | other -> failwith $"Expected a tuple pattern, got %A{other}"
        | other -> failwith $"Expected a tag pattern, got %A{other}"
        match list.Kind with
        | ListPattern ([ x ], Some rest) ->
            textOf source x.Span |> should equal "x"
            textOf source rest.Span |> should equal "rest"
        | other -> failwith $"Expected a list pattern with a rest, got %A{other}"
        match middle.Kind with
        | ListSplatMiddle ([ first ], [ last ]) ->
            textOf source first.Span |> should equal "first"
            textOf source last.Span |> should equal "last"
        | other -> failwith $"Expected a list pattern with a middle splat, got %A{other}"
        match range.Kind with
        | RangePattern (low, high) ->
            textOf source low.Span |> should equal "1"
            textOf source high.Span |> should equal "10"
        | other -> failwith $"Expected a range pattern, got %A{other}"
        match record.Kind with
        | RecordPattern ("Point", [ px; py ]) ->
            textOf source px.Span |> should equal "px"
            textOf source py.Span |> should equal "py"
        | other -> failwith $"Expected a record pattern, got %A{other}"
        match members.Kind with
        | RecordMemberPattern [ ("name", n) ] -> textOf source n.Span |> should equal "n"
        | other -> failwith $"Expected a record member pattern, got %A{other}"
    | other -> failwith $"Unexpected arms: %A{other}"

[<Fact>]
let ``a list pattern ending in an unnamed splat keeps a wildcard rest spanning the dots`` () =
    // `[x, ...]` matches any list of one or more, so it must not parse the same as `[x]`.
    let source = "def f = match v\n  | [x, ...] -> x\n  | [y] -> y"
    match armsOf "f" (parse source) with
    | [ [ unnamed ]; [ exact ] ] ->
        match unnamed.Kind with
        | ListPattern ([ x ], Some rest) ->
            textOf source x.Span |> should equal "x"
            rest.Kind |> should equal WildcardPattern
            textOf source rest.Span |> should equal "..."
        | other -> failwith $"Expected a list pattern with a rest, got %A{other}"
        match exact.Kind with
        | ListPattern ([ _ ], None) -> ()
        | other -> failwith $"Expected a list pattern without a rest, got %A{other}"
    | other -> failwith $"Unexpected arms: %A{other}"

[<Fact>]
let ``a tag with several payload patterns matches them as a tuple spanning the parentheses`` () =
    let source = "def f = match v\n  | #pair(a, b) -> a\n  | #one x -> x"
    match armsOf "f" (parse source) with
    | [ [ pair ]; [ one ] ] ->
        match pair.Kind with
        | TagPattern ("pair", Some payload) ->
            textOf source payload.Span |> should equal "(a, b)"
            match payload.Kind with
            | TuplePattern [ a; b ] ->
                textOf source a.Span |> should equal "a"
                textOf source b.Span |> should equal "b"
            | other -> failwith $"Expected a tuple payload, got %A{other}"
        | other -> failwith $"Expected a tag pattern, got %A{other}"
        textOf source one.Span |> should equal "#one x"
        match one.Kind with
        | TagPattern ("one", Some x) -> textOf source x.Span |> should equal "x"
        | other -> failwith $"Expected a tag pattern, got %A{other}"
    | other -> failwith $"Unexpected arms: %A{other}"

[<Fact>]
let ``the wildcard an if-match makes up for its else is reported at the else branch`` () =
    let source = "def x = if p is (a, 0) then a else fallback\ndef y = if q is #some(v) then v"
    let module' = parse source
    match armsOf "x" module' with
    | [ [ tuple ]; [ wildcard ] ] ->
        textOf source tuple.Span |> should equal "(a, 0)"
        wildcard.Kind |> should equal WildcardPattern
        textOf source wildcard.Span |> should equal "fallback"
    | other -> failwith $"Unexpected arms: %A{other}"
    // Without an `else`, the `()` it stands for is made up at the `then` branch, and so is the wildcard.
    match armsOf "y" module' with
    | [ [ _ ]; [ wildcard ] ] -> textOf source wildcard.Span |> should equal "v"
    | other -> failwith $"Unexpected arms: %A{other}"

[<Fact>]
let ``patterns are equal wherever they were written`` () =
    let first = parse "def f = match v\n  | #some((a, _)) -> a" |> armsOf "f"
    let second = parse "def f = match v\n  |   #some(( a,  _ )) -> a" |> armsOf "f"
    first.[0].[0].Span |> should not' (equal second.[0].[0].Span)
    first |> should equal second
    hash first |> should equal (hash second)
    compare first second |> should equal 0

/// The statements of the block that is the body of the lambda `def name` is bound to.
let private lambdaStatements name (module': Module) =
    match (valueOf name module').Kind with
    | Lambda (_, Kind (Block statements)) -> statements
    | other -> failwith $"Expected a lambda with a block body, got %A{other}"

[<Fact>]
let ``each statement in a block spans its text, without a comment after it`` () =
    let source =
        "def f = { x ->\n" +
        "  def y = x + 1 -- one more\n" +
        "  type Point = (x: int, y: int)\n" +
        "  use ctx\n" +
        "  print(y) -- noted\n" +
        "  match y\n" +
        "    | 0 -> 1\n" +
        "    | _ -> 2\n" +
        "  y * 2\n" +
        "}"
    let statements = parse source |> lambdaStatements "f"
    statements
    |> List.map (fun statement -> textOf source statement.Span)
    |> should equal [ "def y = x + 1"; "type Point = (x: int, y: int)"; "use ctx"; "print(y)"; "match y\n    | 0 -> 1\n    | _ -> 2"; "y * 2" ]
    let def = statements.[0]
    def.Span.StartLine |> should equal 1
    def.Span.StartColumn |> should equal 2

[<Fact>]
let ``each def a destructuring def stands for spans the whole def`` () =
    let source = "def f = { pair ->\n  def (a, b) = pair -- both\n  a + b\n}"
    let statements = parse source |> lambdaStatements "f"
    // The whole value under a temporary name, then one def per name.
    statements |> should haveLength 4
    statements
    |> List.take 3
    |> List.map (fun statement -> textOf source statement.Span)
    |> should equal [ "def (a, b) = pair"; "def (a, b) = pair"; "def (a, b) = pair" ]

[<Fact>]
let ``statements are equal wherever they were written`` () =
    let first = parse "def f = { x ->\n  def y = x\n  y\n}" |> lambdaStatements "f"
    let second = parse "def f = { x ->\n    def y =   x\n    y\n}" |> lambdaStatements "f"
    first.[0].Span |> should not' (equal second.[0].Span)
    first |> should equal second
    hash first |> should equal (hash second)
    compare first second |> should equal 0

/// The annotation of the top-level `def name` in `module'`.
let private annotationOf name (module': Module) =
    module'
    |> List.tryPick (function
        | Kind (Def (ValueDef (_, n, Some annotation, _))) when n = name -> Some annotation
        | _ -> None)
    |> Option.defaultWith (fun () -> failwith $"No annotated def {name} in %A{module'}")

[<Fact>]
let ``each type expression spans its text, and nested types span theirs`` () =
    let source =
        "def f: (int, string) -> list(a)? = f\n" +
        "def g: int, int -> int = g\n" +
        "def h: [Sum(int) + Console] int -> #some(n: int) | #none = h\n" +
        "type Shape =\n  | #circle(r: float)\n  | #square(side: float)"
    let module' = parse source
    let f = annotationOf "f" module'
    textOf source f.Span |> should equal "(int, string) -> list(a)?"
    match f.Kind with
    | TypeFunc (input, output) ->
        // A bracketed tuple spans its brackets.
        textOf source input.Span |> should equal "(int, string)"
        match input.Kind with
        | TypeTuple [ int; string ] ->
            textOf source int.Span |> should equal "int"
            textOf source string.Span |> should equal "string"
        | other -> failwith $"Expected a tuple, got %A{other}"
        textOf source output.Span |> should equal "list(a)?"
        match output.Kind with
        | TypeOptional list ->
            textOf source list.Span |> should equal "list(a)"
            match list.Kind with
            | TypeApply ("list", [ a ]) -> textOf source a.Span |> should equal "a"
            | other -> failwith $"Expected list(a), got %A{other}"
        | other -> failwith $"Expected an optional, got %A{other}"
    | other -> failwith $"Expected a function, got %A{other}"
    // Bare parameters are a tuple from the first to the last.
    let g = annotationOf "g" module'
    textOf source g.Span |> should equal "int, int -> int"
    match g.Kind with
    | TypeFunc (input, _) -> textOf source input.Span |> should equal "int, int"
    | other -> failwith $"Expected a function, got %A{other}"
    let h = annotationOf "h" module'
    textOf source h.Span |> should equal "[Sum(int) + Console] int -> #some(n: int) | #none"
    match h.Kind with
    | TypeWithContext ([ sum; console ], inner) ->
        textOf source sum.Span |> should equal "Sum(int)"
        textOf source console.Span |> should equal "Console"
        textOf source inner.Span |> should equal "int -> #some(n: int) | #none"
        match inner.Kind with
        | TypeFunc (_, Kind (TypeUnion [ some; none ])) ->
            textOf source some.Span |> should equal "#some(n: int)"
            textOf source none.Span |> should equal "#none"
        | other -> failwith $"Expected a function to a union, got %A{other}"
    | other -> failwith $"Expected a type with contexts, got %A{other}"
    // A union written with a leading bar starts at its first member.
    match List.last module' with
    | Kind (Def (TypeDef ("Shape", _, _, shape))) -> textOf source shape.Span |> should equal "#circle(r: float)\n  | #square(side: float)"
    | other -> failwith $"Expected the Shape type, got %A{other}"

[<Fact>]
let ``the types of lambda parameters, context accesses and use bindings span their text`` () =
    let source =
        "def f = { x: int, y -> ToString(list(a)).toString(x) }\n" +
        "def g = { z ->\n  use Sum(int) = (sum = add)\n  0\n}"
    let module' = parse source
    match (valueOf "f" module').Kind with
    | Lambda ([ ("x", Some int); ("y", None) ], Kind (ContextMemberCall (context, "toString", _))) ->
        textOf source int.Span |> should equal "int"
        textOf source context.Span |> should equal "ToString(list(a))"
    | other -> failwith $"Expected a lambda calling a context member, got %A{other}"
    match lambdaStatements "g" module' with
    | Kind (UseStatement (UseContextInstance (context, _))) :: _ -> textOf source context.Span |> should equal "Sum(int)"
    | other -> failwith $"Expected a use of a context instance, got %A{other}"

[<Fact>]
let ``type expressions are equal wherever they were written`` () =
    let first = parse "def f: (int, string) -> list(a)? = f" |> annotationOf "f"
    let second = parse "def f:   ( int,  string ) ->  list( a )? = f" |> annotationOf "f"
    first.Span |> should not' (equal second.Span)
    first |> should equal second
    hash first |> should equal (hash second)
    compare first second |> should equal 0

/// The source text of each item `source` parses to.
let private itemTexts (source: string) = parse source |> List.map (fun item -> textOf source item.Span)

[<Fact>]
let ``each top-level item spans its text, without a comment after it`` () =
    let source =
        "module shapes\n" +
        "import (nanyx/console as c \"./other.nyx\")\n" +
        "@entry\n" +
        "export def area: float -> float = { r -> r * r } -- squared\n" +
        "type Point = (x: int, y: int) -- a point\n" +
        "context Show(a) = (show: a -> string)\n" +
        "print(area(2.0)) -- once\n" +
        "def total =\n  def x = 1\n  x + 1\n"
    itemTexts source
    |> should equal [ "module shapes"
                      "import (nanyx/console as c \"./other.nyx\")"
                      "@entry"
                      "export def area: float -> float = { r -> r * r }"
                      "type Point = (x: int, y: int)"
                      "context Show(a) = (show: a -> string)"
                      "print(area(2.0))"
                      "def total =\n  def x = 1\n  x + 1" ]
    let def = (parse source).[3]
    def.Span.StartLine |> should equal 3
    def.Span.StartColumn |> should equal 0

[<Fact>]
let ``each def a top-level destructuring def stands for spans the whole def`` () =
    let source = "def (a, b) = pair -- both\ndef c = a"
    itemTexts source |> should equal [ "def (a, b) = pair"; "def (a, b) = pair"; "def (a, b) = pair"; "def c = a" ]

[<Fact>]
let ``the items of a module block keep their own spans, and the module it becomes spans the block`` () =
    let source = "module app\nmodule geometry =\n  type Point = (x: int, y: int)\n  def origin = Point(0, 0)\n  -- the end of the block\ndef p = geometry.origin"
    let module' = parse source
    module' |> List.map (fun item -> textOf source item.Span)
    |> should equal [ "module app"
                      "module geometry =\n  type Point = (x: int, y: int)\n  def origin = Point(0, 0)"
                      "type Point = (x: int, y: int)"
                      "def origin = Point(0, 0)"
                      "def p = geometry.origin" ]
    match module'.[1].Kind with
    | ModuleDecl "app.geometry" -> ()
    | other -> failwith $"Expected the block's module declaration, got %A{other}"

[<Fact>]
let ``top-level items are equal wherever they were written`` () =
    let first = parse "def x = 1\n@entry\ndef main = { x }"
    let second = parse "\n\ndef x =   1\n\n@entry\ndef main =  { x }"
    first.[0].Span |> should not' (equal second.[0].Span)
    first |> should equal second
    hash first |> should equal (hash second)
    compare first second |> should equal 0
