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
let ``Unifier rejects occurs check`` () =
    let tvar = TyVar { Id = 2; Name = None }
    let constraintSet: ConstraintSet = [ (tvar, TyFunc(tvar, TyPrimitive "int"), Equal, SourceSpan.none) ]
    let result = Unifier.unify constraintSet
    match result with
    | Ok _ -> Assert.True(false, "Expected occurs check failure")
    | Error (message, _) -> Assert.Contains("Occurs", message)

[<Fact>]
let ``Unifier allows record width subtyping`` () =
    let narrow = TyRecord (Map.ofList [ ("a", TyPrimitive "int") ])
    let wide = TyRecord (Map.ofList [ ("a", TyPrimitive "int"); ("b", TyPrimitive "string") ])
    let result = Unifier.unify [ (narrow, wide, Equal, SourceSpan.none) ]
    match result with
    | Ok _ -> Assert.True(true)
    | Error (message, _) -> Assert.True(false, message)

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
