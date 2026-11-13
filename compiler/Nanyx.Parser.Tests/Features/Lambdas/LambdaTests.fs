module ParserTests.Features.Lambdas

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``Parse lambdas.nyx file`` () =
    let result = parseTestFile "lambdas.nyx"

    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 7  // 1 module + 6 lambda defs
        match module'.[0] with
        | Kind (ModuleDecl name) -> name |> should equal "Lambdas"
        | _ -> failwith "Expected ModuleDecl"

        let defs = module' |> List.tail
        for def in defs do
            match def with
            | Kind (Def (ValueDef (_, _, _, Kind (Lambda _)))) -> ()
            | _ -> failwith "Expected all definitions to contain lambdas"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``Parse shorthand-lambdas.nyx file`` () =
    let result = parseTestFile "shorthand-lambdas.nyx"

    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 15  // 1 module + 14 shorthand lambda defs
        match module'.[0] with
        | Kind (ModuleDecl name) -> name |> should equal "ShorthandLambdas"
        | _ -> failwith "Expected ModuleDecl"

        let defs = module' |> List.tail
        for def in defs do
            match def with
            | Kind (Def (ValueDef (_, _, _, Kind (Lambda _)))) -> ()
            | Kind (Def (ValueDef (_, _, _, Kind (FunctionCall (_, _, args))))) ->
                let rec hasLambda (arg: Expression) =
                    match arg.Kind with
                    | Lambda _ -> true
                    | TupleExpr exprs -> exprs |> List.exists hasLambda
                    | _ -> false
                let foundLambda = args |> List.exists hasLambda
                foundLambda |> should equal true
            | _ -> failwith "Expected lambda or function call with lambda"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``an empty block is a lambda returning unit`` () =
    match parseModule "def main = {\n}\ndef noop = { }" with
    | Result.Ok [ Kind (Def (ValueDef (_, "main", None, Kind (Lambda ([], Kind UnitExpr, _))))); Kind (Def (ValueDef (_, "noop", None, Kind (Lambda ([], Kind UnitExpr, _))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

// Lambda Expression Tests
[<Fact>]
let ``Parse lambda with no parameters`` () =
    let result = parseModule "def f = { 42 }"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Lambda (parameters, body, _))))) ->
            name |> should equal "f"
            parameters |> should haveLength 0
            match body.Kind with
            | LiteralExpr (IntLit v) -> v |> should equal 42I
            | _ -> failwith "Expected int literal in lambda body"
        | _ -> failwith "Expected Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse lambda with one parameter`` () =
    let result = parseModule "def f = { x -> x }"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Lambda (parameters, body, _))))) ->
            name |> should equal "f"
            parameters |> should haveLength 1
            parameters.[0] |> fst |> should equal "x"
            match body.Kind with
            | IdentifierExpr id -> id |> should equal "x"
            | _ -> failwith "Expected identifier in lambda body"
        | _ -> failwith "Expected Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse lambda with multiple parameters`` () =
    let result = parseModule "def add = { x, y -> 42 }"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Lambda (parameters, body, _))))) ->
            name |> should equal "add"
            parameters |> should haveLength 2
            parameters.[0] |> fst |> should equal "x"
            parameters.[1] |> fst |> should equal "y"
        | _ -> failwith "Expected Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse lambda with function call in body`` () =
    let result = parseModule "def f = { x -> println(x) }"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Lambda (parameters, body, _))))) ->
            name |> should equal "f"
            parameters |> should haveLength 1
            parameters.[0] |> fst |> should equal "x"
            match body.Kind with
            | FunctionCall (funcName, _, args) ->
                funcName |> should equal "println"
                args |> should haveLength 1
            | _ -> failwith "Expected function call in lambda body"
        | _ -> failwith "Expected Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse lambda as function argument`` () =
    let result = parseModule "def x = map({ x -> x }, items)"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (FunctionCall (funcName, _, args))))) ->
            name |> should equal "x"
            funcName |> should equal "map"
            // With comma operator, f(x, y) creates a single tuple argument
            args |> should haveLength 1
            match args.[0].Kind with
            | TupleExpr [Kind (Lambda (parameters, _, _)); Kind (IdentifierExpr "items")] ->
                parameters |> should haveLength 1
                parameters.[0] |> fst |> should equal "x"
            | _ -> failwith "Expected tuple with lambda and identifier"
        | _ -> failwith "Expected Def with FunctionCall"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

