module Nanyx.Compiler.Tests.Features.Inference.Unifier

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Unifier binds type variables`` () =
    let tvar = TyVar { Id = 1; Name = None }
    let constraints: ConstraintSet = [ (tvar, TyPrimitive "string", Equal, SourceSpan.none) ]
    let result = Unifier.unify constraints
    match result with
    | Ok subst -> Assert.Equal(Some (TyPrimitive "string"), subst |> Map.tryFind 1)
    | Error (message, _) -> Assert.True(false, message)

[<Fact>]
let ``Applying a definition's parameters replaces each once, even by a variable with another parameter's id`` () =
    // An imported context's parameters are numbered by its own module: here `e` is applied to this module's variable 1,
    // which has the id of the parameter `a`. It must not then be replaced by what `a` is.
    let a = { Id = 1; Name = Some "a" }
    let e = { Id = 2; Name = Some "e" }
    let local = TyVar { Id = 1; Name = None }
    let applied = Unifier.applyToParameters [ a; e ] [ TyPrimitive "int"; local ] (TyFunc(TyVar a, TyVar e))
    Assert.Equal(TyFunc(TyPrimitive "int", local), applied)

[<Fact>]
let ``Unifier rejects occurs check`` () =
    let tvar = TyVar { Id = 2; Name = None }
    let constraintSet: ConstraintSet = [ (tvar, TyFunc(tvar, TyPrimitive "int"), Equal, SourceSpan.none) ]
    let result = Unifier.unify constraintSet
    match result with
    | Ok _ -> Assert.True(false, "Expected occurs check failure")
    | Error (message, _) -> Assert.Contains("Occurs", message)

[<Fact>]
let ``Unifier rejects closed records with different fields, either way round`` () =
    let narrow = TyRecord (Map.ofList [ ("a", TyPrimitive "int") ])
    let wide = TyRecord (Map.ofList [ ("a", TyPrimitive "int"); ("b", TyPrimitive "string") ])
    for kind in [ Equal; Assignable ] do
        for given, expected in [ narrow, wide; wide, narrow ] do
            match Unifier.unify [ (given, expected, kind, SourceSpan.none) ] with
            | Ok _ -> Assert.Fail($"{kind}: {given} should not unify with {expected}")
            | Error (message, _) -> Assert.StartsWith("Record field mismatch:", message)

[<Fact>]
let ``Unifier gives an open record's row the other fields of a closed one`` () =
    let row = { Id = 9; Name = None }
    let opened = TyOpenRecord (Map.ofList [ ("a", TyPrimitive "int") ], row)
    let wide = TyRecord (Map.ofList [ ("a", TyPrimitive "int"); ("b", TyPrimitive "string") ])
    match Unifier.unify [ (wide, opened, Assignable, SourceSpan.none) ] with
    | Ok subst -> Assert.Equal(Some (TyRecord (Map.ofList [ ("b", TyPrimitive "string") ])), subst |> Map.tryFind row.Id)
    | Error (message, _) -> Assert.Fail(message)

[<Fact>]
let ``A lone tag equal to an open union that names it leaves the union's rest empty`` () =
    let rest = { Id = 7; Name = None }
    let named = { Id = 8; Name = Some "r" }
    let notFound = TyTag("notFound", None)
    match Unifier.unify [ (notFound, TyUnion [ notFound; TyVar rest ], Equal, SourceSpan.none) ] with
    | Ok subst -> Assert.Equal(Some (TyUnion []), subst |> Map.tryFind rest.Id)
    | Error (message, _) -> Assert.True(false, message)
    // Given where the union is expected, the tag says nothing of the rest; nor does it of a rest an annotation names.
    match Unifier.unify [ (notFound, TyUnion [ notFound; TyVar rest ], Assignable, SourceSpan.none) ] with
    | Ok subst -> Assert.Equal(None, subst |> Map.tryFind rest.Id)
    | Error (message, _) -> Assert.True(false, message)
    match Unifier.unify [ (notFound, TyUnion [ notFound; TyVar named ], Equal, SourceSpan.none) ] with
    | Ok subst -> Assert.Equal(None, subst |> Map.tryFind named.Id)
    | Error (message, _) -> Assert.True(false, message)

