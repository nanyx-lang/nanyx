module ParserTests.Features.Operators

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``Parse binary-operators.nyx file`` () =
    let result = parseTestFile "binary-operators.nyx"

    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> List.length |> should be (greaterThan 10)
        match module'.[0] with
        | Kind (ModuleDecl name) -> name |> should equal "BinaryOperators"
        | _ -> failwith "Expected ModuleDecl"

        let hasBinaryOp op (module': Module) =
            module' |> List.exists (fun item ->
                match item.Kind with
                | Def (ValueDef (_, _, _, Kind (BinaryOp (operator, _, _)))) -> operator = op
                | _ -> false
            )

        module' |> hasBinaryOp "+" |> should equal true
        module' |> hasBinaryOp "-" |> should equal true
        module' |> hasBinaryOp "*" |> should equal true
        module' |> hasBinaryOp "/" |> should equal true
        module' |> hasBinaryOp "==" |> should equal true
        module' |> hasBinaryOp "!=" |> should equal true
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``++ is concatenation, looser than + and tighter than ==`` () =
    match parseModule "def x = a + b ++ c == d" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (BinaryOp ("==", Kind (BinaryOp ("++", Kind (BinaryOp ("+", _, _)), _)), _))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``++ associates to the right`` () =
    match parseModule "def x = a ++ b ++ c" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (BinaryOp ("++", Kind (IdentifierExpr "a"), Kind (BinaryOp ("++", _, _))))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``and binds tighter than or and looser than comparison`` () =
    match parseModule "def x = a == b and c or d" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (BinaryOp ("or", Kind (BinaryOp ("and", Kind (BinaryOp ("==", _, _)), _)), Kind (IdentifierExpr "d")))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``not is a prefix keyword that binds tighter than infix operators`` () =
    match parseModule "def x = not a == b" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (BinaryOp ("==", Kind (FunctionCall ("not", _, [ Kind (IdentifierExpr "a") ])), _))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``keyword operators do not swallow identifiers that start with them`` () =
    match parseModule "def x = order and android or nothing" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (BinaryOp ("or", Kind (BinaryOp ("and", Kind (IdentifierExpr "order"), Kind (IdentifierExpr "android"))), Kind (IdentifierExpr "nothing")))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``&& and || are aliases for and and or`` () =
    match parseModule "def x = a && b || c" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (BinaryOp ("or", Kind (BinaryOp ("and", _, _)), _))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``++ shorthand lambda is the two-argument concatenation`` () =
    match parseModule "def f = { ++ }" with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda ([ (a, None); (b, None) ], Kind (BinaryOp ("++", _, _))))))) ]
        when a = implicitArgument && b = implicitSecondArgument -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

// Binary Operator Tests
[<Fact>]
let ``Parse addition`` () =
    let result = parseModule "def sum = 1 + 2"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (BinaryOp (op, left, right))))) ->
            name |> should equal "sum"
            op |> should equal "+"
            match left.Kind, right.Kind with
            | LiteralExpr (IntLit v1), LiteralExpr (IntLit v2) ->
                v1 |> should equal 1
                v2 |> should equal 2
            | _ -> failwith "Expected int literals"
        | _ -> failwith "Expected Def with BinaryOp"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse multiplication`` () =
    let result = parseModule "def product = 3 * 4"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        match module'.[0] with
        | Kind (Def (ValueDef (_, _, _, Kind (BinaryOp (op, _, _))))) ->
            op |> should equal "*"
        | _ -> failwith "Expected Def with BinaryOp"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse operator precedence - multiplication before addition`` () =
    let result = parseModule "def x = 1 + 2 * 3"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        match module'.[0] with
        | Kind (Def (ValueDef (_, _, _, Kind (BinaryOp ("+", Kind (LiteralExpr (IntLit 1)), Kind (BinaryOp ("*", Kind (LiteralExpr (IntLit 2)), Kind (LiteralExpr (IntLit 3))))))))) ->
            () // Correct precedence: 1 + (2 * 3)
        | _ -> failwith "Expected correct precedence: 1 + (2 * 3)"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse parenthesized expression`` () =
    let result = parseModule "def x = (1 + 2) * 3"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        match module'.[0] with
        | Kind (Def (ValueDef (_, _, _, Kind (BinaryOp ("*", Kind (BinaryOp ("+", Kind (LiteralExpr (IntLit 1)), Kind (LiteralExpr (IntLit 2)))), Kind (LiteralExpr (IntLit 3))))))) ->
            () // Correct: (1 + 2) * 3
        | _ -> failwith "Expected correct precedence: (1 + 2) * 3"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse comparison operators`` () =
    let result = parseModule "def x = 5 > 3"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        match module'.[0] with
        | Kind (Def (ValueDef (_, _, _, Kind (BinaryOp (">", Kind (LiteralExpr (IntLit 5)), Kind (LiteralExpr (IntLit 3))))))) ->
            ()
        | _ -> failwith "Expected comparison operator"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse equality operators`` () =
    let result = parseModule "def x = a == b"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        match module'.[0] with
        | Kind (Def (ValueDef (_, _, _, Kind (BinaryOp ("==", Kind (IdentifierExpr "a"), Kind (IdentifierExpr "b")))))) ->
            ()
        | _ -> failwith "Expected equality operator"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Prefix minus negates its operand, tighter than every infix operator`` () =
    let negated (operand: ExpressionKind -> bool) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, None, [ Kind inner ]) -> name = NegateSyntax.builtIn && operand inner
        | _ -> false
    let isName name = function IdentifierExpr n -> n = name | _ -> false
    match parseModule "def a = -x * y\ndef b = 3 - -x\ndef c = -f(x)\ndef d = -5\ndef e = a -b\n" with
    | Result.Ok [ Kind (Def (ValueDef (_, "a", _, Kind (BinaryOp ("*", Kind left, _)))))
                  Kind (Def (ValueDef (_, "b", _, Kind (BinaryOp ("-", _, Kind right)))))
                  Kind (Def (ValueDef (_, "c", _, Kind call)))
                  Kind (Def (ValueDef (_, "d", _, Kind (LiteralExpr (IntLit -5)))))
                  Kind (Def (ValueDef (_, "e", _, Kind (BinaryOp ("-", _, _))))) ] ->
        Assert.True(negated (isName "x") left, $"%A{left}")
        Assert.True(negated (isName "x") right, $"%A{right}")
        Assert.True(negated (function FunctionCall("f", _, _) -> true | _ -> false) call, $"%A{call}")
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``A shorthand lambda that starts with minus still subtracts from its argument`` () =
    match parseModule "def f = { -x }\n" with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda ([ _ ], Kind (BinaryOp ("-", _, Kind (IdentifierExpr "x"))))))))] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``** binds tighter than * and groups to the right`` () =
    match parseModule "def a = 2 ** 3 ** 2\ndef b = 2 * 3 ** 2\ndef c = -x ** 2\n" with
    | Result.Ok [ Kind (Def (ValueDef (_, "a", _, Kind (BinaryOp ("**", Kind (LiteralExpr (IntLit 2)), Kind (BinaryOp ("**", _, _)))))))
                  Kind (Def (ValueDef (_, "b", _, Kind (BinaryOp ("*", _, Kind (BinaryOp ("**", _, _)))))))
                  Kind (Def (ValueDef (_, "c", _, Kind (BinaryOp ("**", Kind (FunctionCall (name, _, _)), _))))) ] ->
        Assert.Equal(NegateSyntax.builtIn, name)
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

