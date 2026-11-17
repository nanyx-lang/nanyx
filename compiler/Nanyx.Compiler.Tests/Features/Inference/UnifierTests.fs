module Nanyx.Compiler.Tests.Features.Inference.Unifier

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Unifier binds type variables`` () =
    let tvar = TyVar (TyVar.ordinary 1 None)
    let constraints: ConstraintSet = [ (tvar, TyPrimitive "string", Equal, SourceSpan.none) ]
    let result = Solving.unify constraints
    match result with
    | Ok subst -> Assert.Equal(Some (TyPrimitive "string"), subst |> Map.tryFind 1)
    | Error (message, _) -> Assert.True(false, message)

[<Fact>]
let ``Applying a definition's parameters replaces each once, even by a variable with another parameter's id`` () =
    // An imported context's parameters are numbered by its own module: here `e` is applied to this module's variable 1,
    // which has the id of the parameter `a`. It must not then be replaced by what `a` is.
    let a = TyVar.ordinary 1 (Some "a")
    let e = TyVar.ordinary 2 (Some "e")
    let local = TyVar (TyVar.ordinary 1 None)
    let applied = Unifier.applyToParameters [ a; e ] [ TyPrimitive "int"; local ] (TyFunc(TyVar a, TyVar e, []))
    Assert.Equal(TyFunc(TyPrimitive "int", local, []), applied)

[<Fact>]
let ``Unifier rejects occurs check`` () =
    let tvar = TyVar (TyVar.ordinary 2 None)
    let constraintSet: ConstraintSet = [ (tvar, TyFunc(tvar, TyPrimitive "int", []), Equal, SourceSpan.none) ]
    let result = Solving.unify constraintSet
    match result with
    | Ok _ -> Assert.True(false, "Expected occurs check failure")
    | Error (message, _) -> Assert.EndsWith("which contains it, so the type would be infinite", message)

[<Fact>]
let ``Unifier rejects closed records with different fields, either way round`` () =
    let narrow = TyRecord(Map.ofList [ ("a", TyPrimitive "int") ], FieldOrder.none)
    let wide = TyRecord(Map.ofList [ ("a", TyPrimitive "int"); ("b", TyPrimitive "string") ], FieldOrder.none)
    for kind in [ Equal; Assignable ] do
        for given, expected in [ narrow, wide; wide, narrow ] do
            match Solving.unify [ (given, expected, kind, SourceSpan.none) ] with
            | Ok _ -> Assert.Fail($"{kind}: {given} should not unify with {expected}")
            | Error (message, _) -> Assert.StartsWith("Record field mismatch:", message)

[<Fact>]
let ``Unifier gives an open record's row the other fields of a closed one`` () =
    let row = TyVar.ordinary 9 None
    let opened = TyOpenRecord (Map.ofList [ ("a", TyPrimitive "int") ], row)
    let wide = TyRecord(Map.ofList [ ("a", TyPrimitive "int"); ("b", TyPrimitive "string") ], FieldOrder.none)
    match Solving.unify [ (wide, opened, Assignable, SourceSpan.none) ] with
    | Ok subst -> Assert.Equal(Some (TyRecord(Map.ofList [ ("b", TyPrimitive "string") ], FieldOrder.none)), subst |> Map.tryFind row.Id)
    | Error (message, _) -> Assert.Fail(message)

[<Fact>]
let ``A lone tag equal to an open union that names it leaves the union's rest empty`` () =
    let rest = TyVar.ordinary 7 None
    let named = TyVar.ordinary 8 (Some "r")
    let notFound = TyTag("notFound", None)
    match Solving.unify [ (notFound, TyUnion [ notFound; TyVar rest ], Equal, SourceSpan.none) ] with
    | Ok subst -> Assert.Equal(Some (TyUnion []), subst |> Map.tryFind rest.Id)
    | Error (message, _) -> Assert.True(false, message)
    // Given where the union is expected, the tag says nothing of the rest; nor does it of a rest an annotation names.
    match Solving.unify [ (notFound, TyUnion [ notFound; TyVar rest ], Assignable, SourceSpan.none) ] with
    | Ok subst -> Assert.Equal(None, subst |> Map.tryFind rest.Id)
    | Error (message, _) -> Assert.True(false, message)
    match Solving.unify [ (notFound, TyUnion [ notFound; TyVar named ], Equal, SourceSpan.none) ] with
    | Ok subst -> Assert.Equal(None, subst |> Map.tryFind named.Id)
    | Error (message, _) -> Assert.True(false, message)

