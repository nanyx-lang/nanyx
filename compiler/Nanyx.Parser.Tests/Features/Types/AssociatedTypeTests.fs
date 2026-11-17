module ParserTests.Features.AssociatedTypes

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

// Associated types: `elem: type` in a context, `Iter(c, elem = string)` and `Iter(c).elem` in a signature, and
// `type Bag.elem = int` attached to a type (see the Contexts page).

let private ty kind = TypeExpr.synthetic kind
let private named name = ty (TypeName name)
let private var name = ty (TypeVar name)

let private annotationOf source =
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, Some annotation, _))) ] -> annotation
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A context's member of kind type is written elem: type`` () =
    match parseModule "context Iter(c) = (elem: type, next: c -> elem)\n" with
    | Result.Ok [ Kind (Def (TypeDef ("Iter", _, [ "c", None ], Kind (TypeRecord [ TypeField ("elem", false, Some keyword, None); _ ])))) ] ->
        keyword |> should equal (ty TypeKeyword)
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A context's associated type is fixed by a named argument`` () =
    annotationOf "def f: [Iter(c, elem = string)] c -> () = f"
    |> should equal (ty (TypeWithContext ([ ty (TypeApply ("Iter", [ var "c"; ty (TypeNamedArgument ("elem", named "string")) ])) ], ty (TypeFunc (var "c", ty TypeUnit)))))

[<Fact>]
let ``A context's associated type is named after the context`` () =
    annotationOf "def f: [Iter(c)] c -> Iter(c).elem = f"
    |> should equal (ty (TypeWithContext ([ ty (TypeApply ("Iter", [ var "c" ])) ], ty (TypeFunc (var "c", ty (TypeAssociated (ty (TypeApply ("Iter", [ var "c" ])), "elem")))))))

[<Fact>]
let ``A type's associated type is declared attached to it`` () =
    match parseModule "type Bag.elem = int\n" with
    | Result.Ok [ Kind (Def (TypeDef ("Bag.elem", _, [], body))) ] -> body |> should equal (named "int")
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A use fixes an associated type by name`` () =
    match parseModule "def f = {\n  use Sized(Bag, elem = int)(size = s)\n  1\n}\n" with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", None, Kind (Lambda (_, Kind (Block (statement :: _)), _))))) ] ->
        match statement.Kind with
        | UseStatement (Kind (UseInstanceSyntax.Instance (context, _))) ->
            context |> should equal (ty (TypeApply ("Sized", [ named "Bag"; ty (TypeNamedArgument ("elem", named "int")) ])))
        | other -> failwith $"Unexpected statement: %A{other}"
    | other -> failwith $"Unexpected parse: %A{other}"
