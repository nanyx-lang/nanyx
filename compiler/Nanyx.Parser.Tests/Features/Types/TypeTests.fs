module ParserTests.Features.Types

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

// Type expressions to compare annotations with. Equality ignores where a type was written, so these have no span.
let private ty kind = TypeExpr.synthetic kind
let private named name = ty (TypeName name)
let private var name = ty (TypeVar name)
let private func input output = ty (TypeFunc (input, output))
let private tuple items = ty (TypeTuple items)
let private tag name payload = ty (TypeTag (name, payload))

[<Fact>]
let ``Parse type definition feature files`` () =
    parseFeatureNyxFiles "Types"
    |> Array.iter (fun (filePath, result) ->
        result |> isOk |> should equal true
        match result with
        | Result.Error err -> failwith $"Parse failed for {filePath}: {err}"
        | _ -> ()
    )

[<Fact>]
let ``def annotation can start an indented continuation line`` () =
    let source =
        "def mapOption\n" +
        "  : (#some(a) | r), (a -> b) -> (#some(b) | r)\n" +
        "  = { | #some(a), f -> #some(f(a))\n" +
        "      | other, _ -> other }\n"
    let optionOf payload = ty (TypeUnion [ tag "some" (Some (var payload)); var "r" ])
    let expected = func (tuple [ optionOf "a"; func (var "a") (var "b") ]) (optionOf "b")
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, "mapOption", Some annotation, Kind (Lambda _)))) ] -> annotation |> should equal expected
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``bare parameters in a def annotation bind tighter than the arrow`` () =
    let annotationOf source =
        match parseModule source with
        | Result.Ok [ Kind (Def (ValueDef (_, _, Some annotation, _))) ] -> annotation
        | other -> failwith $"Unexpected parse: %A{other}"
    let expected = func (tuple [ named "int"; named "int" ]) (named "int")
    annotationOf "def add: int, int -> int = { x, y -> x + y }" |> should equal expected
    annotationOf "def add: (int, int) -> int = { x, y -> x + y }" |> should equal expected

[<Fact>]
let ``comma binds tighter than the arrow inside brackets too`` () =
    let annotationOf source =
        match parseModule source with
        | Result.Ok [ Kind (Def (ValueDef (_, _, Some annotation, _))) ] -> annotation
        | other -> failwith $"Unexpected parse: %A{other}"
    let pair = tuple [ named "int"; var "a" ]
    // In brackets, `int, a -> b` is still a function of two parameters.
    annotationOf "def f: (int, a -> b) -> b = { g -> g(1, 2) }"
    |> should equal (func (func pair (var "b")) (var "b"))
    // A function that is an item has brackets of its own, wherever items are separated by commas.
    annotationOf "def p: (int, (a -> b)) = (1, { x -> x })"
    |> should equal (tuple [ named "int"; func (var "a") (var "b") ])
    annotationOf "def m: map(string, (a -> b)) = m" |> should equal (ty (TypeApply ("map", [ named "string"; func (var "a") (var "b") ])))
    annotationOf "def m: map(string, a -> b) = m" |> should equal (ty (TypeApply ("map", [ func (tuple [ named "string"; var "a" ]) (var "b") ])))
    annotationOf "def t: #pair(int, a) = t" |> should equal (tag "pair" (Some pair))
    annotationOf "def t: #apply(int, a -> b) = t" |> should equal (tag "apply" (Some (func pair (var "b"))))
    annotationOf "def t: #field(name: int) = t" |> should equal (tag "field" (Some (ty (TypeRecord [ TypeField ("name", false, false, Some (named "int"), None) ]))))
    // A union binds tighter than the arrow, so it needs no brackets on either side of one.
    let union = ty (TypeUnion [ tag "some" (Some (named "int")); tag "none" None ])
    annotationOf "def u: #some(int) | #none -> #some(int) | #none = u" |> should equal (func union union)
    // A record field's and a lambda parameter's commas are their own, so a function there brackets its parameters.
    annotationOf "def r: (combine: (a, a) -> a) = r"
    |> should equal (ty (TypeRecord [ TypeField ("combine", false, false, Some (func (tuple [ var "a"; var "a" ]) (var "a")), None) ]))