[<Fact>]
let ``A union leaves out a tag whose payload has no values, and a tag its rest repeats`` () =
    let payload = TyVar.ordinary 9 None
    let rest = TyVar.ordinary 10 None
    let some = TyTag("some", Some (TyPrimitive "int"))
    let subst = Map.ofList [ payload.Id, TyUnion []; rest.Id, some ]
    Assert.Equal(some, Unifier.apply subst (TyUnion [ some; TyTag("error", Some (TyVar payload)); TyVar rest ]))

[<Fact>]
let ``Unifier matches unions by set and width`` () =
    let unionA = TyUnion [ TyTag("some", Some (TyPrimitive "int")); TyTag("notFound", None) ]
    let unionB = TyUnion [ TyTag("notFound", None); TyTag("some", Some (TyPrimitive "int")) ]
    let unionWide = TyUnion [ TyTag("some", Some (TyPrimitive "int")); TyTag("notFound", None); TyTag("error", Some (TyPrimitive "string")) ]
    let resultOrder = Solving.unify [ (unionA, unionB, Equal, SourceSpan.none) ]
    let resultWidth = Solving.unify [ (unionA, unionWide, Equal, SourceSpan.none) ]
    match resultOrder with
    | Ok _ -> ()
    | Error (message, _) -> Assert.True(false, message)
    match resultWidth with
    | Ok _ -> ()
    | Error (message, _) -> Assert.True(false, message)

[<Fact>]
let ``Solving carries on past a conflict to find the next`` () =
    let spanAt start = { SourceSpan.none with Start = start; End = start + 1 }
    let a = TyVar (TyVar.ordinary 1 None)
    let b = TyVar (TyVar.ordinary 2 None)
    let constraints: ConstraintSet =
        [ (a, TyPrimitive "int", Equal, spanAt 0)
          (a, TyPrimitive "string", Equal, spanAt 10)
          (b, TyPrimitive "bool", Equal, spanAt 20)
          (b, TyPrimitive "int", Equal, spanAt 30) ]
    let subst, failures = Solving.solve constraints
    Assert.Equal<int list>([ 10; 30 ], failures |> List.map (fun (_, span) -> span.Start))
    // What the other constraints say still holds.
    Assert.Equal(Some (TyPrimitive "int"), subst |> Map.tryFind 1)
    Assert.Equal(Some (TyPrimitive "bool"), subst |> Map.tryFind 2)

[<Fact>]
let ``Solving constraints that agree gives what unify gives`` () =
    let a = TyVar (TyVar.ordinary 1 None)
    let constraints: ConstraintSet = [ (a, TyFunc(TyPrimitive "int", TyPrimitive "string", []), Equal, SourceSpan.none) ]
    let subst, failures = Solving.solve constraints
    Assert.Empty(failures)
    Assert.Equal(Solving.unify constraints, Ok subst)

/// What unifying `newer` in front of `older` gives once `older` has been solved, and what solving the same constraints
/// afresh gives: a copy of `older` has not been solved, so nothing is solved on top of it.
let private solvedOnTopAndAfresh (newer: ConstraintSet) (older: ConstraintSet) =
    Solving.unify older |> ignore
    sprintf "%A" (Solving.unify (newer @ older)), sprintf "%A" (Solving.unify (newer @ List.map id older))

[<Fact>]
let ``Constraints in front of a list whose union was put off give what solving them afresh does`` () =
    // `#a` given where `v` is expected is put off to the end (see `AssignableLater`).
    let v = TyVar (TyVar.ordinary 1 None)
    let older: ConstraintSet = [ (TyTag("a", None), v, Assignable, SourceSpan.none) ]
    // Constraints that share no variable with it.
    let onTop, afresh = solvedOnTopAndAfresh [ (TyVar (TyVar.ordinary 5 None), TyPrimitive "int", Equal, SourceSpan.none) ] older
    Assert.Equal(afresh, onTop)
    // Constraints on its variable, solved first when solved afresh: `v` is `#b | r`, and `#a` widens it into `r`.
    let onTop, afresh =
        solvedOnTopAndAfresh [ (v, TyUnion [ TyTag("b", None); TyVar (TyVar.ordinary 3 None) ], Equal, SourceSpan.none) ] older
    Assert.DoesNotContain("Error", afresh)
    Assert.Equal(afresh, onTop)