// Shorthand Lambda Tests
[<Fact>]
let ``Parse shorthand lambda with binary operator`` () =
    let result = parseModule "def multiply = { * }"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Lambda (parameters, body, _))))) ->
            name |> should equal "multiply"
            parameters |> should haveLength 2
            parameters.[0] |> fst |> should equal implicitArgument
            parameters.[1] |> fst |> should equal implicitSecondArgument
            match body.Kind with
            | BinaryOp (op, _, Kind (IdentifierExpr left), Kind (IdentifierExpr right)) ->
                op |> should equal "*"
                left |> should equal implicitArgument
                right |> should equal implicitSecondArgument
            | _ -> failwith "Expected binary operation in shorthand lambda body"
        | _ -> failwith "Expected Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse shorthand lambda with unary operation`` () =
    let result = parseModule "def double = { * 2 }"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Lambda (parameters, body, _))))) ->
            name |> should equal "double"
            parameters |> should haveLength 1
            parameters.[0] |> fst |> should equal implicitArgument
            match body.Kind with
            | BinaryOp (op, _, Kind (IdentifierExpr left), Kind (LiteralExpr (IntLit right))) ->
                op |> should equal "*"
                left |> should equal implicitArgument
                right |> should equal 2I
            | _ -> failwith "Expected binary operation with literal in shorthand lambda body"
        | _ -> failwith "Expected Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse shorthand lambda with property access`` () =
    let result = parseModule "def getName = { .name }"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Lambda (parameters, body, _))))) ->
            name |> should equal "getName"
            parameters |> should haveLength 1
            parameters.[0] |> fst |> should equal implicitArgument
            match body.Kind with
            | MemberAccess (Kind (IdentifierExpr obj), field, _) ->
                obj |> should equal implicitArgument
                field |> should equal "name"
            | _ -> failwith "Expected member access in shorthand property lambda body"
        | _ -> failwith "Expected Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse shorthand lambda with addition`` () =
    let result = parseModule "def addFive = { + 5 }"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Lambda (parameters, body, _))))) ->
            name |> should equal "addFive"
            parameters |> should haveLength 1
            parameters.[0] |> fst |> should equal implicitArgument
            match body.Kind with
            | BinaryOp (op, _, Kind (IdentifierExpr left), Kind (LiteralExpr (IntLit right))) ->
                op |> should equal "+"
                left |> should equal implicitArgument
                right |> should equal 5I
            | _ -> failwith "Expected addition operation in shorthand lambda body"
        | _ -> failwith "Expected Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse shorthand lambda as function argument`` () =
    let result = parseModule "def doubled = map({ * 2 }, numbers)"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (FunctionCall (funcName, _, args))))) ->
            name |> should equal "doubled"
            funcName |> should equal "map"
            // With comma operator, f(x, y) creates a single tuple argument
            args |> should haveLength 1
            match args.[0].Kind with
            | TupleExpr [Kind (Lambda (parameters, body, _)); Kind (IdentifierExpr "numbers")] ->
                parameters |> should haveLength 1
                parameters.[0] |> fst |> should equal implicitArgument
                match body.Kind with
                | BinaryOp (op, _, Kind (IdentifierExpr left), Kind (LiteralExpr (IntLit right))) ->
                    op |> should equal "*"
                    left |> should equal implicitArgument
                    right |> should equal 2I
                | _ -> failwith "Expected multiplication in lambda"
            | _ -> failwith "Expected shorthand lambda as first argument"
        | _ -> failwith "Expected Def with FunctionCall"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

/// The value of the one def `source` declares.
let private onlyValue source =
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, value))) ] -> value
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

let private implicit = IdentifierExpr implicitArgument

[<Fact>]
let ``a shorthand lambda's body continues after its first operation`` () =
    // `{ % 2 == 0 }` is `{ x -> x % 2 == 0 }`: the argument is the left operand of the first operator.
    match (onlyValue "def isEven = { % 2 == 0 }").Kind with
    | Lambda ([ (parameter, None) ], Kind (BinaryOp ("==", _, Kind (BinaryOp ("%", _, Kind left, Kind (LiteralExpr (IntLit64 2L)))), Kind (LiteralExpr (IntLit64 0L)))), _)
        when parameter = implicitArgument && left = implicit -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"

[<Fact>]
let ``the rest of a shorthand lambda's body follows the operators' precedence`` () =
    match (onlyValue "def f = { * 2 + 1 }").Kind with
    | Lambda ([ _ ], Kind (BinaryOp ("+", _, Kind (BinaryOp ("*", _, Kind left, _)), Kind (LiteralExpr (IntLit64 1L)))), _) when left = implicit -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"