// A block's statements and a match arm's body are parsed by the expression parser that stops at the end of a line.
// It has the keyword operators too (#617, #628).

/// The expression of the one statement of the block that is the body of `source`'s one def.
let private blockStatementOf (source: string) =
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, Kind (Block [ Kind (ExprStatement expr) ])))) ]
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, Kind (Lambda (_, Kind (Block [ Kind (ExprStatement expr) ])))))) ] -> expr.Kind
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

/// The body of each arm of the match that is the body of `source`'s one def, a lambda.
let private armBodiesOf (source: string) =
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, Kind (Lambda (_, Kind (Match (_, arms))))))) ]
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, Kind (Lambda (_, Kind (Block [ Kind (ExprStatement (Kind (Match (_, arms)))) ])))))) ] ->
        arms |> List.map (fun (_, body) -> body.Kind)
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``and after a call at the start of a block statement is the operator`` () =
    match blockStatementOf "def b = {\n  f(1) and f(2)\n}\n" with
    | BinaryOp ("and", Kind (FunctionCall ("f", _, _)), Kind (FunctionCall ("f", _, _))) -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``or after a call at the start of a block statement is the operator`` () =
    match blockStatementOf "def b = {\n  f(1) or f(2)\n}\n" with
    | BinaryOp ("or", Kind (FunctionCall ("f", _, _)), Kind (FunctionCall ("f", _, _))) -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``and binds tighter than or and looser than comparison in a block statement`` () =
    match blockStatementOf "def b = { x ->\n  x == 1 and f(x) or g(x) && not h(x) || x % 2 == 0\n}\n" with
    | BinaryOp ("or",
                Kind (BinaryOp ("or",
                                Kind (BinaryOp ("and", Kind (BinaryOp ("==", _, _)), Kind (FunctionCall ("f", _, _)))),
                                Kind (BinaryOp ("and", Kind (FunctionCall ("g", _, _)), Kind (FunctionCall ("not", _, [ Kind (FunctionCall ("h", _, _)) ])))))),
                Kind (BinaryOp ("==", Kind (BinaryOp ("%", _, _)), _))) -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A keyword operator in a block statement does not swallow an identifier that starts with it`` () =
    match blockStatementOf "def b = {\n  order and android or nothing\n}\n" with
    | BinaryOp ("or", Kind (BinaryOp ("and", Kind (IdentifierExpr "order"), Kind (IdentifierExpr "android"))), Kind (IdentifierExpr "nothing")) -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``and and or are operators in the body of a match arm`` () =
    let source = "def g = { x, y ->\n  match x\n    | 1 -> y and true\n    | 2 -> y or false\n    | _ -> false\n}\n"
    match armBodiesOf source with
    | [ BinaryOp ("and", Kind (IdentifierExpr "y"), Kind (LiteralExpr (BoolLit true)))
        BinaryOp ("or", Kind (IdentifierExpr "y"), Kind (LiteralExpr (BoolLit false)))
        LiteralExpr (BoolLit false) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``and and or are operators in a match arm's body on a line of its own`` () =
    let source = "def g = { x, y ->\n  match x\n    | 1 ->\n      y and true\n    | _ ->\n      y or false\n}\n"
    match armBodiesOf source with
    | [ Block [ Kind (ExprStatement (Kind (BinaryOp ("and", _, _)))) ]; Block [ Kind (ExprStatement (Kind (BinaryOp ("or", _, _)))) ] ]
    | [ BinaryOp ("and", _, _); BinaryOp ("or", _, _) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``and after a comparison is the operator in the body of a list pattern's arm`` () =
    let source = "def g = { xs ->\n  match xs\n    | [] -> true\n    | [x, ...more] -> x == 1 and g(more)\n}\n"
    match armBodiesOf source with
    | [ LiteralExpr (BoolLit true); BinaryOp ("and", Kind (BinaryOp ("==", _, _)), Kind (FunctionCall ("g", _, _))) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"