/// What `vars` resolve to, and the failure if any, when `newer` is unified in front of `older` once `older` is a
/// checkpoint, and when the same constraints are unified afresh: a copy of `older` is no checkpoint.
let private onCheckpointAndAfresh (vars: int list) (newer: ConstraintSet) (older: ConstraintSet) =
    let shown result =
        match result with
        | Ok subst -> vars |> List.map (fun id -> Unifier.tyToString (Unifier.apply subst (TyVar (TyVar.ordinary id None)))) |> String.concat "; "
        | Error (message: string, span: SourceSpan) -> $"{message} at {span.Start}"
    Solving.checkpoint older
    shown (Solving.unify (newer @ older)), shown (Solving.unify (newer @ List.map id older))

[<Fact>]
let ``Constraints in front of a checkpoint give what solving them afresh does`` () =
    let spanAt start = { SourceSpan.none with Start = start; End = start + 1 }
    let v, w = TyVar (TyVar.ordinary 1 None), TyVar (TyVar.ordinary 2 None)
    // `#a` and `#b` given where `v` is expected: a union whose tags come in the order the constraints are solved.
    let older: ConstraintSet = [ (TyTag("a", None), v, Assignable, spanAt 0); (TyTag("b", None), v, Assignable, spanAt 1) ]
    // Constraints that share no variable with it.
    let onCheckpoint, afresh =
        onCheckpointAndAfresh [ 1; 2 ] [ (TyTag("c", None), w, Assignable, spanAt 2); (TyTag("d", None), w, Assignable, spanAt 3) ] older
    Assert.Equal(afresh, onCheckpoint)
    // One that fails, which is reported where solving afresh reports it.
    let onCheckpoint, afresh =
        onCheckpointAndAfresh [ 2 ] [ (w, TyPrimitive "int", Equal, spanAt 4); (w, TyPrimitive "string", Equal, spanAt 5) ] older
    Assert.StartsWith("Type mismatch", afresh)
    Assert.Equal(afresh, onCheckpoint)
    // Constraints on its variable, which solving afresh solves first: they are not solved on top of it.
    let onCheckpoint, afresh = onCheckpointAndAfresh [ 1 ] [ (TyTag("c", None), v, Assignable, spanAt 6) ] older
    Assert.Equal(afresh, onCheckpoint)

[<Fact>]
let ``A union's member that fits is not blamed for a failure in a later constraint`` () =
    // `#none` fits `#none | #some(int)`; the conflict is `int` and `string`, solved after it. Trying `#some(int)` as
    // well, which does not fit, must not make the union the one reported.
    let atTag = { SourceSpan.none with Start = 10; StartLine = 1 }
    let atConflict = { SourceSpan.none with Start = 20; StartLine = 2 }
    let union = TyUnion [ TyTag("none", None); TyTag("some", Some (TyPrimitive "int")) ]
    let conflict = (TyPrimitive "int", TyPrimitive "string", Equal, atConflict)
    for tagConstraint in [ (TyTag("none", None), union, Assignable, atTag); (union, TyTag("none", None), Equal, atTag) ] do
        match Solving.unify [ tagConstraint; conflict ] with
        | Ok _ -> Assert.Fail "Expected the conflict to fail"
        | Error (message, span) ->
            Assert.Equal("Type mismatch: 'int' vs 'string'", message)
            Assert.Equal(atConflict, span)
    // With every member failing at the union itself, the union is still what is reported.
    match Solving.unify [ (TyTag("other", None), union, Assignable, atTag) ] with
    | Ok _ -> Assert.Fail "Expected the tag not to fit"
    | Error (_, span) -> Assert.Equal(atTag, span)