[<Fact>]
let ``a lone record type argument may be written in the application's own brackets`` () =
    let annotationOf source =
        match parseModule source with
        | Result.Ok [ Kind (Def (ValueDef (_, _, Some annotation, _))) ] -> annotation
        | other -> failwith $"Unexpected parse: %A{other}"
    let field name typeName = TypeField (name, false, false, Some (named typeName), None)
    let people = ty (TypeApply ("list", [ ty (TypeRecord [ field "name" "string"; field "age" "int" ]) ]))
    annotationOf "def p: list(name: string, age: int) = p" |> should equal people
    annotationOf "def p: list((name: string, age: int)) = p" |> should equal people
    // Other arguments are not affected.
    annotationOf "def m: map(string, int) = m" |> should equal (ty (TypeApply ("map", [ named "string"; named "int" ])))

[<Fact>]
let ``a record type may start with positional fields, named by position`` () =
    let annotationOf source =
        match parseModule source with
        | Result.Ok [ Kind (Def (ValueDef (_, _, Some annotation, _))) ] -> annotation
        | other -> failwith $"Unexpected parse: %A{other}"
    let field name typeValue = TypeField (name, false, false, Some typeValue, None)
    let record fields = ty (TypeRecord fields)
    annotationOf "def g: (int, step: int) -> int = g"
    |> should equal (func (record [ field "0" (named "int"); field "step" (named "int") ]) (named "int"))
    annotationOf "def k: (string, a, on: string) -> b = k"
    |> should equal (func (record [ field "0" (named "string"); field "1" (var "a"); field "on" (named "string") ]) (var "b"))
    // A positional field is a record field like any other, so a function there needs no brackets of its own.
    annotationOf "def r: (a -> b, n: int) = r"
    |> should equal (record [ field "0" (func (var "a") (var "b")); field "n" (named "int") ])

[<Fact>]
let ``lambda parameters still split at commas`` () =
    match parseModule "def f = { x: int, y -> x + y }" with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", None, Kind (Lambda ([ ("x", Some (Kind (TypeName "int"))); ("y", None) ], _, _))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``a bracketed lambda parameter's annotation ends at the bracket`` () =
    let parametersOf source =
        match parseModule source with
        | Result.Ok [ Kind (Def (ValueDef (_, _, None, Kind (Lambda (parameters, _, _))))) ] -> parameters
        | other -> failwith $"Unexpected parse: %A{other}"
    parametersOf "def f = { (x: int) -> x + 1 }" |> should equal [ ("x", Some (named "int")) ]
    parametersOf "def f = { (x: int -> int) -> x(1) }" |> should equal [ ("x", Some (func (named "int") (named "int"))) ]
    parametersOf "def f = { (x: int), y, (z: list(a)) -> x }" |> should equal [ ("x", Some (named "int")); ("y", None); ("z", Some (ty (TypeApply ("list", [ var "a" ])))) ]
    parametersOf "def f = { (x) -> x }" |> should equal [ ("x", (None: TypeExpr option)) ]

[<Fact>]
let ``an unbracketed lambda parameter's annotation ends at the arrow (#272)`` () =
    let parametersOf source =
        match parseModule source with
        | Result.Ok [ Kind (Def (ValueDef (_, _, None, Kind (Lambda (parameters, Kind body, _))))) ] -> parameters, body
        | other -> failwith $"Unexpected parse: %A{other}"
    parametersOf "def f = { q: Point -> q }"
    |> should equal ([ ("q", Some (named "Point")) ], IdentifierExpr "q")
    parametersOf "def f = { x: int, y: list(a) -> y }"
    |> should equal ([ ("x", Some (named "int")); ("y", Some (ty (TypeApply ("list", [ var "a" ])))) ], IdentifierExpr "y")
    parametersOf "def f = { x: int | string -> x }"
    |> should equal ([ ("x", Some (ty (TypeUnion [ named "int"; named "string" ]))) ], IdentifierExpr "x")
    // A function type there has brackets of its own.
    parametersOf "def f = { g: (int -> int) -> g }"
    |> should equal ([ ("g", Some (func (named "int") (named "int"))) ], IdentifierExpr "g")