[<Fact>]
let ``a shorthand lambda's body may continue after a member access`` () =
    match (onlyValue "def adults = { .age > 18 }").Kind with
    | Lambda ([ _ ], Kind (BinaryOp (">", _, Kind (MemberAccess (Kind target, "age", _)), _)), _) when target = implicit -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"

[<Fact>]
let ``a member access shorthand compared with a string is a pipe's trailing lambda`` () =
    match (onlyValue "def bs = ps \\list.filter { .name == \"b\" }").Kind with
    | Pipe (Kind (IdentifierExpr "ps"), "list.filter", _, [ Kind (Lambda ([ _ ], Kind (BinaryOp ("==", _, Kind (MemberAccess (Kind target, "name", _)), Kind (LiteralExpr (StringLit "b")))), _)) ])
        when target = implicit -> ()
    | other -> failwith $"Unexpected pipe: %A{other}"

[<Fact>]
let ``every operator may start a shorthand lambda`` () =
    for op in [ "+"; "-"; "*"; "/"; "%"; "++"; "<"; ">"; "<="; ">="; "=="; "!=" ] do
        match (onlyValue $"def f = {{ {op} y }}").Kind with
        | Lambda ([ _ ], Kind (BinaryOp (parsed, _, Kind left, Kind (IdentifierExpr "y"))), _) when parsed = op && left = implicit -> ()
        | other -> failwith $"Unexpected lambda for {op}: %A{other}"
        match (onlyValue $"def f = {{ {op} }}").Kind with
        | Lambda ([ _; _ ], Kind (BinaryOp (parsed, _, _, _)), _) when parsed = op -> ()
        | other -> failwith $"Unexpected two-argument lambda for {op}: %A{other}"

[<Fact>]
let ``a leading minus is subtraction from the argument, and an arrow keeps a negative body`` () =
    match (onlyValue "def f = { - 1 }").Kind with
    | Lambda ([ _ ], Kind (BinaryOp ("-", _, Kind left, Kind (LiteralExpr (IntLit64 1L)))), _) when left = implicit -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"
    match (onlyValue "def f = { -> -1 }").Kind with
    | Lambda ([], _, _) -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"

[<Fact>]
let ``a shorthand lambda's argument cannot capture a name written in it`` () =
    // The argument is a name no code can write, so `x` here is the `x` around the lambda.
    match (onlyValue "def f = { * x }").Kind with
    | Lambda ([ (parameter, None) ], Kind (BinaryOp ("*", _, _, Kind (IdentifierExpr "x"))), _) -> parameter |> should not' (equal "x")
    | other -> failwith $"Unexpected lambda: %A{other}"

[<Fact>]
let ``a shorthand lambda is a pipe's trailing lambda`` () =
    match (onlyValue "def evens = items \\filter { % 2 == 0 }").Kind with
    | Pipe (Kind (IdentifierExpr "items"), "filter", _, [ Kind (Lambda ([ _ ], Kind (BinaryOp ("==", _, _, _)), _)) ]) -> ()
    | other -> failwith $"Unexpected pipe: %A{other}"

[<Fact>]
let ``a pattern parameter is taken apart by a match at the top of the body`` () =
    let source = "def f = { $Email(s) -> s }"
    match (onlyValue source).Kind with
    | Lambda ([ (parameter, None) ], Kind (Match ([ scrutinee ], [ [ Kind (RecordPattern ("$Email", [ Kind (IdentifierPattern "s") ])) ], Kind (IdentifierExpr "s") ])), _)
        when parameter = patternArgument 1 && scrutinee.Kind = IdentifierExpr parameter ->
        // The scrutinee is made up where the pattern is written.
        source.Substring(scrutinee.Span.Start, scrutinee.Span.End - scrutinee.Span.Start) |> should equal "$Email(s)"
    | other -> failwith $"Unexpected lambda: %A{other}"

[<Fact>]
let ``pattern parameters and named ones may be mixed, and one match takes every pattern apart`` () =
    match (onlyValue "def f = { (a, b), c, [x, ...rest] -> a }").Kind with
    | Lambda ([ (first, None); ("c", None); (third, None) ], Kind (Match ([ Kind (IdentifierExpr s1); Kind (IdentifierExpr s3) ], [ [ Kind (TuplePattern _); Kind (ListPattern _) ], _ ])), _)
        when first = patternArgument 1 && third = patternArgument 3 && s1 = first && s3 = third -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"

