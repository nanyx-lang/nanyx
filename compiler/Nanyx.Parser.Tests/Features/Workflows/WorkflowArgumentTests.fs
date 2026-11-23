module ParserTests.Features.Workflows.Arguments

open Xunit
open ParserTestHelpers
open Parser.Program

/// The expression a builder's block, `proc { ... }`, written as `def f = <it>`, returns.
let private returned (source: string) =
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", None, Kind (FunctionCall ("proc", _, [ Kind (Lambda ([], body, _)) ]))))) ] ->
        match body.Kind with
        | WorkflowReturnExpr value
        | Block [ Kind (ExprStatement (Kind (WorkflowReturnExpr value))) ] -> value.Kind
        | other -> failwith $"Unexpected body: %A{other}"
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``A try that is a named argument ends at the comma`` () =
    match returned "def f = proc { return P(a = try num(x), b = try num(y)) }\n" with
    | FunctionCall ("P", _, [ Kind (RecordExpr [ NamedField ("a", Kind (WorkflowBindExpr ("try", Kind (FunctionCall ("num", _, _)))))
                                                 NamedField ("b", Kind (WorkflowBindExpr ("try", Kind (FunctionCall ("num", _, _))))) ]) ]) -> ()
    | other -> failwith $"Unexpected call: %A{other}"

[<Fact>]
let ``A try that is a named argument ends at the end of its line, one argument per line`` () =
    match returned "def f = proc {\n  return P(\n    a = try num(x)\n    b = try num(y)\n  )\n}\n" with
    | FunctionCall ("P", _, [ Kind (RecordExpr [ NamedField ("a", Kind (WorkflowBindExpr ("try", Kind (FunctionCall ("num", _, _)))))
                                                 NamedField ("b", Kind (WorkflowBindExpr ("try", Kind (FunctionCall ("num", _, _))))) ]) ]) -> ()
    | other -> failwith $"Unexpected call: %A{other}"

[<Fact>]
let ``Each workflow keyword in a positional argument ends at its comma, and takes in an operator after it`` () =
    match returned "def f = proc { return g(try a + 1, try! b, yield c, return d, e) }\n" with
    | FunctionCall ("g", _, [ Kind (TupleExpr [ Kind (WorkflowBindExpr ("try", Kind (BinaryOp ("+", _, Kind (IdentifierExpr "a"), _))))
                                                Kind (WorkflowBindExpr ("try", Kind (IdentifierExpr "b")))
                                                Kind (WorkflowBindExpr ("yield", Kind (IdentifierExpr "c")))
                                                Kind (WorkflowReturnExpr (Kind (IdentifierExpr "d")))
                                                Kind (IdentifierExpr "e") ]) ]) -> ()
    | other -> failwith $"Unexpected call: %A{other}"

[<Fact>]
let ``Each workflow keyword on a line of its own among the arguments ends at the end of its line`` () =
    match returned "def f = proc {\n  return g(\n    return a\n    try! b\n    yield c\n    d\n  )\n}\n" with
    | FunctionCall ("g", _, [ Kind (TupleExpr [ Kind (WorkflowReturnExpr (Kind (IdentifierExpr "a")))
                                                Kind (WorkflowBindExpr ("try", Kind (IdentifierExpr "b")))
                                                Kind (WorkflowBindExpr ("yield", Kind (IdentifierExpr "c")))
                                                Kind (IdentifierExpr "d") ]) ]) -> ()
    | other -> failwith $"Unexpected call: %A{other}"

[<Fact>]
let ``A try in a list's element or a map's entry ends at the comma`` () =
    match returned "def f = proc { return [try a, try b] }\n" with
    | ListExpr [ Kind (WorkflowBindExpr ("try", Kind (IdentifierExpr "a"))); Kind (WorkflowBindExpr ("try", Kind (IdentifierExpr "b"))) ] -> ()
    | other -> failwith $"Unexpected list: %A{other}"
    match returned "def f = proc { return [try a => 1, 2 => try b] }\n" with
    | MapSyntax.Call [ Kind (WorkflowBindExpr ("try", Kind (IdentifierExpr "a"))), _; _, Kind (WorkflowBindExpr ("try", Kind (IdentifierExpr "b"))) ] -> ()
    | other -> failwith $"Unexpected map: %A{other}"