[<Fact>]
let ``typeof names a value as a type`` () =
    let annotationOf source =
        match parseModule source with
        | Result.Ok [ Kind (Def (ValueDef (_, _, Some annotation, _))) ] -> annotation
        | other -> failwith $"Unexpected parse: %A{other}"
    let value name = Expression.synthetic (IdentifierExpr name)
    let typeOf name = ty (TypeOf (value name))
    let defaults = typeOf "defaults"
    annotationOf "def o: typeof defaults = d" |> should equal defaults
    // Brackets only group the value.
    annotationOf "def o: typeof(defaults) = d" |> should equal defaults
    annotationOf "def o: typeof ( defaults ) = d" |> should equal defaults
    // A path of fields names the value of a field.
    annotationOf "def p: typeof config.server.port = p"
    |> should equal (ty (TypeOf (Expression.synthetic (MemberAccess (Expression.synthetic (MemberAccess (value "config", "server", None)), "port", None)))))
    // It is a type like any other, and binds as tightly as one.
    annotationOf "def o: typeof x? = o" |> should equal (ty (TypeOptional (typeOf "x")))
    annotationOf "def f: typeof x -> typeof y = f" |> should equal (func (typeOf "x") (typeOf "y"))
    annotationOf "def l: list(typeof x) = l" |> should equal (ty (TypeApply ("list", [ typeOf "x" ])))

[<Fact>]
let ``typeof is a keyword only on its own`` () =
    match parseModule "def t: typeofThing = t" with
    | Result.Ok [ Kind (Def (ValueDef (_, _, Some (Kind (TypeName "typeofThing")), _))) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``a record field's name may start with a keyword`` () =
    let annotationOf source =
        match parseModule source with
        | Result.Ok [ Kind (Def (ValueDef (_, _, Some annotation, _))) ] -> annotation
        | other -> failwith $"Unexpected parse: %A{other}"
    let field name isMutable = TypeField (name, isMutable, false, Some (named "string"), None)
    annotationOf "def r: (typeName: string) = r" |> should equal (ty (TypeRecord [ field "typeName" false ]))
    annotationOf "def r: (mutable: string) = r" |> should equal (ty (TypeRecord [ field "mutable" false ]))
    annotationOf "def r: (mut typeName: string) = r" |> should equal (ty (TypeRecord [ field "typeName" true ]))
    annotationOf "def r: (type Item = int, typeName: string) = r"
    |> should equal (ty (TypeRecord [ TypeMember ("Item", Some (named "int")); field "typeName" false ]))

[<Fact>]
let ``typeof in a module block names the block's own value`` () =
    let source =
        "module config =\n" +
        "  def defaults = (port = 80)\n" +
        "  def custom: typeof defaults = (port = 8080)\n"
    match parseModule source with
    | Result.Ok items ->
        let annotation =
            items
            |> List.pick (function
                | Kind (Def (ValueDef (_, "config.custom", Some annotation, _))) -> Some annotation
                | _ -> None)
        annotation |> should equal (ty (TypeOf (Expression.synthetic (IdentifierExpr "config.defaults"))))
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``an underscore in a type is a hole`` () =
    let annotationOf source =
        match parseModule source with
        | Result.Ok [ Kind (Def (ValueDef (_, _, Some annotation, _))) ] -> annotation
        | other -> failwith $"Unexpected parse: %A{other}"
    let hole = ty TypeHole
    annotationOf "def xs: list(_) = [1, 2]" |> should equal (ty (TypeApply ("list", [ hole ])))
    annotationOf "def f: (int, _) -> _ = { a, b -> a + b }" |> should equal (func (tuple [ named "int"; hole ]) hole)
    annotationOf "def m: map(string, _) = m" |> should equal (ty (TypeApply ("map", [ named "string"; hole ])))
    annotationOf "def x: _ = 1" |> should equal hole
    // Only `_` alone: a name that starts with one is a type's name.
    annotationOf "def x: _a = 1" |> should equal (named "_a")
