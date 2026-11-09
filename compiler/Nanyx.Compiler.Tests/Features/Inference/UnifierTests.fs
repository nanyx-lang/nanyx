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
