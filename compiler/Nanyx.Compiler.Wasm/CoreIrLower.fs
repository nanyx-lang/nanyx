/// Lowers the typed AST to the core IR (`CoreIr.fs`), specialising generic functions as it goes.
///
/// Each definition whose type is concrete is lowered as a root. A call to a generic definition asks for its
/// specialisation at the call's type arguments, and on the context members it knows (see `CoreIrLowerKnown`), which
/// is lowered in turn (and may ask for more). Every function is
/// compiled from the IR. What it cannot lower yet (`cannot`) is reported by `CodeGen.fs` as `UnsupportedByBackend`
/// where it is written, and a broken invariant of the backend (`internalError`) as `BackendInternalError`: see "What
/// cannot be compiled" in `docs/src/content/deep-dives/memory-management.md`.
module Transpiler.Wasm.CoreIrLower

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.Wat
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes
open Transpiler.Wasm.CoreIrLowerSyntax
open Transpiler.Wasm.CoreIrLowerInput
open Transpiler.Wasm.CoreIrLowerKnown
open Transpiler.Wasm.CoreIrLowerScope

/// Lowers each function, asking for the specialisations its calls need through `request`.
type internal FunctionLowering(input: LowerInput, definitions: Map<string, Expression>, subst: Map<int, Ty>, self: SpecKey, request: SpecKey -> Map<int, Ty> -> Lowering -> string) =
    // The type arguments of the specialisation, and the variables of the generic locals lowered at the one use they have
    // (see `Block`).
    let mutable subst = subst
    let mutable nextId = 0
    let usedNames = System.Collections.Generic.HashSet<string>()
    // What `storesBeforeValue` found of each expression, by the node itself.
    let storesFound = System.Collections.Generic.Dictionary<Expression, bool>(HashIdentity.Reference)
    // The locals that stand for the members the function is specialised on, by id (see `CoreIrLowerKnown`).
    let known = System.Collections.Generic.Dictionary<int, Var * KnownMember>()
    // The locals a `def` gives the value of a top-level function, by id: a `use` that binds one to a context member
    // makes the member known where it is in scope, and a call through one calls the function directly.
    let definedKnown = System.Collections.Generic.Dictionary<int, KnownMember>()

    /// The member the local `var` is known to be, if it is.
    let knownOf (var: Var) =
        match known.TryGetValue var.Id, definedKnown.TryGetValue var.Id with
        | (true, (_, knownMember)), _
        | _, (true, knownMember) -> Some knownMember
        | _ -> None

    let newVar (name: string) (kind: VarKind) (ty: IrType) =
        nextId <- nextId + 1
        { Id = nextId; Name = name; Kind = kind; Type = ty }

    let names = NameSupply(usedNames)

    /// A parameter's or local's name: as written, made unique within the function.
    let uniqueName (name: string) = names.Fresh(sanitizeIdentifier name)

    /// A new local of type `ty`, its name `name` made unique: each call makes another.
    let newLocal (name: string) (ty: IrType) = newVar (uniqueName name) Local ty

    let newTemp (ty: IrType) =
        let var = newVar "" Temp ty
        { var with Name = $"__t{var.Id}" }

    let toIrType = toIrTypeIn input.ContextLayouts
    let declaredMemberTypes = declaredMemberTypes input.ContextLayouts

    /// The type the member of the key `key` that a `use` binds to the local `local` has: the one in the layout of the
    /// context the `use` names, or else the one context that has a member of the key declares.
    let bindingMemberType (key: ContextMemberKey) (local: string) =
        match input.UseBindingContexts |> Map.tryFind local |> Option.bind (fun context -> input.ContextLayouts.Plain |> Map.tryFind context) with
        | Some layout -> layout |> List.tryFind (fst >> (=) key) |> Option.map snd
        | None -> declaredMemberTypes |> Map.tryFind key

    /// What a `dbg` printer needs from this function's lowering (see `CoreIrLowerDbg`).
    let printerTools: CoreIrLowerDbg.PrinterTools =
        { NewVar = fun name kind ty -> newVar (uniqueName name) kind ty
          NewTemp = newTemp
          PrinterOf = fun printer -> request (SpecKey.plain (CoreIrLowerDbg.key printer) []) Map.empty (Printing printer)
          ToIrType = toIrType; Budgeted = input.Probes }

    // In a debug build, the name and type of each parameter and `def` local, by id: those a probe writes (see
    // `CoreIrProbes`).
    let probedLocals = System.Collections.Generic.Dictionary<int, string * Ty>()

    // The parameters a probe writes, in order.
    let probedParameters = ResizeArray<Var>()

    /// Notes that a probe writes `var`, named `name` and of `ty`, in a debug build, if a printer can write it.
    let noteProbed (name: string) (var: Var) (ty: Ty) =
        let ty = unconstrainedAsUnit ty
        // A cell's local holds its box, not the value its type is the type of.
        if input.Probes && not (MadeUpNames.isMadeUp name) && CoreIrLowerDbg.printable toIrType ty && toIrType ty = var.Type then
            probedLocals.[var.Id] <- (name, ty)
            if var.Kind = Param then probedParameters.Add var

    /// What a statement's probe writes in `scope`: the parameters, and the `probeLimit` locals bound last, of those in
    /// scope, by name. A probe that wrote every local would make a function of n locals n² in size (#2488).
    let probedInScope (scope: Scope) : Var list =
        let nameOf (var: Var) = fst probedLocals.[var.Id]
        let inScope (var: Var) = scope.Vars |> Map.tryFind (nameOf var) |> Option.exists (fun bound -> bound.Id = var.Id)
        // A local that a later one of its name hides is passed over, but only so many, so that a long run of them
        // costs no more than the limit.
        let rec nearest (found: Var list) (count: int) (looked: int) (remaining: Var list) =
            match remaining with
            | var :: rest when count < CoreIrProbes.probeLimit && looked < 4 * CoreIrProbes.probeLimit ->
                if inScope var then nearest (var :: found) (count + 1) (looked + 1) rest
                else nearest found count (looked + 1) rest
            | _ -> found
        (probedParameters |> Seq.filter inScope |> List.ofSeq) @ nearest [] 0 0 scope.Probed
        |> List.sortWith (fun a b -> System.String.CompareOrdinal(nameOf a, nameOf b))

    /// What a probe writes of `vars`: those `noteProbed` noted, each with its type's printer.
    let probeLocals (vars: Var seq) : ProbeLocal list =
        vars
        |> Seq.choose (fun var ->
            match probedLocals.TryGetValue var.Id with
            | true, (name, ty) -> Some { Name = name; Var = var; Printer = printerTools.PrinterOf (CoreIrLowerDbg.Showing ty) }
            | _ -> None)
        |> List.ofSeq

    let wrappedOf = wrappedOf input

    let tyOf (expr: Expression) : IrType =
        match input.ExprTypes |> Map.tryFind expr.Id with
        | Some ty -> toIrType (substitute subst ty)
        | None -> cannot "an expression without a type"

    /// The type of `expr` as the type checker has it, at the function's type arguments, unaliased.
    let sourceTypeOf (expr: Expression) : Ty option =
        input.ExprTypes |> Map.tryFind expr.Id |> Option.map (substitute subst >> Unifier.unalias)

    /// The type of `rhs`, a closure over the lifted lambda `target`, when it is a local function generic in type
    /// variables of its own.
    let genericLocalType (target: string) (rhs: Expression) =
        match input.Lifted |> Map.tryFind target, input.ExprTypes |> Map.tryFind rhs.Id |> Option.map (substitute subst) with
        | Some (LiftedLambda _), Some ty when not (freeVars ty).IsEmpty -> Some ty
        | _ -> None

    let isFunction (name: string) =
        match definitions.[name].Kind with
        | Lambda _ -> true
        | _ -> false

    let parameterNames (name: string) =
        match definitions |> Map.tryFind name |> Option.map _.Kind with
        | Some (Lambda(parameters, _, _)) -> parameters |> List.map fst
        | _ -> []

    // Which function each call calls, at the function's type arguments as they are where it is asked.
    let callees = CoreIrLowerCallee.Callees(input, definitions, request)
    let membersOf = callees.MembersOf
    let contextVariables = callees.ContextVariables
    let variableMembers = callees.VariableMembers
    let bindingsAt (name: string) (instanceTy: Ty) = callees.BindingsAt subst name instanceTy
    let withAssociatedArguments (call: NodeId option) (name: string) (bindings: Map<int, Ty>) = callees.WithAssociatedArguments subst call name bindings
    let calleeKnowing (call: NodeId option) (name: string) (instanceTy: Ty) (knownMembers: (ContextMemberKey * KnownMember) list) =
        callees.Knowing subst call name instanceTy knownMembers

    let callee (name: string) (instanceTy: Ty) = calleeKnowing None name instanceTy []

    /// A scope with nothing in it, as at the top of the start function.
    let emptyScope =
        { Vars = Map.empty
          Probed = []
          MatchDepth = 0
          LivePayloads = Set.empty
          InterpolationDepth = 0
          Members = Map.empty
          CapturedMembers = Map.empty
          ForDepth = 0
          LocalInstances = Map.empty
          CapturedLocals = Map.empty }

    /// The place in the source of the expression being lowered, where a failure to lower is reported.
    let mutable current: SourceSpan option = None

    /// The name of the function being lowered, in WebAssembly text.
    let mutable selfName = ""
    // Where the definition `name` is written: its `def`'s line for a top-level def, its lambda's for a lifted one.
    let definitionPlace (name: string) =
        Place.ofSpan (input.DefinitionSpans |> Map.tryFind name |> Option.defaultValue definitions.[name].Span)

    /// The members the function being lowered takes after its other parameters, when it is a lifted lambda, which a
    /// `rec` local function passes on when it calls itself.
    let mutable selfMembers: (ContextMemberKey * IrType) list = []

    let bindTemp (value: Expr) (k: Atom -> Expr) : Expr =
        let temp = newTemp value.Type
        let body = k (AVar temp)
        Expr.make (Let(temp, value, body)) body.Type

    /// `value` bound to a local named `name`, made unique, passed to `k`.
    let bindLocal (name: string) (value: Expr) (k: Atom -> Expr) : Expr =
        let local = newVar (uniqueName name) Local value.Type
        let body = k (AVar local)
        Expr.make (Let(local, value, body)) body.Type

    /// `value` as an atom that can wait while code that stores runs before it is used: an atom that is not a temporary
    /// as it is, and anything else in a local.
    let settle (value: Expr) (k: Atom -> Expr) : Expr =
        match value.Kind with
        | Atom (AVar var) when var.Kind = Temp -> bindLocal "__held" value k
        | Atom atom -> k atom
        | _ -> bindLocal "__held" value k

    /// Where the expression being lowered is written, for reporting what cannot be lowered.
    member _.CurrentSpan = current

    /// Lowers `expr` to a value that is not a `Let` (its own lets go around it), and passes that to `k`.
    member this.Complex (scope: Scope) (expr: Expression) (k: Expr -> Expr) : Expr =
        // What fails once `expr` is lowered, in the expression around it, is reported there.
        let around = current
        // What is made from `expr` is at its place (see `Places`), and what is made after it at the place around it.
        let aroundPlace = Places.Current
        if not (SourceSpan.isNone expr.Span) then
            current <- Some expr.Span
            Places.Current <- Place.ofSpan expr.Span
        let k (value: Expr) =
            current <- around
            Places.Current <- aroundPlace
            k value
        match expr.Kind with
        | UnitExpr -> k (Expr.make (Atom AUnit) IrUnit)
        // A `Type` value is the number of the type it stands for (see `CoreIrTypeIds`).
        | TypeValue _ ->
            match input.ExprTypes |> Map.tryFind expr.Id |> Option.map (substitute subst) with
            | Some (TypeValues.TypeValueType standsFor) when (freeVars standsFor).IsEmpty && not (holdsBoxed standsFor) ->
                k (Expr.make (Atom(AInt(input.TypeIds.Of(standsFor, expr.Span.File)))) IrInt)
            | _ -> cannot "a `type` of a type that is only known when the program runs"
        | LiteralExpr(IntLit value) -> k (Expr.make (Atom(intAtom value (tyOf expr))) (tyOf expr))
        | LiteralExpr(BoolLit value) -> k (Expr.make (Atom(ABool value)) IrBool)
        // A string literal where a `char` is expected is one, its code point (see `CharLiterals`).
        | LiteralExpr(StringLit value) when tyOf expr = IrSized SizedInteger.U32 ->
            match Chars.scalarOf value with
            | Some scalar -> k (Expr.make (Atom(ASized(SizedInteger.U32, int64 scalar))) (tyOf expr))
            | None -> internalError "a char literal that is not one character, which the checker reports"
        | LiteralExpr(StringLit value) -> k (Expr.make (Atom(AString value)) IrString)
        | LiteralExpr(FloatLit value) -> k (Expr.make (FloatConstant value) (tyOf expr))
        | IdentifierExpr name -> this.Identifier scope expr name k
        | TagExpr(name, None) -> k (Expr.make (Atom(ATag name)) (tyOf expr))
        | TagExpr(name, Some payload) ->
            let ty = tyOf expr
            this.Atoms scope [ payload ] (fun atoms -> k (Expr.make (MakeTag(name, atoms.Head)) ty))
        | BinaryOp(op, _, left, right) -> this.BinaryOp scope expr op left right k
        | FunctionCall(name, _, [ operand ]) when name = NotSyntax.builtIn && not (scope.Vars.ContainsKey name) && not (definitions.ContainsKey name) ->
            this.Atoms scope [ operand ] (fun atoms -> k (Expr.make (Prim(Not, atoms)) IrBool))
        | FunctionCall(name, _, [ operand ]) when name = NegateSyntax.builtIn ->
            // `-x` on a built-in number (on any other type it calls a `negate`). An integer is `0 - x`, and a float
            // `-0.0 - x`, which is IEEE 754's negation: `-0.0 - 0.0` is `-0.0`, and `-0.0 - -0.0` is `0.0`.
            let ty = tyOf expr
            this.Atoms scope [ operand ] (fun atoms ->
                match ty with
                | IrFloat | IrF32 -> bindTemp (Expr.make (FloatConstant -0.0) ty) (fun zero -> k (Expr.make (Prim(FloatBinary "-", zero :: atoms)) ty))
                | IrInt -> k (Expr.make (Prim(Binary "-", AInt 0L :: atoms)) ty)
                | IrSized integer -> k (Expr.make (Prim(Binary "-", ASized(integer, 0L) :: atoms)) ty)
                | other -> cannot $"the operator `-` on a value of type '{IrType.show other}'")
        | ForSyntax.Call(iterables, _, parameters, body) when parameters.Length = iterables.Length ->
            let depth = scope.ForDepth
            let lowerBody vars = this.Tail { scope with Vars = vars; ForDepth = depth + 1 } body
            if CoreIrLowerLoops.pullsSequence tyOf iterables then
                let storeAsList = CoreIrLowerLoops.storeAsList sourceTypeOf callee toIrType
                let local name ty = newVar (uniqueName name) Local ty
                let members wanted k = CoreIrLowerMembers.memberAtoms this.MemberTools scope None wanted k
                // The iterables are held in locals, which the loop's own locals are set from.
                CoreIrLowerLoops.forEachPulling local tyOf storeAsList (this.AtomsInLocals scope) members lowerBody scope.Vars depth iterables parameters k
            else CoreIrLowerLoops.forEach newVar tyOf (CoreIrLowerLoops.walksStore sourceTypeOf) (this.Atoms scope) lowerBody scope.Vars depth iterables parameters k
        | WhileSyntax.Call(condition, body) ->
            let inner = { scope with ForDepth = scope.ForDepth + 1 }
            k (CoreIrLowerLoops.whileLoop scope.ForDepth (this.Tail inner condition) (this.Tail inner body))
        // A cell (see `CellSyntax`) no lambda captures is the local its def binds, and one a lambda captures is a box of
        // one field. A read is a copy of what the cell holds then, so that a later `:=` leaves the copy as it is.
        // A cell holds values of its own type, which can be a union wider than its first value's (`mut found: #some(int) |
        // #none = #none`), so the first value is taken as one of the cell's type: a tag is held the same way in any union.
        | MutSyntax.Call value ->
            let cellTy = tyOf expr
            this.Complex scope value (fun lowered ->
                match lowered.Kind with
                | _ when asCellType cellTy lowered.Type = lowered.Type -> k lowered
                | Atom atom -> k (Expr.make (Atom atom) cellTy)
                | _ -> bindTemp lowered (fun atom -> k (Expr.make (Atom atom) cellTy)))
        | FunctionCall(form, _, [ Kind (IdentifierExpr name) ]) when (form = CellSyntax.read || form = CellSyntax.unbox) && scope.Vars.ContainsKey name ->
            let cell = scope.Vars.[name]
            let value =
                if form = CellSyntax.read then Expr.make (Atom(AVar cell)) cell.Type
                else
                    match cell.Type with
                    | CoreIrRegions.RegionBox held -> Expr.make (Field(AVar cell, CoreIrRegions.boxValueSlot)) held
                    | other -> internalError $"a box of type '{IrType.show other}'"
            let copy = newVar (uniqueName name) Local value.Type
            let body = k (Expr.make (Atom(AVar copy)) value.Type)
            Expr.make (Let(copy, value, body)) body.Type
        | AssignSyntax.Call(Kind (IdentifierExpr name), value) when scope.Vars.ContainsKey name ->
            // A cell no lambda captures lives no longer than the block it is in, so what it is given needs no promoting.
            this.Atoms scope [ value ] (fun atoms -> k (Expr.make (Assign(scope.Vars.[name], atoms.Head)) IrUnit))
        // The box's node is the `mut`'s, so its type is the cell's.
        | FunctionCall(form, _, [ value ]) when form = CellSyntax.box ->
            // A captured cell is a box on the innermost region, as an `MRef` is (see `CoreIrRegions`).
            this.Atoms scope [ value ] (fun atoms ->
                let held = asCellType (tyOf expr) (atomType atoms.Head)
                CoreIrRegions.tracked (newLocal "__cell") held atoms.Head (fun box -> k (Expr.make (Atom box) (atomType box))))
        | FunctionCall(form, _, [ Kind (IdentifierExpr name); value ]) when form = CellSyntax.setBox && scope.Vars.ContainsKey name ->
            this.Atoms scope [ value ] (fun atoms ->
                CoreIrRegions.withBarrier (newLocal "__stored") WasmRegions.Names.regionStore (AVar scope.Vars.[name] :: atoms) (fun atoms ->
                    k (Expr.make (SetField(scope.Vars.[name], CoreIrRegions.boxValueSlot, List.last atoms)) IrUnit)))
        | CrashSyntax.Call message ->
            // One the type checker added (the last arm of a match that does not handle every case) has no type.
            let ty = if input.ExprTypes.ContainsKey expr.Id then tyOf expr else IrError
            k (Expr.make (Crash([ message ], true)) ty)
        | FunctionCall(name, nameSpan, args) when (name = DbgSyntax.builtIn || name = DbgSyntax.shown) && not (scope.Vars.ContainsKey name) && not (definitions.ContainsKey name) ->
            // Prints the value's text, which the printer of its type writes, and leaves the value; a shown value's by its type where the printer can't (see `ShownValues`).
            // The one argument, which may be a tuple, as the type checker has it.
            let value =
                match args with
                | [ single ] -> single
                | _ -> cannot "`dbg` of more than one value"
            match input.Dbg with
            | Stripped -> this.Complex scope value k
            | PrintedFrom root ->
                let valueTy = unconstrainedAsUnit (substitute subst input.ExprTypes.[expr.Id])
                let printer = printerTools.PrinterOf (if name = DbgSyntax.shown then CoreIrLowerDbg.ShowingAny valueTy else CoreIrLowerDbg.Showing valueTy)
                // Where `dbg` is written, which for `\dbg` at the end of a pipeline is not where the call starts.
                let site = CoreIrLowerDbg.site (name = DbgSyntax.shown) root (nameSpan |> Option.defaultValue expr.Span)
                this.Complex scope value (fun lowered ->
                    let held = newVar (uniqueName "__dbg_value") Local lowered.Type
                    let text = newTemp IrString
                    let rest = k (Expr.make (Atom(AVar held)) held.Type)
                    let printed = Expr.make (Let(text, Expr.make (Call(printer, [ AVar held ])) IrString, Expr.make (Dbg(site, AVar text)) IrUnit)) IrUnit
                    Expr.make (Let(held, lowered, Expr.make (Do(printed, rest)) rest.Type)) rest.Type)
        | AssertSyntax.Checked(condition, header, values) -> this.Assert scope condition header values k
        | WithSyntax.Call(record, Kind (RecordExpr updates)) ->
            // `record with f = v, ...`: a copy of the record with those fields replaced.
            let ty = tyOf expr
            let fields =
                match tyOf record with
                | IrRecord fields -> fields
                | other -> cannot $"`with` on a value of type '{IrType.show other}'"
            let slotsAndValues =
                updates
                |> List.map (function
                    | NamedField(name, value) ->
                        match fields |> List.tryFindIndex (fst >> (=) name) with
                        | Some slot -> slot, value
                        | None -> cannot $"an update of '{name}', which the record does not have"
                    | PositionalField _ -> cannot "a positional field in a record update")
            this.Atoms scope (record :: List.map snd slotsAndValues) (fun atoms ->
                let updated = List.zip (List.map fst slotsAndValues) atoms.Tail
                k (Expr.make (UpdateRecord(atoms.Head, updated)) ty))
        | SpreadSyntax.Call parts ->
            CoreIrLowerSpread.record (fun ty -> newVar (uniqueName "__spread") Local ty) (this.Complex scope) (tyOf expr) parts k
        // The hole function called outside a string, at a built-in type: the value as a hole would write it.
        | FunctionCall(_, _, [ value ]) when input.BuiltInHoles.Contains expr.Id ->
            this.Complex scope { expr with Kind = InterpolatedString [ StringExpr value ] } k
        | FunctionCall(name, _, args) when input.ShapeWrappers.Contains name && not (scope.Vars.ContainsKey name) ->
            // `Money(pence)`, `Pair(1, 2)`: a value of a type whose shape is not a record is its shape's value.
            match SyntaxTree.flattenCallArgs args with
            | [ single ] -> this.Complex scope single k
            | several -> this.Record scope (several |> List.mapi (fun index value -> string index, value)) (tyOf expr) k
        | FunctionCall(name, _, args) when input.RecordConstructors.Contains name && not (scope.Vars.ContainsKey name) ->
            match SyntaxTree.flattenCallArgs args with
            | [ Kind (RecordExpr fields) ] -> this.Record scope (recordExprFields fields |> sortFields) (tyOf expr) k
            | _ -> cannot $"the type constructor '{name}' without a record of its fields"
        | FunctionCall(name, _, args) when scope.Members.ContainsKey (NamedMember name) && not (namesOwnDefinition input expr) ->
            // A context member in scope: a function value like any other.
            let ty = tyOf expr
            this.CallValue scope expr.Id scope.Members.[NamedMember name] args ty k
        | FunctionCall(name, _, [ value ]) when Intrinsics.reinterpretations.Contains name && not (scope.Vars.ContainsKey name) ->
            // A map and the list of its entries are held the same way: the value is its argument.
            this.Complex scope value k
        // A `box` (see `Boxes`) is made on the region of the innermost `memory` block running (see `CoreIrRegions`), and
        // read and changed as a captured cell is, a store into it promoting what it is given.
        | FunctionCall(name, _, [ value ]) when name = Intrinsics.boxNew && not (scope.Vars.ContainsKey name) ->
            this.Atoms scope [ value ] (fun atoms ->
                CoreIrRegions.tracked (newLocal "__box") (atomType atoms.Head) atoms.Head (fun box -> k (Expr.make (Atom box) (atomType box))))
        | FunctionCall(name, _, [ box ]) when name = Intrinsics.boxGet && not (scope.Vars.ContainsKey name) ->
            this.Atoms scope [ box ] (fun atoms ->
                let held = tyOf expr
                let copy = newVar (uniqueName "__box_value") Local held
                let body = k (Expr.make (Atom(AVar copy)) held)
                Expr.make (Let(copy, Expr.make (Field(atoms.Head, CoreIrRegions.boxValueSlot)) held, body)) body.Type)
        | FunctionCall(name, _, args) when name = Intrinsics.boxSet && not (scope.Vars.ContainsKey name) ->
            this.Atoms scope (SyntaxTree.flattenCallArgs args) (fun atoms ->
                match atoms with
                | [ AVar _; _ ] ->
                    CoreIrRegions.withBarrier (newLocal "__stored") WasmRegions.Names.regionStore atoms (fun atoms ->
                        match atoms with
                        | [ AVar box; value ] -> k (Expr.make (SetField(box, CoreIrRegions.boxValueSlot, value)) IrUnit)
                        | _ -> internalError "a box set with something other than a box and a value")
                | _ -> internalError "a box set with something other than a box and a value")
        // The array of a collection literal's items, built in one block (see `CoreIrLowerArrayLiterals`).
        | FunctionCall(name, _, [ items ]) when name = CollectionSyntax.arrayOf && not (scope.Vars.ContainsKey name) ->
            let isList (e: Expression) = match tyOf e with IrList _ -> true | _ -> false
            CoreIrLowerArrayLiterals.arrayOf
                (this.AtomsInLocals scope)
                (CoreIrLowerMembers.coerceAll this.MemberTools scope)
                (fun name ty -> newVar (uniqueName name) Local ty)
                (tyOf expr)
                (CoreIrLowerArrayLiterals.parts isList items)
                k
        | FunctionCall(name, _, args) when Intrinsics.isIntrinsic name && not (scope.Vars.ContainsKey name) ->
            // A compiler intrinsic (only the standard library can call these, and a built-in property such as
            // `s.length` is one): the runtime helper of the same name. A mutable array is a box on a region, and is
            // lowered by `CoreIrRegions.mutableArray`. The cells of a list an `MList` changes aren't on a region, so the
            // element `__nanyx_list_set_head` gives one goes onto the outermost one. `__nanyx_list_set_tail` needs no
            // barrier: `MList.add` stores the new cell into its reference to the last cell too, which promotes it.
            let ty = tyOf expr
            let stored = newLocal "__stored"
            match CoreIrLowerDivision.negativePower name (SyntaxTree.flattenCallArgs args |> List.map tyOf) ty with
            | Some stop -> k stop
            | None ->
            this.Atoms scope (SyntaxTree.flattenCallArgs args) (fun atoms ->
                let call atoms = k (Expr.make (CallRuntime(name, atoms, not (Intrinsics.returnsUnit name))) ty)
                if CoreIrRegions.isMutableArrayIntrinsic name then CoreIrRegions.mutableArray stored name atoms ty k
                elif name = Intrinsics.listSetHead then CoreIrRegions.withBarrier stored WasmRegions.Names.regionStoreOutermost atoms call
                elif CoreIrLowerDivision.isSameType name atoms ty then k (Expr.make (Atom atoms.Head) ty)
                // The scheduler calls a task's function with nothing but itself, so it holds the members it requires.
                elif name = Intrinsics.taskStart then CoreIrLowerMembers.coerce this.MemberTools scope atoms.Head (IrFunction(IrUnit, IrUnit, [], true)) (fun task -> call [ task ])
                // The scheduler calls the host's wait with its timeout and nothing else, as it calls a task's function.
                elif name = Intrinsics.hostWait then
                    CoreIrLowerMembers.coerce this.MemberTools scope atoms.Head (IrFunction(IrSized SizedInteger.I32, IrUnit, [], false)) (fun wait -> call (wait :: atoms.Tail))
                else call atoms)
        | FunctionCall(name, _, args) when input.Externs.ContainsKey name && not (scope.Vars.ContainsKey name) ->
            // A foreign function: a direct call to the module's import of it. It takes no context members: it requires
            // only `HostRuntime`, which marks who may call it.
            let ty = tyOf expr
            this.Atoms scope (SyntaxTree.flattenCallArgs args) (fun atoms -> k (Expr.make (CallImport(input.Externs.[name], atoms)) ty))
        | ClosureSyntax.Closure(target, captured) ->
            // A function value: the closure block of a lifted function over what it captures.
            match input.Lifted |> Map.tryFind target with
            | Some (LiftedLambda _) -> this.Closure scope expr target captured Map.empty [] k
            | Some (FunctionWrapper wrapped) -> this.WrapperClosure scope expr target wrapped k
            | None -> cannot $"a closure over '{target}', which lambda lifting did not make"
        | FunctionCall(name, _, args) when scope.Vars.ContainsKey name ->
            // A call through a function value in a parameter or local.
            let ty = tyOf expr
            match CoreIrLowerLocals.localAt this.LocalTools scope scope.Vars.[name] (input.CalleeTypes |> Map.tryFind expr.Id) with
            | { Type = IrFunction _ } as var -> this.CallValue scope expr.Id var args ty k
            | var -> cannot $"a call to '{name}', a value of type '{IrType.show var.Type}'"
        | FunctionCall(name, _, args) when definitions.ContainsKey name && not (isFunction name) && not (SyntaxTree.flattenCallArgs args).IsEmpty ->
            // A call through a function value a top-level value holds. The value is evaluated once, into a local, which
            // the call reads twice: as the environment, and for the table slot.
            let ty = tyOf expr
            let instanceTy =
                match input.CalleeTypes |> Map.tryFind expr.Id with
                | Some ty -> ty
                | None -> cannot $"a call to '{name}' whose type is not known"
            let value = Expr.make (Call(callee name instanceTy, [])) (toIrType instanceTy)
            let closure = newVar (uniqueName name) Local value.Type
            let body = this.CallValue scope expr.Id closure args ty k
            Expr.make (Let(closure, value, body)) body.Type
        | FunctionCall(name, _, args) when input.Lifted.ContainsKey name ->
            // A `rec` local function calling itself, directly, with its environment passed on.
            if name <> self.Definition then cannot $"a call to the lifted function '{name}' from another function"
            let ty = tyOf expr
            this.Atoms scope (SyntaxTree.flattenCallArgs args) (fun atoms ->
                CoreIrLowerMembers.memberAtoms this.MemberTools scope None selfMembers (fun members -> k (Expr.make (Call(selfName, atoms @ members)) ty)))
        | FunctionCall(name, _, args) -> this.CallDefinition scope expr name args k
        | TupleExpr items -> this.Record scope (items |> List.mapi (fun index item -> string index, item)) (tyOf expr) k
        | ListExpr items ->
            // Each element that is a function value is adapted to the list's element type (see `CoreIrLowerMembers.coerce`).
            let ty = tyOf expr
            let elementTys =
                match ty with
                | IrList elementTy -> items |> List.map (fun _ -> elementTy)
                | _ -> []
            this.AtomsAt scope items elementTys (fun atoms -> CoreIrLowerMembers.coerceAll this.MemberTools scope atoms elementTys (fun atoms -> k (Expr.make (MakeList atoms) ty)))
        | RecordExpr fields -> this.Record scope (recordExprFields fields |> sortFields) (tyOf expr) k
        | MemberAccess(Kind (IdentifierExpr qualifier), fieldName, _) when
            not (scope.Vars.ContainsKey qualifier) && definitions.ContainsKey $"{qualifier}.{fieldName}" ->
            // A value of another module, named through it.
            this.Complex scope { expr with Kind = IdentifierExpr $"{qualifier}.{fieldName}" } k
        // A record description's `build` (see `CoreIrLowerBuild`).
        | MemberAccess(record, "build", _) when
            (match input.ExprTypes |> Map.tryFind record.Id with Some (TyApply(name, _)) -> name = TypeValues.recordInfo | _ -> false) ->
            CoreIrLowerBuild.building this.TypeMatchTools scope record (tyOf expr) k
        | MemberAccess(record, fieldName, _) ->
            // The field's type is the record's for it: a lifted function reads what it captures from its environment
            // with reads the type checker never saw.
            let recordTy =
                match record.Kind with
                | IdentifierExpr name when scope.Vars.ContainsKey name -> scope.Vars.[name].Type
                | _ -> tyOf record
            let slot, ty =
                match IrType.unfold recordTy with
                | IrRecord fields ->
                    match fields |> List.tryFindIndex (fst >> (=) fieldName) with
                    | Some slot -> slot, snd fields.[slot]
                    | None -> cannot $"the field '{fieldName}', which the record does not have"
                | other -> cannot $"the field '{fieldName}' of a value of type '{IrType.show other}'"
            this.Atoms scope [ record ] (fun atoms -> k (Expr.make (Field(atoms.Head, slot)) ty))
        | IfExpr(cond, ifTrue, ifFalse) ->
            let ty = tyOf expr
            this.Atoms scope [ cond ] (fun atoms ->
                k (Expr.make (If(atoms.Head, this.Branch scope ifTrue ty, this.Branch scope ifFalse ty)) ty))
        | Block statements -> this.Block scope (statements |> List.map _.Kind) k
        | ExpressionKind.Match(values, arms) -> this.Match scope expr values arms k
        | ContextMemberCall(contextType, memberName, args) ->
            // `C(T).f(args)`: the member of that instance of the context, which must be in scope.
            let key = contextMemberKey (Some contextType) memberName
            let ty = tyOf expr
            match scope.Members |> Map.tryFind key with
            | Some memberVar -> this.CallValue scope expr.Id memberVar args ty k
            | None -> cannot $"the context member '{ContextMemberKey.text key}', which is not in scope"
        | ContextMemberAccess(contextType, memberName) ->
            // `C(T).m`: the member of that instance of the context as a value.
            let key = contextMemberKey (Some contextType) memberName
            match scope.Members |> Map.tryFind key with
            | Some memberVar -> this.MemberValue expr key memberVar k
            | None -> cannot $"the context member '{ContextMemberKey.text key}', which is not in scope"
        // `x \f(a, b)` is the call `f(x, a, b)`, typed at the pipe's node.
        | Pipe(value, name, range, args) -> this.Complex scope { expr with Kind = FunctionCall(name, range, pipeCallArguments value args) } k
        | InterpolatedString parts -> CoreIrLowerInterpolation.lowerInterpolation this.InterpolationTools scope expr parts k
        | Lambda _ -> cannot "a lambda"
        | _ -> cannot "this kind of expression"

    /// The context member of the key `key`, `memberVar` in scope, named as a value at `expr`. A function member requires
    /// its context where it is called, so it is a closure that calls the member it is then passed, and holds nothing of
    /// this instance (see `CoreIrLowerLifted.memberCalling`).
    member private this.MemberValue (expr: Expression) (key: ContextMemberKey) (memberVar: Var) (k: Expr -> Expr) : Expr =
        match input.ExprTypes |> Map.tryFind expr.Id |> Option.map (substitute subst >> toIrType) with
        | Some (IrFunction(_, _, wanted, _) as expected) when wanted |> List.exists (fst >> (=) key) ->
            let functionName =
                this.MemberTools.Request (SpecKey.plain $"__member_{sanitizeIdentifier (ContextMemberKey.text key)}_{IrType.mangle expected}" []) (this.MemberTools.Subst ()) (MemberCalling(key, expected))
            let arity = 1 + (adaptedValueTypes memberVar.Type expected).Length + wanted.Length
            k (Expr.make (MakeClosure(functionName, arity, [])) expected)
        | _ -> k (Expr.make (Atom(AVar memberVar)) memberVar.Type)

    /// The name `name`, at `expr`: a local, a context member in scope, or a top-level value.
    member private this.Identifier (scope: Scope) (expr: Expression) (name: string) (k: Expr -> Expr) : Expr =
        match scope.Vars |> Map.tryFind name with
        | Some var ->
            let var = CoreIrLowerLocals.localAt this.LocalTools scope var (input.ExprTypes |> Map.tryFind expr.Id)
            // A value of a type only known when the program runs, used in an arm of a `Type` match that says what its
            // type is, is unboxed there (see `CoreIrLowerTypeMatch`).
            // A map's or a set's store is a recursive type, which a box in it is converted in by the type it is of.
            let narrowed = input.ExprTypes |> Map.tryFind expr.Id |> Option.map (substitute subst)
            match narrowed |> Option.bind (fun ty -> CoreIrLowerBuild.decanonicalStore this.TypeMatchTools ty (AVar var) (fun atom -> k (Expr.make (Atom atom) (atomType atom)))) with
            | Some converted -> converted
            | None ->
            match narrowed |> Option.map toIrType with
            | Some expected when CoreIrLowerBoxes.holdsBox var.Type && CoreIrLowerBoxes.needsConversion var.Type expected ->
                CoreIrLowerMembers.coerce this.MemberTools scope (AVar var) expected (fun atom -> k (Expr.make (Atom atom) expected))
            | _ -> k (Expr.make (Atom(AVar var)) var.Type)
        // A context member in scope, named as a value (`` `0` ``, or a function member passed on).
        | None when scope.Members.ContainsKey (NamedMember name) && not (namesOwnDefinition input expr) ->
            this.MemberValue expr (NamedMember name) scope.Members.[NamedMember name] k
        | None when definitions.ContainsKey name && not (isFunction name) ->
            // A top-level value is compiled to a function of no parameters: evaluate it. One of a type generic in
            // contexts named where one that carries more is expected is made at its own type, and adapted (see
            // `CoreIrLowerMembers.coerce`).
            let ty = tyOf expr
            let own =
                input.DefinitionTypes
                |> Map.tryFind name
                |> Option.filter (freeVars >> List.isEmpty)
                |> Option.map toIrType
            match own with
            | Some (IrRecord _ as own) when own <> ty ->
                bindTemp (Expr.make (Call(callee name input.ExprTypes.[expr.Id], [])) own) (fun atom ->
                    CoreIrLowerMembers.coerce this.MemberTools scope atom ty (fun atom -> k (Expr.make (Atom atom) ty)))
            | _ -> k (Expr.make (Call(callee name input.ExprTypes.[expr.Id], [])) ty)
        | None when definitions.ContainsKey name -> cannot $"the function '{name}' used as a value"
        | None -> cannot $"a reference to '{name}'"

    /// The binary operator `op` on `left` and `right`, at `expr`.
    member private this.BinaryOp (scope: Scope) (expr: Expression) (op: string) (left: Expression) (right: Expression) (k: Expr -> Expr) : Expr =
        match op, left, right with
        | "++", Kind (ListExpr items), rest when not items.IsEmpty ->
            // `[a, b] ++ rest`, which is what `[a, b, ...rest]` is: a cell for each element, put in front of `rest`
            // rather than copied from a list of their own. The elements are evaluated first, then `rest`, as `++`
            // evaluates them.
            let ty = tyOf expr
            this.Atoms scope (items @ [ rest ]) (fun atoms ->
                let tail = List.last atoms
                let cons head tail = Expr.make (CallRuntime(Helpers.listCons, [ head; tail ], true)) ty
                let rec build (heads: Atom list) (k: Expr -> Expr) =
                    match heads with
                    | [ head ] -> k (cons head tail)
                    | head :: others -> build others (fun inner -> bindTemp inner (fun cell -> k (cons head cell)))
                    | [] -> cannot "an empty list in front of ++"
                build (List.take items.Length atoms) k)
        | "++", _, _ ->
            // Joining strings or lists: a runtime helper for each.
            let helper =
                match tyOf left with
                | IrString -> Helpers.stringConcat
                | IrList _ -> Helpers.listConcat
                | other -> cannot $"the operator `++` on a value of type '{IrType.show other}'"
            let ty = tyOf expr
            this.Atoms scope [ left; right ] (fun atoms -> k (Expr.make (CallRuntime(helper, atoms, true)) ty))
        | ("==" | "!="), _, _ when (match tyOf left with
                                    | IrString | IrTags _ | IrRecord _ | IrList _ | IrRecursive _ -> true
                                    | _ -> false) ->
            // A string, a record, a list and a tag with a payload are pointers: equality compares what they point to,
            // by the operands' structure (see `CoreIrEquality`).
            let helper =
                match tyOf left with
                | IrString -> Helpers.stringEquals
                | _ -> Helpers.valueEquals
            this.Atoms scope [ left; right ] (fun atoms ->
                let equal = Expr.make (CallRuntime(helper, atoms, true)) IrBool
                if op = "==" then k equal
                else bindTemp equal (fun result -> k (Expr.make (Prim(Not, [ result ])) IrBool)))
        | ("<" | "<=" | ">" | ">="), _, _ when (match tyOf left with
                                                | IrString | IrList _ -> true
                                                | IrRecord fields -> IrType.isTuple fields
                                                | _ -> false) ->
            // A string, a tuple and a list are ordered by their structure (see `CoreIrOrdering`): their order, a
            // number, is compared with 0. Records, tags and functions have no order: a local function that orders them
            // is not supported. (A record inside a list or tuple is ordered field by field, only where a generic
            // function orders a `list(a)`, see `CoreIrOrdering`.)
            if IrType.holdsUnordered (tyOf left) then cannot $"the operator `{op}` on a value of type '{IrType.show (tyOf left)}'"
            this.Atoms scope [ left; right ] (fun atoms ->
                let order = Expr.make (CallRuntime(Helpers.valueCompare, atoms, true)) IrInt
                bindTemp order (fun result -> k (Expr.make (Prim(Binary op, [ result; AInt 0L ])) IrBool)))
        | ("and" | "or"), _, _ ->
            // `a and b` evaluates `b` only when `a` is true, and `a or b` only when `a` is false: an `if`. A `b` of a
            // name or a literal, or an operator on those, that is one instruction that cannot trap or call anything, is
            // evaluated anyway, with `i32.and` or `i32.or`, which costs less than a branch. Lowering it to see is done
            // only for those, which lower to nothing but what they give.
            let ty = tyOf expr
            let isPlain (operand: Expression) =
                match operand.Kind with
                | LiteralExpr _
                | IdentifierExpr _ -> true
                | _ -> false
            let isSmall =
                match right.Kind with
                | BinaryOp(_, _, rightLeft, rightRight) -> isPlain rightLeft && isPlain rightRight
                | _ -> isPlain right
            if isSmall && Expr.isSafeInstruction (this.Branch scope right ty) then
                this.Atoms scope [ left; right ] (fun atoms -> k (Expr.make (Prim(Binary op, atoms)) ty))
            else
                this.Atoms scope [ left ] (fun atoms ->
                    let decided = Expr.make (Atom(ABool(op = "or"))) ty
                    let right = this.Branch scope right ty
                    let ifTrue, ifFalse = if op = "and" then right, decided else decided, right
                    k (Expr.make (If(atoms.Head, ifTrue, ifFalse)) ty))
        | _ when IrType.isFloat (tyOf left) || IrType.isFloat (tyOf right) ->
            // An instruction on the floats' values.
            // `//` and `%` have no instruction: each calls a runtime helper (see `CoreIrEmit`).
            if op <> "//" && op <> "%" && (wasmFloatInstruction op).IsNone then cannot $"the operator `{op}` on floats"
            let ty = tyOf expr
            this.Atoms scope [ left; right ] (fun atoms -> k (Expr.make (Prim(FloatBinary op, atoms)) ty))
        | "/", _, _ -> this.Atoms scope [ left; right ] (fun atoms -> CoreIrLowerDivision.fractional bindTemp atoms (tyOf expr) k)
        | _ ->
            if op <> "//" && (wasmBinaryInstruction op).IsNone then cannot $"the operator `{op}`"
            for operand in [ left; right ] do
                match tyOf operand with
                | IrInt | IrSized _ | IrBool | IrUnit | IrError -> ()
                | other -> cannot $"the operator `{op}` on a value of type '{IrType.show other}'"
            let ty = tyOf expr
            this.Atoms scope [ left; right ] (fun atoms -> k (Expr.make (Prim(Binary op, atoms)) ty))

    /// The top-level function `target` wraps, `wrapped`, passed as a value at `expr`, through a wrapper for each type it
    /// is passed at. Each call passes the members of the contexts the value's type requires (see `IrFunction`); the
    /// closure captures those of the others the function requires, as they are where it is named (a parameterised
    /// context).
    member private this.WrapperClosure (scope: Scope) (expr: Expression) (target: string) (wrapped: string) (k: Expr -> Expr) : Expr =
        let ty = tyOf expr
        let arity = IrType.closureArity (parameterNames target) ty
        let instanceTy = substitute subst input.ExprTypes.[expr.Id]
        let passed =
            match ty with
            | IrFunction(_, _, members, _) -> members |> List.map fst |> Set.ofList
            | _ -> Set.empty
        let specialised =
            input.DefinitionTypes |> Map.tryFind wrapped |> Option.exists (freeVars >> List.isEmpty >> not)
            || not (contextVariables wrapped).IsEmpty
        let key = SpecKey.plain target (if specialised then [ toIrType instanceTy ] else [])
        CoreIrLowerMembers.contextMembers this.MemberTools scope wrapped expr.Id instanceTy passed [] (fun captured ->
            let name = request key subst (WrapperCalling(wrapped, instanceTy, captured |> List.map (fun (memberKey, atom) -> memberKey, atomType atom)))
            k (closureOver name arity (List.map snd captured) ty))

    /// A call, at `expr`, to the top-level function `name`, with the context members it requires after its arguments.
    member private this.CallDefinition (scope: Scope) (expr: Expression) (name: string) (args: Expression list) (k: Expr -> Expr) : Expr =
        if not (definitions.ContainsKey name) then cannot $"a call to '{name}'"
        if not (isFunction name) then cannot $"a call to the value '{name}'"
        let instanceTy =
            match input.CalleeTypes |> Map.tryFind expr.Id with
            | Some ty -> ty
            | None ->
                // A call the type checker did not record as one (a built-in property, such as `xs.length`, is
                // written as a call to the function that reads it): its type is its arguments' and its own.
                let argumentTys =
                    SyntaxTree.flattenCallArgs args
                    |> List.map (fun arg ->
                        match input.ExprTypes |> Map.tryFind arg.Id with
                        | Some ty -> ty
                        | None -> cannot $"a call to '{name}' whose type is not known")
                let input' =
                    match argumentTys with
                    | [ single ] -> single
                    | several -> TyRecord(several |> List.mapi (fun index ty -> string index, ty) |> Map.ofList, FieldOrder.none)
                match input.ExprTypes |> Map.tryFind expr.Id with
                | Some output -> TyFunc(input', output, [])
                | None -> cannot $"a call to '{name}' whose type is not known"
        let knownMembers = CoreIrLowerMembers.knownMembers this.MemberTools scope name expr.Id instanceTy
        let target = calleeKnowing (Some expr.Id) name instanceTy knownMembers
        let ty = tyOf expr
        let parameters = parameterNames name
        // A function value given for a parameter whose type requires other contexts is adapted to it (see `CoreIrLowerMembers.coerce`).
        let parameterTys =
            try parameterTypes parameters (substitute subst instanceTy) true |> fst |> List.map toIrType
            with CannotLower _ -> []
        let crashing = CoreIrProbes.beforeCrash (input.Probes && CoreIrProbes.endsTheProgram input.DefinitionTypes name) (fun () -> probeLocals (probedInScope scope)) (Place.ofSpan expr.Span)
        // The context members the callee requires follow its arguments.
        let call atoms =
            CoreIrLowerMembers.coerceAll this.MemberTools scope atoms (if parameterTys.Length = atoms.Length then parameterTys else []) (fun atoms ->
                CoreIrLowerMembers.contextMembers this.MemberTools scope name expr.Id instanceTy Set.empty knownMembers (fun members ->
                    k (crashing (Expr.make (Call(target, atoms @ List.map snd members)) ty))))
        // Arguments one of which is adapted to its parameter's type by storing are held in locals, so that no other
        // waits on the stack across the stores (#1949).
        let atomsOf (arguments: Expression list) =
            if arguments.Length = parameterTys.Length
               && List.exists2 (fun argument parameterTy -> CoreIrLowerMembers.coercionStores (tyOf argument) parameterTy) arguments parameterTys then
                this.AtomsInLocals scope arguments
            else this.Atoms scope arguments
        // The arguments as the callee takes them.
        match SyntaxTree.flattenCallArgs args with
        | [ Kind (RecordExpr fields) ] when parameters.Length > 1 && fields.Length = parameters.Length ->
            // `f(a, step = 2)`: each parameter takes the field of its name, or else the positional field in its
            // place.
            let positional = fields |> List.choose (function PositionalField value -> Some value | NamedField _ -> None)
            let named = fields |> List.choose (function NamedField(n, value) -> Some (n, value) | PositionalField _ -> None) |> Map.ofList
            let ordered =
                parameters
                |> List.mapi (fun index parameter ->
                    match named |> Map.tryFind parameter, positional |> List.tryItem index with
                    | Some value, _
                    | None, Some value -> value
                    | None, None -> cannot $"a call to '{name}' with no argument for {parameter}")
            atomsOf ordered call
        | [ record ] when parameters.Length > 1 && (match this.TypeIn scope record with IrRecord _ -> true | _ -> false) ->
            // A record that is not a name in scope is held in a local of its own, as each field is read from it.
            let fieldsOf held = CoreIrLowerArguments.fieldsAsArguments newTemp name parameters (this.TypeIn scope record) held call
            match record.Kind with
            | IdentifierExpr held when scope.Vars.ContainsKey held -> fieldsOf (AVar scope.Vars.[held])
            | _ ->
                this.Complex scope record (fun value ->
                    let local = newVar (uniqueName "__arguments") Local value.Type
                    let body = fieldsOf (AVar local)
                    Expr.make (Let(local, value, body)) body.Type)
        | several when parameters.Length = 1 && several.Length > 1 ->
            // `f(a, b)` and `f((a, b))` parse alike; a function of one parameter takes the whole tuple.
            let tupleTy = IrRecord(several |> List.mapi (fun index value -> string index, tyOf value))
            this.Record scope (several |> List.mapi (fun index value -> string index, value)) tupleTy (fun tuple ->
                bindTemp tuple (fun atom -> call [ atom ]))
        | [] when parameters.Length = 1 ->
            // `f()` on a function written `{ _ -> ... }`: its one parameter is the unit.
            call [ AUnit ]
        | flatArgs when flatArgs.Length = parameters.Length -> atomsOf flatArgs call
        | _ -> cannot $"a call to '{name}' with its arguments in another shape"

    /// What lowering an interpolated string needs from this function's lowering (see `CoreIrLowerInterpolation`).
    member private this.InterpolationTools : CoreIrLowerInterpolation.InterpolationTools =
        { Input = input
          Subst = fun () -> subst
          ToIrType = toIrType
          TypeOf = tyOf
          TypeIn = this.TypeIn
          NewVar = newVar
          Complex = this.Complex
          Atoms = this.Atoms }

    /// What finding and adapting context members needs from this function's lowering (see `CoreIrLowerMembers`).
    member private this.MemberTools : CoreIrLowerMembers.MemberTools =
        { Input = input
          Subst = fun () -> subst
          ToIrType = toIrType
          Request = request
          NewVar = newVar
          UniqueName = uniqueName
          BindTemp = bindTemp
          Settle = settle
          KnownOf = knownOf
          VariableMembers = variableMembers
          BindingsAt = fun call name instanceTy -> bindingsAt name instanceTy |> withAssociatedArguments (Some call) name
          ParameterNames = parameterNames
          Callee = callee
          WrappedOf = wrappedOf }

    /// `body` with each local that stands for a known member and that it uses given the member's value first.
    member private this.WithKnownValues (body: Expr) : Expr =
        usedOf (known.Keys |> Set.ofSeq) body
        |> Seq.fold (fun (body: Expr) id ->
            let var, knownMember = known.[id]
            CoreIrLowerMembers.valueOfKnown this.MemberTools knownMember (fun value ->
                CoreIrLowerMembers.coerce this.MemberTools emptyScope value var.Type (fun value -> Expr.make (Let(var, Expr.make (Atom value) var.Type, body)) body.Type))) body

    /// The type of `expr` where it is: a name in scope has its variable's type, and a field of one the field's, which
    /// may be more than the type checker recorded at the node (for a parameter lambda lifting annotated, say).
    member this.TypeIn (scope: Scope) (expr: Expression) : IrType =
        match expr.Kind with
        // A boxed value an arm of a `Type` match unboxes where it is used has the type it is unboxed to (see `Identifier`).
        | IdentifierExpr name when scope.Vars.ContainsKey name && CoreIrLowerBoxes.holdsBox scope.Vars.[name].Type && input.ExprTypes.ContainsKey expr.Id ->
            tyOf expr
        | IdentifierExpr name when scope.Vars.ContainsKey name -> scope.Vars.[name].Type
        | MemberAccess(record, fieldName, _) when (match record.Kind with IdentifierExpr name -> scope.Vars.ContainsKey name | _ -> true) ->
            match this.TypeIn scope record with
            | IrRecord fields when fields |> List.exists (fst >> (=) fieldName) -> fields |> List.find (fst >> (=) fieldName) |> snd
            | _ -> tyOf expr
        | _ -> tyOf expr

    /// The closure block, at `expr`, of the lifted lambda `target` over `captured`. The closure keeps the lambda's node,
    /// so its type is the lambda's, with the parameters lambda lifting annotated where the type checker left them open.
    /// A lambda in a specialisation is specialised with it, and the lambda of a local function generic in variables of
    /// its own at the `typeArguments` it is used at, which `bindings` binds (see `GenericLocal`).
    member private this.Closure (scope: Scope) (expr: Expression) (target: string) (captured: Expression list) (bindings: Map<int, Ty>) (typeArguments: IrType list) (k: Expr -> Expr) : Expr =
        let subst = bindings |> Map.fold (fun acc id ty -> acc |> Map.add id ty) subst
        let ty =
            match input.ExprTypes |> Map.tryFind expr.Id with
            | Some lambdaTy ->
                let parameterTys, resultTy = this.LiftedParameterTypes target (substitute subst lambdaTy)
                let inputTy =
                    match parameterTys, Unifier.unalias (substitute subst lambdaTy) with
                    | [], _ -> TyPrimitive "unit"
                    | [ single ], _ -> single
                    // Several parameters of a record that is not a tuple take it whole (see `IrType.recordTakenWhole`).
                    | _, (TyFunc(input, _, _) as fnTy) when (IrType.recordTakenWhole (parameterNames target) (toIrType fnTy)).IsSome -> input
                    | several, _ -> TyRecord(several |> List.mapi (fun index ty -> string index, ty) |> Map.ofList, FieldOrder.none)
                toIrType (substitute subst (TyFunc(inputTy, resultTy, requiredContexts lambdaTy)))
            | None -> tyOf expr
        let arity = IrType.closureArity (parameterNames target) ty
        // The members the lambda captures that this function knows, which it is specialised on in turn, and not
        // captured when they come last: a lambda that captured only them is a static closure, made once. A local
        // function generic in variables of its own keeps them, since it finds its captures by its own keys (#2311).
        let knownOf (name: string) =
            match scope.Vars |> Map.tryFind name with
            | Some var -> match known.TryGetValue var.Id with | true, (_, knownMember) -> Some knownMember | _ -> None
            | None -> None
        let knownCaptured, kept = CoreIrLowerKnown.knownCaptures input target knownOf captured
        let captured = if bindings.IsEmpty && typeArguments.IsEmpty then kept else captured
        this.Atoms scope captured (fun atoms ->
            CoreIrLowerLocals.capturedInstances this.LocalTools scope target bindings captured atoms (fun atoms capturedLocals ->
                let capturedTys = atoms |> List.map atomType
                let key = { SpecKey.plain target (self.TypeArguments @ typeArguments) with Described = self.Described; Known = knownCaptured }
                let name = request key subst (LiftedCapturing(capturedTys, capturedLocals))
                k (closureOver name arity atoms ty)))

    /// What lowering a local function generic in variables of its own needs from this function's lowering (see
    /// `CoreIrLowerLocals`).
    member private this.LocalTools : CoreIrLowerLocals.LocalTools =
        { Input = input
          Definitions = definitions
          NewVar = newVar
          UniqueName = uniqueName
          Substitute = fun ty -> substitute subst ty
          ToIrType = toIrType
          Closure = this.Closure
          LowerBlock = this.Block }

    /// Allocates a record of `fields` (name and value, in the order they are stored), of type `ty`. Each value is
    /// adapted to its field's type (see `CoreIrLowerMembers.coerce`): a pure lambda given for a field of a value of a
    /// type generic in contexts that carries some takes their members (#1725).
    member this.Record (scope: Scope) (fields: (string * Expression) list) (ty: IrType) (k: Expr -> Expr) : Expr =
        let fieldTys =
            match IrType.unfold ty with
            | IrRecord stored when List.map fst stored = List.map fst fields -> List.map snd stored
            | _ -> []
        this.Atoms scope (List.map snd fields) (fun atoms ->
            CoreIrLowerMembers.coerceAll this.MemberTools scope atoms fieldTys (fun atoms ->
                k (Expr.make (MakeRecord(List.zip (List.map fst fields) atoms)) ty)))

    /// A call through the function value `closure`, whose result is of type `ty`. A function whose input is a tuple takes
    /// its elements, read from a tuple passed whole; one that takes one value takes a tuple whole (see
    /// `CoreIrLowerArguments.takesOneValue`); one whose input is unit takes nothing, so `f()` and `f(())` are the same call
    /// (see `ValueParameters`). The members of the contexts the value requires follow, as the call at `node` finds them
    /// (see `CoreIrLowerMembers.memberAtoms`). A known function a specialisation stands in for is called directly.
    member this.CallValue (scope: Scope) (node: NodeId) (closure: Var) (args: Expression list) (ty: IrType) (k: Expr -> Expr) : Expr =
        // The arguments of a context member whose type has values boxed are held in locals, since converting them stores
        // (see `CoreIrLowerBoxes`).
        let boxing = CoreIrLowerBoxes.holdsBox closure.Type
        let atomsOf = if boxing then this.AtomsInLocals else this.Atoms
        let bindElement = if boxing then bindLocal "__element" else bindTemp
        let members, parameterTys =
            match closure.Type with
            | IrFunction(input, _, members, _) ->
                members, (IrType.tupleElements input |> Option.map (List.map snd) |> Option.defaultValue [ input ])
            | _ -> [], []
        let callThen (k: Expr -> Expr) atoms =
            CoreIrLowerMembers.coerceAll this.MemberTools scope atoms (if parameterTys.Length = atoms.Length then parameterTys else []) (fun atoms ->
                match knownOf closure, closure.Type with
                | Some (KnownFunction(wrapper, wrapped, at)), IrFunction(input, _, _, _) ->
                    directArguments bindTemp (parameterNames wrapper).Tail.Length input atoms (fun atoms -> k (Expr.make (Call(callee wrapped at, atoms)) ty))
                // A lambda that captures nothing is called as a call through its closure calls it, with the closure as
                // its environment.
                | Some (KnownLambda(lifted, closure)), IrFunction(_, output, [], _)
                    when not (CoreIrLowerBoxes.needsConversion output ty || CoreIrLowerMembers.differInContexts output ty) ->
                    k (Expr.make (Call(lifted, closure :: atoms)) ty)
                | Some (KnownLambdaOver lifted), IrFunction(_, output, [], _)
                    when not (CoreIrLowerBoxes.needsConversion output ty || CoreIrLowerMembers.differInContexts output ty) ->
                    k (Expr.make (Call(lifted, AVar closure :: atoms)) ty)
                | _ ->
                    CoreIrLowerMembers.memberAtoms this.MemberTools scope (Some node) members (fun memberAtoms ->
                        // A context member's result with values boxed, or a function that requires other contexts'
                        // members than the call has it require (#1899), is adapted to the type the call has it at.
                        match closure.Type with
                        | IrFunction(_, output, _, _) when CoreIrLowerBoxes.needsConversion output ty || CoreIrLowerMembers.differInContexts output ty ->
                            bindTemp (Expr.make (CallClosure(closure, atoms @ memberAtoms, Borrowing)) output) (fun result ->
                                CoreIrLowerMembers.coerce this.MemberTools scope result ty (fun atom -> k (Expr.make (Atom atom) ty)))
                        | _ -> k (Expr.make (CallClosure(closure, atoms @ memberAtoms, Borrowing)) ty)))
        let call = callThen k
        let elements =
            match closure.Type with
            | IrFunction(input, _, _, _) -> IrType.tupleElements input
            | _ -> None
        let takesOneValue = match closure.Type with IrFunction(input, _, _, _) -> CoreIrLowerArguments.takesOneValue input | _ -> false
        match elements, (if takesOneValue then args else SyntaxTree.flattenCallArgs args) with
        | _, (_ :: _ :: _ as several) when takesOneValue ->
            atomsOf scope several (fun atoms -> CoreIrLowerArguments.asTuple bindElement atoms (fun tuple -> call [ tuple ]))
        | _, flatArgs when (match closure.Type with IrFunction(IrUnit, _, _, _) -> true | _ -> false) ->
            // What the argument does is done first, as part of the call, which may be an operand computed where it is
            // used, after others that wait (#2662); its value, the unit, isn't passed.
            let effects =
                flatArgs
                |> List.map (this.Tail scope)
                |> List.filter (fun effect -> match effect.Kind with Atom _ -> false | _ -> true)
            callThen (fun call -> k (List.foldBack (fun effect rest -> Expr.make (Do(effect, rest)) rest.Type) effects call)) []
        | Some elements, [ tuple ] ->
            // Each element is read from the tuple, so one that is not a name is held in a local first.
            this.AtomsInLocals scope [ tuple ] (fun atoms ->
                let rec loadElements (remaining: (int * IrType) list) (acc: Atom list) =
                    match remaining with
                    | [] -> call (List.rev acc)
                    | (slot, elementTy) :: rest ->
                        bindElement (Expr.make (Field(atoms.Head, slot)) elementTy) (fun atom -> loadElements rest (atom :: acc))
                loadElements elements [])
        | _, flatArgs -> atomsOf scope flatArgs call

    /// The parameters a lifted function takes for its parameters `names` of types `tys` (its environment aside), and
    /// how to bind those names around its body. A lone parameter whose type is a tuple takes the tuple's elements, which
    /// the body then has as one tuple again (see `IrType.tupleElements`). A lone parameter of type unit, as in
    /// `{ _ -> ... }`, takes nothing, as a function written without parameters does, so that calls through a function
    /// value of type `() -> a` agree however its lambda was written (see `CallValue`).
    member private this.ValueParameters (names: string list) (tys: IrType list) : Var list * (Expr -> Expr) * Var list =
        let tupleElements =
            match tys with
            | [ ty ] -> IrType.tupleElements ty
            | _ -> None
        match names, tys, tupleElements with
        | [ name ], [ IrUnit ], _ ->
            let unit = newVar (uniqueName name) Local IrUnit
            [], (fun body -> Expr.make (Let(unit, Expr.make (Atom AUnit) IrUnit, body)) body.Type), [ unit ]
        | [ name ], [ tupleTy ], Some elementTys ->
            let elements = elementTys |> List.mapi (fun index (_, ty) -> newVar (uniqueName $"{name}_{index}") Param ty)
            let tuple = newVar (uniqueName name) Local tupleTy
            // Each element in its slot of the tuple, under its position's name.
            let fieldValues =
                List.zip elementTys elements
                |> List.mapi (fun index ((slot, _), element) -> slot, (string index, AVar element))
                |> List.sortBy fst
                |> List.map snd
            let bind (body: Expr) = Expr.make (Let(tuple, Expr.make (MakeRecord fieldValues) tupleTy, body)) body.Type
            elements, bind, [ tuple ]
        | _ ->
            let parameters = List.map2 (fun name ty -> newVar (uniqueName name) Param ty) names tys
            parameters, id, parameters

    /// Lowers `expr` in tail position: the whole of what is left to do.
    member this.Tail (scope: Scope) (expr: Expression) : Expr = this.Complex scope expr id

    /// Lowers `expr`, a branch of an `if` or a `match` whose value is of type `ty`, in tail position. A branch whose
    /// value carries fewer contexts than the whole, as the pure rest of a sequence in a `match` that gives a
    /// `[Console] seq(a)`, is adapted to it (see `CoreIrLowerMembers.coerce`). So is an arm of a `Type` match that gives
    /// a value of the type it narrows `a` to, where `a` is only known when the program runs (see `CoreIrLowerTypeMatch`).
    member this.Branch (scope: Scope) (expr: Expression) (ty: IrType) : Expr =
        let value = this.Tail scope expr
        if CoreIrLowerMembers.differInContexts value.Type ty || CoreIrLowerBoxes.needsConversion value.Type ty then
            bindTemp value (fun atom -> CoreIrLowerMembers.coerce this.MemberTools scope atom ty (fun atom -> Expr.make (Atom atom) ty))
        else value

    /// Lowers `assert(condition)`, written out with its message's `header` and the `values` it shows, each with its
    /// label (see `AssertSyntax.Checked`). Each value is evaluated once, into a local, and the condition reads that local
    /// in its place; then, if the condition is false, the program crashes with the header and a line for each value,
    /// printed as `dbg` prints it. It checks in a release build too.
    member this.Assert (scope: Scope) (condition: Expression) (header: string) (values: (string * Expression) list) (k: Expr -> Expr) : Expr =
        let rec bind (scope: Scope) (remaining: (string * Expression) list) (bound: (string * NodeId * Var) list) =
            match remaining with
            | (label, value) :: rest ->
                this.Complex scope value (fun lowered ->
                    let name = uniqueName "__assert_value"
                    let held = newVar name Local lowered.Type
                    let body = bind { scope with Vars = scope.Vars.Add(name, held) } rest ((label, value.Id, held) :: bound)
                    Expr.make (Let(held, lowered, body)) body.Type)
            | [] -> check scope (List.rev bound)
        and check (scope: Scope) (bound: (string * NodeId * Var) list) =
            let held = bound |> List.map (fun (_, node, var) -> node, var.Name) |> Map.ofList
            let rec reading (expr: Expression) =
                match held |> Map.tryFind expr.Id with
                | Some name -> { expr with Kind = IdentifierExpr name }
                | None -> SyntaxTree.mapChildren reading expr
            this.Atoms scope [ reading condition ] (fun atoms ->
                let lines =
                    bound
                    |> List.map (fun (label, node, var) ->
                        let ty = unconstrainedAsUnit (substitute subst input.ExprTypes.[node])
                        label, var, printerTools.PrinterOf (CoreIrLowerDbg.Showing ty))
                let texts = lines |> List.map (fun _ -> newLocal "__assert_text" IrString)
                let message = newLocal "__assert_message" IrString
                let parts =
                    Text header
                    :: (List.zip lines texts |> List.collect (fun ((label, _, _), text) -> [ Text $"  {label} = "; Hole(text, HoleString); Text "\n" ]))
                let crash =
                    List.foldBack2
                        (fun (_, var: Var, printer) text rest -> Expr.make (Let(text, Expr.make (Call(printer, [ AVar var ])) IrString, rest)) rest.Type)
                        lines
                        texts
                        (Expr.make (Let(message, Expr.make (Interpolate parts) IrString, Expr.make (CrashWith(AVar message)) IrUnit)) IrUnit)
                k (Expr.make (If(atoms.Head, Expr.make (Atom AUnit) IrUnit, crash)) IrUnit))
        bind scope values []

    /// Lowers `exprs` in order to atoms, binding each value that is not one to a temporary. An operand that stores to a
    /// local on the way to its value (see `storesBeforeValue`) is lowered whole, its stores with it, into one
    /// temporary: it is computed where it is used, so nothing computed before it waits on the stack across its stores.
    member this.Atoms (scope: Scope) (exprs: Expression list) (k: Atom list -> Expr) : Expr = this.AtomsAt scope exprs [] k

    /// `Atoms`, each operand wanted at the type in `tys` in its place, or at its own where `tys` is empty. An operand that
    /// never gives a value, such as a call to `crash`, whose type is `undefined`, the empty union, is a call of the type
    /// it is wanted at, whose result the emitter converts (see `CoreIrEmit`): an `i64` in a list of `int`s, say. As a
    /// value of its own type, an `i32`, it would be stored where an `i64` is, wherever code follows the call.
    member this.AtomsAt (scope: Scope) (exprs: Expression list) (tys: IrType list) (k: Atom list -> Expr) : Expr =
        let wanted =
            if tys.Length = exprs.Length then List.map Some tys else List.replicate exprs.Length None
        let atType (wanted: IrType option) (value: Expr) =
            match wanted, value.Kind with
            | Some ty, (Call _ | Crash _) when value.Type = IrTags [] -> { value with Type = ty }
            | _ -> value
        // A call through a function value in scope holds its arguments in locals where its type holds boxes, and a tuple
        // passed whole to one whose input is a tuple where the tuple is not a name, as it reads each element from it
        // (see `CallValue`).
        let callStores (call: Expression) =
            match call.Kind with
            | FunctionCall(name, _, args) when not (SyntaxTree.flattenCallArgs args).IsEmpty ->
                let value =
                    match scope.Vars |> Map.tryFind name with
                    | Some var -> Some var
                    | None -> scope.Members |> Map.tryFind (NamedMember name)
                let tupleNotNamed (var: Var) =
                    match var.Type, SyntaxTree.flattenCallArgs args with
                    | IrFunction _, [ { Kind = IdentifierExpr _ } ] -> false
                    | IrFunction(input, _, _, _), [ _ ] -> (IrType.tupleElements input).IsSome
                    | _ -> false
                value |> Option.exists (fun var -> CoreIrLowerBoxes.holdsBox var.Type || tupleNotNamed var)
            | _ -> false
        let rec go (acc: Atom list) (remaining: (Expression * IrType option) list) =
            match remaining with
            | [] -> k (List.rev acc)
            | (expr, _) :: rest when storesBeforeValue callStores storesFound expr -> bindTemp (this.Tail scope expr) (fun atom -> go (atom :: acc) rest)
            | (expr, ty) :: rest ->
                this.Complex scope expr (fun value ->
                    match value.Kind with
                    | Atom atom -> go (atom :: acc) rest
                    | _ -> bindTemp (atType ty value) (fun atom -> go (atom :: acc) rest))
        go [] (List.zip exprs wanted)

    /// `Atoms`, but binding each value that is not an atom to a local, so that code that stores can come between them
    /// and where they are used.
    member this.AtomsInLocals (scope: Scope) (exprs: Expression list) (k: Atom list -> Expr) : Expr =
        let rec go (acc: Atom list) (remaining: Expression list) =
            match remaining with
            | [] -> k (List.rev acc)
            | expr :: rest -> this.Complex scope expr (fun value -> settle value (fun atom -> go (atom :: acc) rest))
        go [] exprs

    /// Lowers a block, its value passed to `k`, a statement at a time (see `CoreIrLowerHoles`). In a debug build, each
    /// statement starts with a probe that can write the locals in scope there (see `CoreIrProbes`).
    member this.Block (scope: Scope) (statements: StatementKind list) (k: Expr -> Expr) : Expr =
        CoreIrLowerHoles.lowerBlock
            (fun scope statements ->
                let lowered, rest = this.Statement scope statements k
                let written =
                    match statements with
                    | (ExprStatement written | DefStatement(_, _, _, written) | UseStatement written) :: _ -> Place.ofSpan written.Span
                    | _ -> None
                match written with
                | Some place when input.Probes ->
                    let probe = CoreIrProbes.probe ProbeKind.Statement (probeLocals (probedInScope scope)) (Some place)
                    { lowered with Kind = Do(probe, lowered) }, rest
                | _ -> lowered, rest)
            scope
            statements

    /// The first of a block's `statements`, lowered around a hole (see `CoreIrLowerHoles.lowerBlock`).
    member private this.Statement (scope: Scope) (statements: StatementKind list) (k: Expr -> Expr) : Expr * (Expr * Scope * StatementKind list) option =
        let hole = CoreIrLowerHoles.hole ()
        let restScope = ref scope
        // A value a `use` below binds to a context member whose type has values boxed is lowered with its own type
        // variables boxed, as the member's are (see `CoreIrLowerBoxes`), and the rests of its unions without tags.
        match statements with
        | DefStatement(_, name, _, rhs) :: rest when this.BindsBoxingMember name rest ->
            match input.ExprTypes |> Map.tryFind rhs.Id |> Option.map (substitute subst) with
            | Some ty ->
                let values = valueVariables ty |> Set.ofList
                subst <- freeVars ty |> List.fold (fun acc id -> acc |> Map.add id (if values.Contains id then boxedTy else TyUnion [])) subst
            | None -> ()
        | _ -> ()
        match statements with
        | [] -> cannot "an empty block"
        | [ ExprStatement last ] -> this.Complex scope last k, None
        // A lambda's annotated result is the value it names (see `LambdaResultSyntax`), so a call there stays a tail call.
        | [ DefStatement(_, defined, _, value); ExprStatement(Kind (IdentifierExpr result)) ] when defined = LambdaResultSyntax.name && result = defined ->
            this.Complex scope value k, None
        | DefStatement(_, _, _, Kind (Lambda _)) :: _ -> cannot "a local function"
        | DefStatement(_, name, _, (Kind (ClosureSyntax.Closure(target, captured)) as rhs)) :: rest
            when (genericLocalType target rhs).IsSome ->
            CoreIrLowerLocals.genericLocal this.LocalTools scope name rhs target captured (genericLocalType target rhs).Value rest k, None
        | DefStatement(_, name, _, Kind (MemberAccess(Kind (IdentifierExpr environment), _, _))) :: rest
            when environment = ClosureSyntax.envParameter && scope.CapturedLocals.ContainsKey name ->
            // A captured local function generic in variables of its own, whose closure at each type arguments it is
            // used at here is in a field of the environment.
            let localTy, fields = scope.CapturedLocals.[name]
            let read (field: int) =
                this.Complex scope (Expression.synthetic (MemberAccess(Expression.synthetic (IdentifierExpr environment), string field, None)))
            CoreIrLowerLocals.localInstancesAround this.LocalTools scope name localTy (fields |> List.map (fun (typeArguments, field) -> typeArguments, read field)) None rest k, None
        | DefStatement(_, name, _, rhs) :: rest ->
            // A local value generic in variables of its own, such as the builder's member a workflow keyword reads
            // (`def m = proc.try`, then `m(args)`), is lowered once. Where it has one use, it is lowered at that use's
            // type arguments, so that what it reads is specialised as it is used rather than with its variables unit.
            // The contexts its function types require stay as they are: a member is passed them where it is called.
            match input.ExprTypes |> Map.tryFind rhs.Id |> Option.map (substitute subst) with
            | Some localTy when not (freeVars localTy).IsEmpty ->
                match CoreIrLowerLocals.localUses this.LocalTools Map.empty localTy name (Expression.synthetic (Block(rest |> List.map Statement.synthetic))) with
                | [ (_, bindings) ] ->
                    let values = freeVars localTy |> Set.ofList
                    subst <- bindings |> Map.filter (fun id _ -> values.Contains id) |> Map.fold (fun acc id ty -> acc |> Map.add id ty) subst
                | _ -> ()
            | _ -> ()
            // The local is named before its value is lowered, so that locals are declared in the order they are
            // written. Its type is its value's, which a local lambda lifting made has as well.
            let localName = uniqueName name
            // A captured member the lambda is specialised on is known in it, as in the function that made it: it is
            // given its value here rather than read from the environment, which may not hold it (see `Closure`).
            let ownerParameter = if scope.CapturedMembers.ContainsKey name then CoreIrLowerKnown.ownerParameter input self.Definition name else None
            let knownCapture =
                ownerParameter
                |> Option.bind (fun parameter ->
                    self.Known |> List.tryFind (fun (key, knownMember) -> key = parameter.Key && not (KnownMember.isPassed knownMember)))
            let valueOf (k: Expr -> Expr) =
                match knownCapture with
                | Some (_, knownMember) ->
                    CoreIrLowerMembers.valueOfKnown this.MemberTools knownMember (fun value ->
                        match ownerParameter |> Option.bind _.Type |> Option.map (substitute subst >> toIrType) with
                        | Some ty -> CoreIrLowerMembers.coerce this.MemberTools scope value ty (fun value -> k (Expr.make (Atom value) ty))
                        | None -> k (Expr.make (Atom value) (atomType value)))
                | None -> this.Complex scope rhs k
            let statement =
                valueOf (fun value ->
                    let local = newVar localName Local value.Type
                    knownCapture |> Option.iter (fun (_, knownMember) -> known.[local.Id] <- (local, knownMember))
                    this.NoteDefinedKnown local rhs value
                    input.ExprTypes |> Map.tryFind rhs.Id |> Option.iter (fun ty -> noteProbed name local (substitute subst ty))
                    let members =
                        match scope.CapturedMembers |> Map.tryFind name with
                        | Some keys -> keys |> List.fold (fun acc key -> acc |> Map.add key local) scope.Members
                        | None -> scope.Members
                    let probed = if probedLocals.ContainsKey local.Id then local :: scope.Probed else scope.Probed
                    restScope.Value <- { scope with Vars = scope.Vars |> Map.add name local; Probed = probed; Members = members }
                    Expr.make (Let(local, value, hole)) hole.Type)
            statement, Some (hole, restScope.Value, rest)
        | ExprStatement first :: rest -> this.Complex scope first (fun value -> Expr.make (Do(value, hole)) hole.Type), Some (hole, scope, rest)
        | UseStatement(Kind (RecordExpr fields)) :: rest ->
            // Lambda lifting has evaluated each binding into a local; the members are those locals. A function that
            // requires contexts its member's type does not is adapted to the type, with their members as they are here,
            // before the `use` (see `CoreIrLowerMembers.coerce`), and the local holds the adapted closure from here on.
            let bindings =
                fields
                |> List.map (function
                    | NamedField(name, Kind (IdentifierExpr local)) when scope.Vars.ContainsKey local ->
                        input.UseKeys |> Map.tryFind name |> Option.defaultValue (NamedMember name), local
                    | NamedField(name, _) -> cannot $"the `use` binding of '{name}'"
                    | PositionalField _ -> cannot "a `use` binding without a name")
            let rec adapt (remaining: (ContextMemberKey * string) list) (vars: Map<string, Var>) =
                match remaining with
                | [] ->
                    let members = bindings |> List.fold (fun (acc: Map<ContextMemberKey, Var>) (key, local) -> acc |> Map.add key vars.[local]) scope.Members
                    restScope.Value <- { scope with Members = members; Vars = vars }
                    hole
                | (key, local) :: others ->
                    match bindingMemberType key local with
                    | Some memberTy when vars.[local] = scope.Vars.[local] ->
                        CoreIrLowerMembers.coerce this.MemberTools scope (AVar vars.[local]) (toIrType memberTy) (function
                            | AVar var when var = vars.[local] -> adapt others vars
                            | adapted ->
                                let var = newVar (uniqueName local) Local (atomType adapted)
                                let body = adapt others (vars |> Map.add local var)
                                Expr.make (Let(var, Expr.make (Atom adapted) var.Type, body)) body.Type)
                    | _ -> adapt others vars
            let statement = adapt bindings scope.Vars
            statement, Some (hole, restScope.Value, rest)
        | UseStatement _ :: _ -> cannot "this kind of `use`"
        // A local type declares nothing that exists at run time: types are erased, and a nominal type's values are its
        // representation's.
        | TypeDefStatement _ :: rest -> this.Statement scope rest k
        | _ -> cannot "this kind of statement"

    /// Records the local `local`, which a `def` gives the value `rhs`, lowered to `value`, as known when `rhs` is a
    /// top-level function that is not generic and requires no contexts, named as a value (`def bump = addThree`, or what
    /// lambda lifting makes of `use Counter(bump = addThree)`), an int or bool literal, a lambda (`use Step(step = { x ->
    /// x + 3 })`), or a local known already. Such a local holds that value for as long as it is in scope (a cell's `def`
    /// is `mut(...)`, which is none of these), so a call through it is a call to the function, and a `use` that binds it
    /// to a context member makes the member known. A lambda that captures values is known as a function only: its
    /// closure, its environment, is still passed (#1877).
    member private this.NoteDefinedKnown (local: Var) (rhs: Expression) (value: Expr) =
        match rhs.Kind, value.Kind with
        | LiteralExpr(IntLit _ | BoolLit _), Atom(AInt _ | ASized _ | ABool _ as constant) ->
            definedKnown.[local.Id] <- KnownConstant(constant, value.Type)
        | IdentifierExpr _, Atom(AVar var) -> knownOf var |> Option.iter (fun knownMember -> definedKnown.[local.Id] <- knownMember)
        | ClosureSyntax.Closure(target, captured), _ ->
            match input.Lifted |> Map.tryFind target, value.Kind with
            | Some (FunctionWrapper wrapped), _ when
                captured.IsEmpty
                && not (input.ContextParameters.ContainsKey wrapped)
                && (contextVariables wrapped).IsEmpty
                && input.DefinitionTypes |> Map.tryFind wrapped |> Option.exists (freeVars >> List.isEmpty)
                ->
                let _, at = wrappedAt input target None
                definedKnown.[local.Id] <- KnownFunction(target, wrapped, at)
            | Some (LiftedLambda _), Atom(AStaticClosure(lifted, _, _) as closure) -> definedKnown.[local.Id] <- KnownLambda(lifted, closure)
            | Some (LiftedLambda _), MakeClosure(lifted, _, _) -> definedKnown.[local.Id] <- KnownLambdaOver lifted
            | _ -> ()
        | _ -> ()

    /// Whether a `use` among `statements` binds the local `name` to a context member whose type has values boxed.
    member private this.BindsBoxingMember (name: string) (statements: StatementKind list) =
        statements
        |> List.exists (function
            | UseStatement(Kind (RecordExpr fields)) ->
                fields
                |> List.exists (function
                    | NamedField(memberName, Kind (IdentifierExpr local)) when local = name ->
                        let key = input.UseKeys |> Map.tryFind memberName |> Option.defaultValue (NamedMember memberName)
                        bindingMemberType key local |> Option.exists (toIrType >> CoreIrLowerBoxes.holdsBox)
                    | _ -> false)
            | _ -> false)

    /// Lowers a `match` (see `CoreIrLowerMatch`).
    member this.Match (scope: Scope) (expr: Expression) (values: Expression list) (arms: (Parser.Program.Pattern list * Expression) list) (k: Expr -> Expr) : Expr =
        match values |> List.map (fun value -> input.ExprTypes |> Map.tryFind value.Id |> Option.map (substitute subst)) with
        // A match on a type's description (see `CoreIrLowerTypeMatch`).
        | [ Some (TypeValues.TypeValueType standsFor) ] -> CoreIrLowerTypeMatch.lower this.TypeMatchTools scope expr values.Head standsFor arms k
        | _ -> CoreIrLowerMatch.lowerMatch this.MatchTools scope expr values arms k

    /// What lowering a `match` on a type, or a function that builds descriptions, needs from this function's lowering
    /// (see `CoreIrLowerTypeMatch`).
    member private this.TypeMatchTools : CoreIrLowerTypeMatch.TypeMatchTools =
        { NewVar = newVar
          UniqueName = uniqueName
          ToIrType = toIrType
          TypeIds = input.TypeIds
          TypeDefinitions = input.TypeDefinitions
          ExprTypes = input.ExprTypes
          BindPackaged = fun id ty -> subst <- subst |> Map.add id ty
          TypeOf = tyOf
          Complex = this.Complex
          Branch = this.Branch
          Callee = callee
          Request = fun name lowering -> request (SpecKey.plain name []) Map.empty lowering
          Reader = (CoreIrLowerMembers.boxTools this.MemberTools emptyScope).Reader
          Convert = (CoreIrLowerMembers.boxTools this.MemberTools emptyScope).Convert }

    /// What lowering a `match` needs from this function's lowering (see `CoreIrLowerMatch`).
    member private this.MatchTools : CoreIrLowerMatch.MatchTools =
        { NewVar = newVar
          UniqueName = uniqueName
          TypeOf = tyOf
          Tail = this.Tail
          Branch = this.Branch
          Complex = this.Complex
          SourceRoot = DbgBuild.sourceRoot input.Dbg }

    /// The start function: each top-level expression in turn, its value dropped. The start function returns nothing,
    /// so nothing in it is a tail call; its body ends in unit, which the emitter leaves out (see `emitStart`).
    member this.StartFunction () : Function =
        let scope = emptyScope
        // An expression that holds errors deferred to run time, and cannot be lowered because of them, reports them
        // when it is reached instead.
        let lowerExpression (expr: Expression) =
            try this.Tail scope expr
            with CannotLower _ when not (deferredMessages expr).IsEmpty -> Expr.make (Crash(deferredMessages expr, true)) IrError
        let body =
            List.foldBack (fun (expr: Expression) (rest: Expr) -> Expr.make (Do(lowerExpression expr, rest)) IrUnit) input.TopLevel (Expr.make (Atom AUnit) IrUnit)
        { Name = startFunctionName
          Source = startFunctionName
          TypeArguments = []
          Params = []
          Result = IrUnit
          Body = body
          Place = None }

    /// The definition `name` as a function called `functionName` that reports `messages`, errors deferred to run time,
    /// as soon as it is called: what a definition that cannot be lowered because of them is instead. It takes the
    /// parameters it would take lowered as `lowering` says, and results in what it would, so that a call through a
    /// function value calls it at the signature the value's type gives. Where even those cannot be lowered, it takes
    /// the parameters the definition does, context members included, as values of no known type.
    member this.Trap (lowering: Lowering) (name: string) (functionName: string) (typeArguments: IrType list) (messages: string list) : Function =
        let crash = Expr.make (Crash(messages, true)) IrError
        let asWritten =
            match lowering with
            | AsDefined
            | LiftedCapturing _ ->
                try Some (this.Lowered (Some crash) lowering name functionName typeArguments)
                with CannotLower _ -> None
            | _ -> None
        match asWritten with
        | Some func -> func
        | None ->
        let parameterNames, _ = parametersAndBody definitions.[name]
        let contextParameters = input.ContextParameters |> Map.tryFind name |> Option.map fst |> Option.defaultValue []
        let parameters =
            (parameterNames |> List.map (fun parameterName -> newVar (uniqueName parameterName) Param IrError))
            @ (contextParameters
               |> List.filter (fun parameter ->
                   not (self.Known |> List.exists (fun (key, knownMember) -> key = parameter.Key && not (KnownMember.isPassed knownMember))))
               |> List.map (fun parameter -> newVar parameter.Name Param IrError))
            @ (variableMembers name subst |> List.map (fun (key, _) -> newVar (contextMemberParameter key) Param IrError))
        { Name = functionName
          Source = name
          TypeArguments = typeArguments
          Params = parameters
          Result = IrError
          Body = crash
          Place = definitionPlace name }

    /// The types of the parameters of the lifted lambda `name` (its environment aside), whose type where it was written
    /// is `lambdaTy`, and of its result. A lambda bound to a context member in a `use` may have parameters the type
    /// checker left open; lambda lifting annotates them with the types the member declares.
    member this.LiftedParameterTypes (name: string) (lambdaTy: Ty) : Ty list * Ty =
        let parameters =
            match definitions.[name].Kind with
            | Lambda(parameters, _, _) -> parameters.Tail
            | _ -> []
        let parameterTys, resultTy = parameterTypes (List.map fst parameters) lambdaTy true
        let annotated =
            List.map2 (fun (ty: Ty) (_, annotation) ->
                match annotation |> Option.bind (typeOfAnnotation input.NamedTypes) with
                | Some annotatedTy when not (freeVars (substitute subst ty)).IsEmpty -> annotatedTy
                | _ -> ty) parameterTys parameters
        annotated, resultTy


    /// Lowers the definition `name` to a function called `functionName`, as `lowering` says.
    member this.Function (lowering: Lowering) (name: string) (functionName: string) (typeArguments: IrType list) : Function =
        this.Lowered None lowering name functionName typeArguments

    /// Lowers the definition `name` as `Function` does, but with `crash` in place of its body when it is given (see
    /// `Trap`).
    member private this.Lowered (crash: Expr option) (lowering: Lowering) (name: string) (functionName: string) (typeArguments: IrType list) : Function =
        selfName <- functionName
        match lowering with
        | AsStart -> this.StartFunction ()
        | Adapting(givenTy, expectedTy) -> CoreIrLowerLifted.adapter this.LiftedTools functionName givenTy expectedTy
        | MemberCalling(key, expectedTy) -> CoreIrLowerLifted.memberCalling this.LiftedTools functionName key expectedTy
        | ValueAdapting(givenTy, expectedTy) -> CoreIrLowerMembers.valueAdapter this.MemberTools emptyScope functionName givenTy expectedTy
        | BoxReading ty -> CoreIrLowerBoxes.readerFunction newVar functionName ty
        | StandingIn ty -> CoreIrLowerInstances.standInFunction newVar functionName ty
        | BoxedCalling(boxed, boxedTy, expectedTy) -> CoreIrLowerLifted.boxedCalling this.LiftedTools functionName boxed boxedTy expectedTy
        | ListConverting(given, expected) -> CoreIrLowerBoxes.listConverterFunction (CoreIrLowerMembers.boxTools this.MemberTools emptyScope) newVar functionName given expected
        | RecursiveConverting(given, expected) -> CoreIrLowerBoxes.recursiveConverterFunction (CoreIrLowerMembers.boxTools this.MemberTools emptyScope) newVar functionName given expected
        | Printing printer -> CoreIrLowerDbg.printerFunction printerTools callee functionName printer
        | Describing -> CoreIrLowerTypeMatch.describingFunction this.TypeMatchTools functionName
        | FieldGetting(recordType, fieldName) -> CoreIrLowerTypeMatch.fieldGettingFunction this.TypeMatchTools functionName recordType fieldName
        | CaseGetting(unionType, caseName) -> CoreIrLowerTypeMatch.caseGettingFunction this.TypeMatchTools functionName unionType caseName
        | Canonicalising collection -> CoreIrLowerTypeMatch.canonicalisingFunction this.TypeMatchTools functionName collection
        | Decanonicalising collection -> CoreIrLowerBuild.decanonicalisingFunction this.TypeMatchTools functionName collection
        | CanonicalisingValue ty -> CoreIrLowerTypeMatch.canonicalisingValueFunction this.TypeMatchTools functionName ty
        | DecanonicalisingValue ty -> CoreIrLowerBuild.decanonicalisingValueFunction this.TypeMatchTools functionName ty
        | MakingRecord recordType -> CoreIrLowerBuild.makingFunction this.TypeMatchTools functionName recordType
        | CaseBuilding(unionType, caseName) -> CoreIrLowerBuild.caseBuildingFunction this.TypeMatchTools functionName unionType caseName
        | Building buildTy -> CoreIrLowerBuild.buildingFunction this.TypeMatchTools functionName buildTy
        | BuildingFields buildTy -> CoreIrLowerBuild.buildingFieldsFunction this.TypeMatchTools functionName buildTy
        | _ ->
        let expr = definitions.[name]
        let parameterNames, body = parametersAndBody expr
        let isLambda = match expr.Kind with Lambda _ -> true | _ -> false
        let parameterOf (parameterName: string) (ty: IrType) = newVar (uniqueName parameterName) Param ty
        let scopeOf (parameters: Var list) =
            { emptyScope with Vars = List.zip parameterNames (parameters |> List.truncate parameterNames.Length) |> Map.ofList }
        let make (parameters: Var list) (result: IrType) (body: Expr) =
            { Name = functionName
              Source = name
              TypeArguments = typeArguments
              Params = parameters
              Result = result
              Body = body
              Place = definitionPlace name }
        // A function the source writes, in a debug build, starts with a probe that can write its parameters and ends
        // with one (see `CoreIrProbes`).
        let probed (parameters: Var list) (result: IrType) (body: Expr) =
            if not input.Probes then make parameters result body
            else
                let newResult (ty: IrType) = newVar (uniqueName "__result") Local ty
                make parameters result (CoreIrProbes.around newResult (CoreIrProbes.endsTheProgram input.DefinitionTypes) name (Place.ofSpan expr.Span) (probeLocals parameters) body)
        match lowering with
        | AsDefined ->
            let defTy =
                match input.ExprTypes |> Map.tryFind expr.Id with
                | Some ty -> substitute subst ty
                | None -> cannot "a definition without a type"
            let parameterTys, resultTy = parameterTypes parameterNames defTy isLambda
            let parameters = List.map2 (fun parameterName ty -> parameterOf parameterName (toIrType ty)) parameterNames parameterTys
            List.iter2 (fun parameterName (parameter, ty) -> noteProbed parameterName parameter ty) parameterNames (List.zip parameters parameterTys)
            // The members of the contexts it requires follow, each a closure in a parameter of its own, but those it is
            // specialised on, each in a local that has the member's value only if the body uses it as one.
            let contextParameters, aliases = input.ContextParameters |> Map.tryFind name |> Option.defaultValue ([], Map.empty)
            let members =
                contextParameters
                |> List.map (fun parameter ->
                    let knownMember = self.Known |> List.tryFind (fst >> (=) parameter.Key) |> Option.map snd
                    match parameter.Type with
                    | Some ty ->
                        match knownMember with
                        // A lambda's environment is passed, and a call through it is a call to the lambda.
                        | Some knownMember when KnownMember.isPassed knownMember ->
                            let var = newVar parameter.Name Param (toIrType (substitute subst ty))
                            definedKnown.[var.Id] <- knownMember
                            parameter.Key, var
                        | _ ->
                            let var = newVar parameter.Name (if knownMember.IsSome then Local else Param) (toIrType (substitute subst ty))
                            knownMember |> Option.iter (fun knownMember -> known.[var.Id] <- (var, knownMember))
                            parameter.Key, var
                    | None -> cannot $"the context member '{ContextMemberKey.text parameter.Key}', whose type is not known")
            // Then those of the contexts its context variables stand for here.
            let members =
                members @ (variableMembers name subst |> List.map (fun (key, ty) -> key, newVar (contextMemberParameter key) Param ty))
            let memberScope =
                aliases
                |> Map.fold (fun acc alias key -> acc |> Map.add (NamedMember alias) (Map.ofList members).[key]) (Map.ofList members)
            // A lambda lifted out of the function captures each member by its parameter's name.
            let parameterVars = members |> List.map (fun (_, var) -> var.Name, var)
            let scope = scopeOf parameters
            let scope = { scope with Members = memberScope; Vars = parameterVars |> List.fold (fun acc (name, var) -> acc |> Map.add name var) scope.Vars }
            // A function value it results in is adapted to the type it declares (see `CoreIrLowerMembers.coerce`), and so
            // is a value of a type generic in contexts, whose type its value may carry fewer of than it declares, which
            // is what its callers read: `def lines: () -> [Console] Stream(string) = { pure }`. A generic def's declared
            // result is read as a plain one's once its type arguments are in, though its parameters may still have a
            // variable, as a list's element type does in `list(a) -> [Console] Thunk(int)` called with `[]` (#1872).
            let resultTy =
                match input.DefinitionTypes |> Map.tryFind name |> Option.map (substitute subst) with
                | Some declared ->
                    match snd (parameterTypes parameterNames declared isLambda) with
                    | declaredResult when (freeVars declaredResult).IsEmpty -> declaredResult
                    | _ -> resultTy
                | None -> resultTy
            let result = toIrType resultTy
            let adapted (value: Expr) =
                match value.Type with
                | IrFunction _
                | IrRecord _ when value.Type <> result -> bindTemp value (fun atom -> CoreIrLowerMembers.coerce this.MemberTools scope atom result (fun atom -> Expr.make (Atom atom) result))
                | _ -> value
            let parameters = parameters @ (members |> List.map snd |> List.filter (fun var -> var.Kind = Param))
            match crash with
            | Some crash -> probed parameters result crash
            | None -> probed parameters result (this.WithKnownValues (this.Complex scope body adapted))
        | LiftedCapturing(capturedTys, capturedLocals) ->
            let lowerBody (scope: Scope) (body: Expression) = crash |> Option.defaultWith (fun () -> this.Tail scope body)
            CoreIrLowerLifted.liftedCapturing this.LiftedTools name parameterNames body lowerBody scopeOf probed capturedTys capturedLocals
        | WrapperCalling(wrapped, instanceTy, captured) ->
            CoreIrLowerLifted.wrapperCalling this.LiftedTools wrapped instanceTy captured parameterNames make
        | Adapting _ -> internalError "an adapter lowered as a definition"
        | MemberCalling _ -> internalError "a member's caller lowered as a definition"
        | ValueAdapting _ -> internalError "a value's adapter lowered as a definition"
        | BoxReading _ -> internalError "a box's function lowered as a definition"
        | StandingIn _ -> internalError "a stand-in lowered as a definition"
        | BoxedCalling _ -> internalError "a call through a boxed specialisation lowered as a definition"
        | ListConverting _ -> internalError "a list's conversion lowered as a definition"
        | RecursiveConverting _ -> internalError "a recursive type's conversion lowered as a definition"
        | Printing _ -> internalError "a printer lowered as a definition"
        | Describing
        | FieldGetting _
        | CaseGetting _
        | Canonicalising _
        | Decanonicalising _
        | CanonicalisingValue _
        | DecanonicalisingValue _
        | MakingRecord _
        | CaseBuilding _
        | Building _
        | BuildingFields _ -> internalError "a part of a type's description lowered as a definition"
        | AsStart -> internalError "the start function lowered as a definition"

    /// What lowering a lifted lambda, a wrapper or an adapter needs from this function's lowering (see
    /// `CoreIrLowerLifted`).
    member private this.LiftedTools : CoreIrLowerLifted.LiftedTools =
        { Input = input
          NewVar = newVar
          UniqueName = uniqueName
          Substitute = fun ty -> substitute subst ty
          ToIrType = toIrType
          MembersOf = membersOf
          EmptyScope = emptyScope
          BindTemp = bindTemp
          BindLocal = bindLocal
          Settle = settle
          Coerce = CoreIrLowerMembers.coerce this.MemberTools
          CoerceAll = CoreIrLowerMembers.coerceAll this.MemberTools
          ValueParameters = this.ValueParameters
          ParameterNames = parameterNames
          LiftedParameterTypes = this.LiftedParameterTypes
          Callee = callee
          BindingsAt = bindingsAt
          VariableMembers = variableMembers
          SetSelfMembers = fun members -> selfMembers <- members
          Tail = this.Tail }