[<Fact>]
let ``a name, _ or a bracketed annotated name is an ordinary parameter, not a pattern`` () =
    match (onlyValue "def f = { x, _, (y: int) -> x }").Kind with
    | Lambda ([ ("x", None); ("_", None); ("y", Some _) ], Kind (IdentifierExpr "x"), _) -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"
    match (onlyValue "def f = { (x) -> x }").Kind with
    | Lambda ([ ("x", None) ], Kind (IdentifierExpr "x"), _) -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"

[<Fact>]
let ``a unit pattern alone is a lambda without parameters that takes unit`` () =
    match (onlyValue "def f = { () -> 5 }").Kind with
    | Lambda ([], Kind (LiteralExpr (IntLit64 5L)), true) -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"
    // Without it, the lambda ignores a parameter its expected type gives it (#813).
    for source in [ "def f = { 5 }"; "def f = { -> 5 }"; "def f = { }" ] do
        match (onlyValue source).Kind with
        | Lambda ([], _, false) -> ()
        | other -> failwith $"Unexpected lambda for {source}: %A{other}"
    // Beside other parameters, it is a pattern like any other.
    match (onlyValue "def f = { (), n -> n }").Kind with
    | Lambda ([ (unit, None); ("n", None) ], Kind (Match ([ _ ], [ [ Kind UnitPattern ], _ ])), _) when unit = patternArgument 1 -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"

[<Fact>]
let ``a body that only looks like a pattern is still a body`` () =
    match (onlyValue "def f = { (1, 2) }").Kind with
    | Lambda ([], Kind (TupleExpr _), _) -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"
    match (onlyValue "def f = { #some(1) }").Kind with
    | Lambda ([], Kind (TagExpr _), _) -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"

[<Fact>]
let ``the implicit argument is made up at the operator written in its place`` () =
    let source = "def f = { % 2 == 0 }"
    match (onlyValue source).Kind with
    | Lambda (_, Kind (BinaryOp ("==", _, (Kind (BinaryOp ("%", _, argument, _)) as remainder), _)), _) ->
        source.Substring(argument.Span.Start, argument.Span.End - argument.Span.Start) |> should equal "%"
        source.Substring(remainder.Span.Start, remainder.Span.End - remainder.Span.Start) |> should equal "% 2"
    | other -> failwith $"Unexpected lambda: %A{other}"

[<Fact>]
let ``named parameters followed by arms match the arms against the parameters`` () =
    let source =
        """def bind = { m, f ->
  | #some(v), f -> f(v)
  | other, _ -> other
}"""
            .ReplaceLineEndings "\n"
    match (onlyValue source).Kind with
    | Lambda ([ ("m", None); ("f", None) ], Kind (Match ([ Kind (IdentifierExpr "m"); Kind (IdentifierExpr "f") ], [ [ Kind (TagPattern _); _ ], _; [ Kind (IdentifierPattern "other"); Kind WildcardPattern ], _ ])), false) -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"

[<Fact>]
let ``arms after parameters may follow on the arrow's line, after one parameter or an operator's`` () =
    match (onlyValue "def name = { o -> | #some(s) -> s | #none -> \"none\" }").Kind with
    | Lambda ([ ("o", None) ], Kind (Match ([ Kind (IdentifierExpr "o") ], [ _; _ ])), false) -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"
    match (onlyValue "def `>>=` = { m, (f: int -> int) -> | #some(v), f -> f(v) | other, _ -> other }").Kind with
    | Lambda ([ ("m", None); ("f", Some _) ], Kind (Match ([ _; _ ], [ _; _ ])), false) -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"

[<Fact>]
let ``a result annotation after the parameters types the body`` () =
    match (onlyValue "def f = { (x: int): int -> x * 2 }").Kind with
    | Lambda ([ ("x", Some _) ], Kind (LambdaResultSyntax.Annotated (Kind (TypeName "int"), Kind (BinaryOp ("*", _, _, _)))), false) -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"
    // A function type ends at the arrow, as an unbracketed parameter's does.
    match (onlyValue "def f = { (): int | string -> 5 }").Kind with
    | Lambda ([], Kind (LambdaResultSyntax.Annotated (Kind (TypeUnion _), _)), true) -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"
    // Pattern parameters are taken apart first: the annotated body is the match's arm.
    match (onlyValue "def f = { (a, b): int -> a }").Kind with
    | Lambda ([ _ ], Kind (Match (_, [ _, Kind (LambdaResultSyntax.Annotated (_, Kind (IdentifierExpr "a"))) ])), false) -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"