[<Fact>]
let ``A union leaves out a tag whose payload has no values, and a tag its rest repeats`` () =
    let payload = { Id = 9; Name = None }
    let rest = { Id = 10; Name = None }
    let some = TyTag("some", Some (TyPrimitive "int"))
    let subst = Map.ofList [ payload.Id, TyUnion []; rest.Id, some ]
    Assert.Equal(some, Unifier.apply subst (TyUnion [ some; TyTag("error", Some (TyVar payload)); TyVar rest ]))

[<Fact>]
let ``Unifier matches unions by set and width`` () =
    let unionA = TyUnion [ TyTag("some", Some (TyPrimitive "int")); TyTag("notFound", None) ]
    let unionB = TyUnion [ TyTag("notFound", None); TyTag("some", Some (TyPrimitive "int")) ]
    let unionWide = TyUnion [ TyTag("some", Some (TyPrimitive "int")); TyTag("notFound", None); TyTag("error", Some (TyPrimitive "string")) ]
    let resultOrder = Unifier.unify [ (unionA, unionB, Equal, SourceSpan.none) ]
    let resultWidth = Unifier.unify [ (unionA, unionWide, Equal, SourceSpan.none) ]
    match resultOrder with
    | Ok _ -> Assert.True(true)
    | Error (message, _) -> Assert.True(false, message)
    match resultWidth with
    | Ok _ -> Assert.True(true)
    | Error (message, _) -> Assert.True(false, message)

[<Fact>]
let ``Solving carries on past a conflict to find the next`` () =
    let spanAt start = { SourceSpan.none with Start = start; End = start + 1 }
    let a = TyVar { Id = 1; Name = None }
    let b = TyVar { Id = 2; Name = None }
    let constraints: ConstraintSet =
        [ (a, TyPrimitive "int", Equal, spanAt 0)
          (a, TyPrimitive "string", Equal, spanAt 10)
          (b, TyPrimitive "bool", Equal, spanAt 20)
          (b, TyPrimitive "int", Equal, spanAt 30) ]
    let subst, failures = Unifier.solve constraints
    Assert.Equal<int list>([ 10; 30 ], failures |> List.map (fun (_, span) -> span.Start))
    // What the other constraints say still holds.
    Assert.Equal(Some (TyPrimitive "int"), subst |> Map.tryFind 1)
    Assert.Equal(Some (TyPrimitive "bool"), subst |> Map.tryFind 2)

[<Fact>]
let ``Solving constraints that agree gives what unify gives`` () =
    let a = TyVar { Id = 1; Name = None }
    let constraints: ConstraintSet = [ (a, TyFunc(TyPrimitive "int", TyPrimitive "string"), Equal, SourceSpan.none) ]
    let subst, failures = Unifier.solve constraints
    Assert.Empty(failures)
    Assert.Equal(Unifier.unify constraints, Ok subst)

[<Fact>]
let ``A union's member that fits is not blamed for a failure in a later constraint`` () =
    // `#none` fits `#none | #some(int)`; the conflict is `int` and `string`, solved after it. Trying `#some(int)` as
    // well, which does not fit, must not make the union the one reported.
    let atTag = { SourceSpan.none with Start = 10; StartLine = 1 }
    let atConflict = { SourceSpan.none with Start = 20; StartLine = 2 }
    let union = TyUnion [ TyTag("none", None); TyTag("some", Some (TyPrimitive "int")) ]
    let conflict = (TyPrimitive "int", TyPrimitive "string", Equal, atConflict)
    for tagConstraint in [ (TyTag("none", None), union, Assignable, atTag); (union, TyTag("none", None), Equal, atTag) ] do
        match Unifier.unify [ tagConstraint; conflict ] with
        | Ok _ -> Assert.Fail "Expected the conflict to fail"
        | Error (message, span) ->
            Assert.Equal("Type mismatch: int vs string.", message)
            Assert.Equal(atConflict, span)
    // With every member failing at the union itself, the union is still what is reported.
    match Unifier.unify [ (TyTag("other", None), union, Assignable, atTag) ] with
    | Ok _ -> Assert.Fail "Expected the tag not to fit"
    | Error (_, span) -> Assert.Equal(atTag, span)
