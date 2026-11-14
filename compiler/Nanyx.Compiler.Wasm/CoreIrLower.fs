/// Lowers the typed AST to the core IR (`CoreIr.fs`), specialising generic functions as it goes.
///
/// Each definition whose type is concrete is lowered as a root. A call to a generic definition asks for its
/// specialisation at the call's type arguments, and on the context members it knows (see `CoreIrLowerKnown`), which
/// is lowered in turn (and may ask for more). Every function is
/// compiled from the IR. What it cannot lower yet (`cannot`) is reported by `CodeGen.fs` as `UnsupportedByBackend`
/// where it is written, and a broken invariant of the backend (`internalError`) as `BackendInternalError`: see "What
/// cannot be compiled" in `docs/src/content/work-in-progress/memory-management.md`.
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

    /// A parameter's or local's name: as written, made unique within the function.
    let uniqueName (name: string) =
        let candidate = freeName usedNames.Contains (sanitizeIdentifier name)
        usedNames.Add candidate |> ignore
        candidate

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
          ToIrType = toIrType }


    let wrappedOf = wrappedOf input


    /// The members of the contexts in `contexts`, in the order a function value takes them.
    let membersOf = contextMembersOf input.ContextLayouts

    /// The context variables of the definition `name`'s type (`[c]` in `[c] ([c] a -> b), a -> b`), by id.
    let contextVariables (name: string) =
        input.DefinitionTypes |> Map.tryFind name |> Option.map (FunctionContexts.variablesIn >> Map.keys >> List.ofSeq) |> Option.defaultValue []

    /// The context variables of the definition `name`'s type that only the function it returns requires (`[c]` in
    /// `([c] a -> b), ([c] b -> d) -> [c] a -> d`): that function takes their members when it is called, so a call to
    /// the definition does not pass them, though it is specialised on them.
    let returnedVariables (name: string) =
        let ofContexts (contexts: Ty list) = contexts |> List.choose (function TyVar v -> Some v.Id | _ -> None) |> Set.ofList
        match input.DefinitionTypes |> Map.tryFind name |> Option.map Unifier.unalias with
        | Some (TyFunc(_, result, own)) -> Set.difference (ofContexts (requiredContexts result)) (ofContexts own)
        | _ -> Set.empty

    /// The members of the contexts the context variables of the definition `name` stand for where `bindings` binds them,
    /// which the definition takes after those of the contexts it declares, as each call passes them.
    let variableMembers (name: string) (bindings: Map<int, Ty>) =
        let declared =
            input.ContextParameters |> Map.tryFind name |> Option.map (fst >> List.map _.Key) |> Option.defaultValue [] |> Set.ofList
        let returned = returnedVariables name
        contextVariables name
        |> List.filter (returned.Contains >> not)
        |> List.collect (fun id -> bindings |> Map.tryFind id |> Option.map List.singleton |> Option.defaultValue [])
        |> membersOf
        |> List.filter (fst >> declared.Contains >> not)


    let tyOf (expr: Expression) : IrType =
        match input.ExprTypes |> Map.tryFind expr.Id with
        | Some ty -> toIrType (substitute subst ty)
        | None -> cannot "an expression without a type"

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
        match definitions.[name].Kind with
        | Lambda(parameters, _, _) -> parameters |> List.map fst
        | _ -> []

    /// What the type variables and context variables of the definition `name` are where its type is `instanceTy`.
    let bindingsAt (name: string) (instanceTy: Ty) =
        match input.DefinitionTypes |> Map.tryFind name with
        | Some scheme -> bindScheme Map.empty scheme (substitute subst instanceTy)
        | None -> Map.empty

    /// The name to call `name` by at the instance type `instanceTy`, given the members known where it is called: its
    /// own, or a specialisation's. A definition with context variables is specialised on the members of the contexts
    /// they stand for, unless they stand for none.
    let calleeKnowing (name: string) (instanceTy: Ty) (knownMembers: (ContextMemberKey * KnownMember) list) =
        let scheme =
            match input.DefinitionTypes |> Map.tryFind name with
            | Some scheme -> scheme
            | None -> cannot $"a call to '{name}', whose type is not known"
        let typeVars = freeVars scheme |> List.distinct |> List.sort
        let contextVars = contextVariables name
        if typeVars.IsEmpty && contextVars.IsEmpty && knownMembers.IsEmpty then sanitizeIdentifier name
        else
            let bindings = bindingsAt name instanceTy
            let typeArguments =
                typeVars
                |> List.map (fun id ->
                    match bindings |> Map.tryFind id with
                    | Some ty -> toIrType ty
                    // A type argument the call does not fix is a type variable nothing constrains (see `toIrType`).
                    | None -> IrUnit)
            let contextArguments =
                contextVars
                |> List.map (fun id -> bindings |> Map.tryFind id |> Option.map List.singleton |> Option.defaultValue [] |> membersOf)
            let typeArguments =
                if contextArguments |> List.forall List.isEmpty then typeArguments
                else typeArguments @ (contextArguments |> List.map (fun members -> IrFunction(IrUnit, IrUnit, members)))
            if typeArguments.IsEmpty && knownMembers.IsEmpty then sanitizeIdentifier name
            else request { Definition = name; TypeArguments = typeArguments; Known = knownMembers } bindings AsDefined

    let callee (name: string) (instanceTy: Ty) = calleeKnowing name instanceTy []

    /// A scope with nothing in it, as at the top of the start function.
    let emptyScope =
        { Vars = Map.empty
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
        if not (SourceSpan.isNone expr.Span) then current <- Some expr.Span
        let k (value: Expr) =
            current <- around
            k value
        match expr.Kind with
        | UnitExpr -> k (Expr.make (Atom AUnit) IrUnit)
        | LiteralExpr(IntLit value) -> k (Expr.make (Atom(intAtom value (tyOf expr))) (tyOf expr))
        | LiteralExpr(BoolLit value) -> k (Expr.make (Atom(ABool value)) IrBool)
        | LiteralExpr(StringLit value) -> k (Expr.make (Atom(AString value)) IrString)
        | LiteralExpr(FloatLit value) -> k (Expr.make (BoxFloat value) (tyOf expr))
        | IdentifierExpr name ->
            match scope.Vars |> Map.tryFind name with
            | Some var ->
                let var = CoreIrLowerLocals.localAt this.LocalTools scope var (input.ExprTypes |> Map.tryFind expr.Id)
                k (Expr.make (Atom(AVar var)) var.Type)
            // A context member in scope, named as a value (`` `0` ``, or a function member passed on).
            | None when scope.Members.ContainsKey (NamedMember name) && not (namesOwnDefinition input expr) ->
                let memberVar = scope.Members.[NamedMember name]
                k (Expr.make (Atom(AVar memberVar)) memberVar.Type)
            | None when definitions.ContainsKey name && not (isFunction name) ->
                // A top-level value is compiled to a function of no parameters: evaluate it.
                let ty = tyOf expr
                k (Expr.make (Call(callee name input.ExprTypes.[expr.Id], [])) ty)
            | None when definitions.ContainsKey name -> cannot $"the function '{name}' used as a value"
            | None -> cannot $"a reference to '{name}'"
        | TagExpr(name, None) -> k (Expr.make (Atom(ATag name)) (tyOf expr))
        | TagExpr(name, Some payload) ->
            let ty = tyOf expr
            this.Atoms scope [ payload ] (fun atoms -> k (Expr.make (MakeTag(name, atoms.Head)) ty))
        | BinaryOp("++", _, Kind (ListExpr items), rest) when not items.IsEmpty ->
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
        | BinaryOp("++", _, left, right) ->
            // Joining strings or lists: a runtime helper for each.
            let helper =
                match tyOf left with
                | IrString -> Helpers.stringConcat
                | IrList _ -> Helpers.listConcat
                | other -> cannot $"the operator ++ on a value of type {IrType.show other}"
            let ty = tyOf expr
            this.Atoms scope [ left; right ] (fun atoms -> k (Expr.make (CallRuntime(helper, atoms, true)) ty))
        | BinaryOp(("==" | "!=") as op, _, left, right) when (match tyOf left with
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
        | BinaryOp(("<" | "<=" | ">" | ">=") as op, _, left, right) when (match tyOf left with
                                                                         | IrString | IrList _ -> true
                                                                         | IrRecord fields -> IrType.isTuple fields
                                                                         | _ -> false) ->
            // A string, a tuple and a list are ordered by their structure (see `CoreIrOrdering`): their order, a
            // number, is compared with 0. Records, tags and functions have no order: a local function that orders them
            // is not supported. (A record inside a list or tuple is ordered field by field, only where a generic
            // function orders a `list(a)`, see `CoreIrOrdering`.)
            if IrType.holdsUnordered (tyOf left) then cannot $"the operator {op} on a value of type {IrType.show (tyOf left)}"
            this.Atoms scope [ left; right ] (fun atoms ->
                let order = Expr.make (CallRuntime(Helpers.valueCompare, atoms, true)) IrInt
                bindTemp order (fun result -> k (Expr.make (Prim(Binary op, [ result; AInt 0L ])) IrBool)))
        | BinaryOp(op, _, left, right) when IrType.isFloat (tyOf left) || IrType.isFloat (tyOf right) ->
            // Floats are boxed: the operands are unboxed, and an arithmetic result boxed.
            // `%` has no instruction: it calls a runtime helper (see `CoreIrEmit`).
            if op <> "%" && (wasmFloatInstruction op).IsNone then cannot $"the operator {op} on floats"
            let ty = tyOf expr
            this.Atoms scope [ left; right ] (fun atoms -> k (Expr.make (Prim(FloatBinary op, atoms)) ty))
        | BinaryOp(op, _, left, right) ->
            if (wasmBinaryInstruction op).IsNone then cannot $"the operator {op}"
            for operand in [ left; right ] do
                match tyOf operand with
                | IrInt | IrSized _ | IrBool | IrUnit | IrError -> ()
                | other -> cannot $"the operator {op} on a value of type {IrType.show other}"
            let ty = tyOf expr
            this.Atoms scope [ left; right ] (fun atoms -> k (Expr.make (Prim(Binary op, atoms)) ty))
        | FunctionCall(name, _, [ operand ]) when name = NotSyntax.builtIn && not (scope.Vars.ContainsKey name) && not (definitions.ContainsKey name) ->
            this.Atoms scope [ operand ] (fun atoms -> k (Expr.make (Prim(Not, atoms)) IrBool))
        | FunctionCall(name, _, [ operand ]) when name = NegateSyntax.builtIn ->
            // `-x` on a built-in number (on any other type it calls a `negate`). An integer is `0 - x`, and a float
            // `-0.0 - x`, which is IEEE 754's negation: `-0.0 - 0.0` is `-0.0`, and `-0.0 - -0.0` is `0.0`.
            let ty = tyOf expr
            this.Atoms scope [ operand ] (fun atoms ->
                match ty with
                | IrFloat | IrF32 -> bindTemp (Expr.make (BoxFloat -0.0) ty) (fun zero -> k (Expr.make (Prim(FloatBinary "-", zero :: atoms)) ty))
                | IrInt -> k (Expr.make (Prim(Binary "-", AInt 0L :: atoms)) ty)
                | IrSized integer -> k (Expr.make (Prim(Binary "-", ASized(integer, 0L) :: atoms)) ty)
                | other -> cannot $"the operator - on a value of type {IrType.show other}")
        | ForSyntax.Call(iterables, _, parameters, body) when parameters.Length = iterables.Length ->
            let depth = scope.ForDepth
            let lowerBody vars = this.Tail { scope with Vars = vars; ForDepth = depth + 1 } body
            CoreIrLowerLoops.forEach newVar tyOf (this.Atoms scope) lowerBody scope.Vars depth iterables parameters k
        | WhileSyntax.Call(condition, body) ->
            let inner = { scope with ForDepth = scope.ForDepth + 1 }
            k (CoreIrLowerLoops.whileLoop scope.ForDepth (this.Tail inner condition) (this.Tail inner body))
        // A cell (see `CellSyntax`) no lambda captures is the local its def binds, and one a lambda captures is a box of
        // one field. A read is a copy of what the cell holds then, so that a later `:=` leaves the copy as it is.
        | MutSyntax.Call value -> this.Complex scope value k
        | FunctionCall(form, _, [ Kind (IdentifierExpr name) ]) when (form = CellSyntax.read || form = CellSyntax.unbox) && scope.Vars.ContainsKey name ->
            let cell = scope.Vars.[name]
            let value =
                if form = CellSyntax.read then Expr.make (Atom(AVar cell)) cell.Type
                else
                    match cell.Type with
                    | IrRecord [ _, held ] -> Expr.make (Field(AVar cell, 0)) held
                    | other -> internalError $"a box of type {IrType.show other}"
            let copy = newVar (uniqueName name) Local value.Type
            let body = k (Expr.make (Atom(AVar copy)) value.Type)
            Expr.make (Let(copy, value, body)) body.Type
        | AssignSyntax.Call(Kind (IdentifierExpr name), value) when scope.Vars.ContainsKey name ->
            this.Atoms scope [ value ] (fun atoms -> k (Expr.make (Assign(scope.Vars.[name], atoms.Head)) IrUnit))
        | FunctionCall(form, _, [ value ]) when form = CellSyntax.box ->
            this.Atoms scope [ value ] (fun atoms -> k (Expr.make (MakeRecord [ "0", atoms.Head ]) (IrRecord [ "0", atomType atoms.Head ])))
        | FunctionCall(form, _, [ Kind (IdentifierExpr name); value ]) when form = CellSyntax.setBox && scope.Vars.ContainsKey name ->
            this.Atoms scope [ value ] (fun atoms -> k (Expr.make (SetField(scope.Vars.[name], 0, atoms.Head)) IrUnit))
        | PanicSyntax.Call message ->
            // One the type checker added (the last arm of a match that does not handle every case) has no type.
            let ty = if input.ExprTypes.ContainsKey expr.Id then tyOf expr else IrError
            k (Expr.make (Panic [ message ]) ty)
        | FunctionCall(name, _, args) when name = DbgSyntax.builtIn && not (scope.Vars.ContainsKey name) && not (definitions.ContainsKey name) ->
            // Prints the value's text, which the printer of its type writes, and leaves the value.
            // The one argument, which may be a tuple, as the type checker has it.
            let value =
                match args with
                | [ single ] -> single
                | _ -> cannot "`dbg` of more than one value"
            let valueTy = unconstrainedAsUnit (substitute subst input.ExprTypes.[expr.Id])
            let printer = printerTools.PrinterOf (CoreIrLowerDbg.Showing valueTy)
            this.Complex scope value (fun lowered ->
                let held = newVar (uniqueName "__dbg_value") Local lowered.Type
                let text = newTemp IrString
                let rest = k (Expr.make (Atom(AVar held)) held.Type)
                let printed = Expr.make (Let(text, Expr.make (Call(printer, [ AVar held ])) IrString, Expr.make (Dbg(AVar text)) IrUnit)) IrUnit
                Expr.make (Let(held, lowered, Expr.make (Do(printed, rest)) rest.Type)) rest.Type)
        | WithSyntax.Call(record, Kind (RecordExpr updates)) ->
            // `record with f = v, ...`: a copy of the record with those fields replaced.
            let ty = tyOf expr
            let fields =
                match tyOf record with
                | IrRecord fields -> fields
                | other -> cannot $"`with` on a value of type {IrType.show other}"
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
            | _ -> cannot $"the type constructor {name} without a record of its fields"
        | FunctionCall(name, _, args) when scope.Members.ContainsKey (NamedMember name) && not (namesOwnDefinition input expr) ->
            // A context member in scope: a function value like any other.
            let ty = tyOf expr
            this.CallValue scope expr.Id scope.Members.[NamedMember name] args ty k
        | FunctionCall(name, _, [ value ]) when Intrinsics.reinterpretations.Contains name && not (scope.Vars.ContainsKey name) ->
            // A map and the list of its entries are held the same way: the value is its argument.
            this.Complex scope value k
        | FunctionCall(name, _, args) when Intrinsics.isIntrinsic name && not (scope.Vars.ContainsKey name) ->
            // A compiler intrinsic (only the standard library can call these, and a built-in property such as
            // `s.length` is one): the runtime helper of the same name.
            let ty = tyOf expr
            this.Atoms scope (SyntaxTree.flattenCallArgs args) (fun atoms ->
                k (Expr.make (CallRuntime(name, atoms, not (Intrinsics.returnsUnit name))) ty))
        | ClosureSyntax.Closure(target, captured) ->
            // A function value: the closure block of a lifted function over what it captures.
            match input.Lifted |> Map.tryFind target with
            | Some (LiftedLambda _) -> this.Closure scope expr target captured Map.empty [] k
            | Some (FunctionWrapper wrapped) ->
                // A top-level function passed as a value, through a wrapper for each type it is passed at. Each call
                // passes the members of the contexts the value's type requires (see `IrFunction`); the closure captures
                // those of the others the function requires, as they are where it is named (a parameterised context).
                let ty = tyOf expr
                let arity = IrType.closureArity (parameterNames target) ty
                let instanceTy = substitute subst input.ExprTypes.[expr.Id]
                let passed =
                    match ty with
                    | IrFunction(_, _, members) -> members |> List.map fst |> Set.ofList
                    | _ -> Set.empty
                let specialised =
                    input.DefinitionTypes |> Map.tryFind wrapped |> Option.exists (freeVars >> List.isEmpty >> not)
                    || not (contextVariables wrapped).IsEmpty
                let key = SpecKey.plain target (if specialised then [ toIrType instanceTy ] else [])
                CoreIrLowerMembers.contextMembers this.MemberTools scope wrapped expr.Id instanceTy passed [] (fun captured ->
                    let name = request key subst (WrapperCalling(wrapped, instanceTy, captured |> List.map (fun (memberKey, atom) -> memberKey, atomType atom)))
                    k (closureOver name arity (List.map snd captured) ty))
            | None -> cannot $"a closure over '{target}', which lambda lifting did not make"
        | FunctionCall(name, _, args) when scope.Vars.ContainsKey name ->
            // A call through a function value in a parameter or local.
            let ty = tyOf expr
            match CoreIrLowerLocals.localAt this.LocalTools scope scope.Vars.[name] (input.CalleeTypes |> Map.tryFind expr.Id) with
            | { Type = IrFunction _ } as var -> this.CallValue scope expr.Id var args ty k
            | var -> cannot $"a call to '{name}', a value of type {IrType.show var.Type}"
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
        | FunctionCall(name, _, args) ->
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
                        | several -> TyRecord(several |> List.mapi (fun index ty -> string index, ty) |> Map.ofList)
                    match input.ExprTypes |> Map.tryFind expr.Id with
                    | Some output -> TyFunc(input', output, [])
                    | None -> cannot $"a call to '{name}' whose type is not known"
            let knownMembers = CoreIrLowerMembers.knownMembers this.MemberTools scope name expr.Id instanceTy
            let target = calleeKnowing name instanceTy knownMembers
            let ty = tyOf expr
            let parameters = parameterNames name
            // A function value given for a parameter whose type requires other contexts is adapted to it (see `CoreIrLowerMembers.coerce`).
            let parameterTys =
                try parameterTypes parameters (substitute subst instanceTy) true |> fst |> List.map toIrType
                with CannotLower _ -> []
            // The context members the callee requires follow its arguments.
            let call atoms =
                CoreIrLowerMembers.coerceAll this.MemberTools scope atoms (if parameterTys.Length = atoms.Length then parameterTys else []) (fun atoms ->
                    CoreIrLowerMembers.contextMembers this.MemberTools scope name expr.Id instanceTy Set.empty knownMembers (fun members ->
                        k (Expr.make (Call(target, atoms @ List.map snd members)) ty)))
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
                this.Atoms scope ordered call
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
            | flatArgs when flatArgs.Length = parameters.Length -> this.Atoms scope flatArgs call
            | _ -> cannot $"a call to '{name}' with its arguments in another shape"
        | TupleExpr items -> this.Record scope (items |> List.mapi (fun index item -> string index, item)) (tyOf expr) k
        | ListExpr items ->
            // Each element that is a function value is adapted to the list's element type (see `CoreIrLowerMembers.coerce`).
            let ty = tyOf expr
            let elementTys =
                match ty with
                | IrList elementTy -> items |> List.map (fun _ -> elementTy)
                | _ -> []
            this.Atoms scope items (fun atoms -> CoreIrLowerMembers.coerceAll this.MemberTools scope atoms elementTys (fun atoms -> k (Expr.make (MakeList atoms) ty)))
        | RecordExpr fields -> this.Record scope (recordExprFields fields |> sortFields) (tyOf expr) k
        | MemberAccess(Kind (IdentifierExpr qualifier), fieldName, _) when
            not (scope.Vars.ContainsKey qualifier) && definitions.ContainsKey $"{qualifier}.{fieldName}" ->
            // A value of another module, named through it.
            this.Complex scope { expr with Kind = IdentifierExpr $"{qualifier}.{fieldName}" } k
        | MemberAccess(record, fieldName, _) ->
            // The field's type is the record's for it: a lifted function reads what it captures from its environment
            // with reads the type checker never saw.
            let recordTy =
                match record.Kind with
                | IdentifierExpr name when scope.Vars.ContainsKey name -> scope.Vars.[name].Type
                | _ -> tyOf record
            let slot, ty =
                match recordTy with
                | IrRecord fields ->
                    match fields |> List.tryFindIndex (fst >> (=) fieldName) with
                    | Some slot -> slot, snd fields.[slot]
                    | None -> cannot $"the field '{fieldName}', which the record does not have"
                | other -> cannot $"the field '{fieldName}' of a value of type {IrType.show other}"
            this.Atoms scope [ record ] (fun atoms -> k (Expr.make (Field(atoms.Head, slot)) ty))
        | IfExpr(cond, ifTrue, ifFalse) ->
            let ty = tyOf expr
            this.Atoms scope [ cond ] (fun atoms ->
                k (Expr.make (If(atoms.Head, this.Tail scope ifTrue, this.Tail scope ifFalse)) ty))
        | Block statements -> this.Block scope (statements |> List.map _.Kind) k
        | ExpressionKind.Match(values, arms) -> this.Match scope expr values arms k
        | ContextMemberCall(contextType, memberName, args) ->
            // `C(T).f(args)`: the member of that instance of the context, which must be in scope.
            let key = contextMemberKey (Some contextType) memberName
            let ty = tyOf expr
            match scope.Members |> Map.tryFind key with
            | Some memberVar -> this.CallValue scope expr.Id memberVar args ty k
            | None -> cannot $"the context member {ContextMemberKey.text key}, which is not in scope"
        | ContextMemberAccess(contextType, memberName) ->
            // `C(T).m`: the member of that instance of the context as a value.
            let key = contextMemberKey (Some contextType) memberName
            match scope.Members |> Map.tryFind key with
            | Some memberVar -> k (Expr.make (Atom(AVar memberVar)) memberVar.Type)
            | None -> cannot $"the context member {ContextMemberKey.text key}, which is not in scope"
        // `x \f(a, b)` is the call `f(x, a, b)`, typed at the pipe's node.
        | Pipe(value, name, range, args) -> this.Complex scope { expr with Kind = FunctionCall(name, range, pipeCallArguments value args) } k
        // A hole the compiler writes itself holds its value, not a call to convert it.
        | InterpolatedString parts when parts |> List.exists (function StringExpr hole -> input.BuiltInHoles.Contains hole.Id | StringText _ -> false) ->
            let written =
                parts
                |> List.map (function
                    | StringExpr (Kind (FunctionCall(_, _, [ value ])) as hole) when input.BuiltInHoles.Contains hole.Id -> StringExpr value
                    | part -> part)
            this.Complex scope { expr with Kind = InterpolatedString written } k
        | InterpolatedString parts when parts |> List.exists (function StringExpr hole -> tyOf hole = IrError | StringText _ -> false) ->
            // A hole of no known type cannot be printed. The holes are evaluated in order up to it, for what they do (the
            // hole itself most likely stops the program, through the deferred error that left its type unknown), and if
            // the program is still running then, it stops there.
            let insideHole = { scope with InterpolationDepth = scope.InterpolationDepth + 1 }
            let holes = parts |> List.choose (function StringExpr hole -> Some hole | StringText _ -> None)
            let before = holes |> List.takeWhile (fun hole -> tyOf hole <> IrError)
            let unknown = holes |> List.find (fun hole -> tyOf hole = IrError)
            let stop = Expr.make (Panic [ Interpolation.unknownHoleMessage unknown.Span ]) IrString
            (before @ [ unknown ])
            |> List.rev
            |> List.fold (fun (rest: Expr) hole -> this.Complex insideHole hole (fun value -> Expr.make (Do(value, rest)) rest.Type)) stop
            |> k
        | InterpolatedString parts ->
            // Each hole is evaluated into its local, in order, and then the string is built from the text and them.
            // A string inside a hole is a level deeper, so that it keeps to its own hole locals.
            let depth = scope.InterpolationDepth
            let insideHole = { scope with InterpolationDepth = depth + 1 }
            let holes = parts |> List.choose (function StringExpr hole -> Some hole | StringText _ -> None)
            let rec evaluateHoles (index: int) (remaining: Expression list) (evaluated: (Var * HoleType) option list) =
                match remaining with
                | [] ->
                    let mutable next = List.rev evaluated
                    let irParts =
                        parts
                        |> List.map (function
                            | StringText text -> Text text
                            | StringExpr _ ->
                                let held = next.Head
                                next <- next.Tail
                                match held with
                                | Some (var, holeType) -> Hole(var, holeType)
                                | None -> Text "()")
                    k (Expr.make (Interpolate irParts) IrString)
                // The unit value is written `()`, as a derived `toString` writes it. It is still evaluated, for what it does:
                // the instance passed for the elements of `[]` writes one (see `unconstrainedAsUnit`).
                | hole :: rest when tyOf hole = IrUnit ->
                    let body = evaluateHoles (index + 1) rest (None :: evaluated)
                    this.Complex insideHole hole (fun value -> Expr.make (Do(value, body)) body.Type)
                | hole :: rest ->
                    let holeType = this.HoleType scope hole
                    // A float or an `F32` is formatted to a string as its hole is evaluated, since that allocates.
                    let local = newVar (Interpolation.holeLocalName depth index) (InterpolationHole(depth, index)) (if holeType = HoleFloat then IrString else tyOf hole)
                    let store (value: Expr) =
                        let body = evaluateHoles (index + 1) rest (Some (local, holeType) :: evaluated)
                        Expr.make (Let(local, value, body)) body.Type
                    if holeType = HoleFloat then
                        let format = if tyOf hole = IrF32 then Helpers.f32ToString else Helpers.floatToString
                        this.Atoms insideHole [ hole ] (fun atoms -> store (Expr.make (CallRuntime(format, atoms, true)) IrString))
                    else this.Complex insideHole hole store
            evaluateHoles 0 holes []
        | Lambda _ -> cannot "a lambda"
        | _ -> cannot "this kind of expression"

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
          BindingsAt = bindingsAt
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
                    match parameterTys with
                    | [] -> TyPrimitive "unit"
                    | [ single ] -> single
                    | several -> TyRecord(several |> List.mapi (fun index ty -> string index, ty) |> Map.ofList)
                toIrType (substitute subst (TyFunc(inputTy, resultTy, requiredContexts lambdaTy)))
            | None -> tyOf expr
        let arity = IrType.closureArity (parameterNames target) ty
        this.Atoms scope captured (fun atoms ->
            CoreIrLowerLocals.capturedInstances this.LocalTools scope target bindings captured atoms (fun atoms capturedLocals ->
                let capturedTys = atoms |> List.map atomType
                let name = request (SpecKey.plain target (self.TypeArguments @ typeArguments)) subst (LiftedCapturing(capturedTys, capturedLocals))
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

    /// How the hole `hole` of an interpolated string is printed, from its type. A tag's cases are in the order its type
    /// names them, which is the order they are tested in.
    member this.HoleType (scope: Scope) (hole: Expression) : HoleType =
        match this.TypeIn scope hole with
        | IrString -> HoleString
        | IrInt -> HoleInt
        | IrSized integer -> HoleSized integer
        | IrBool -> HoleBool
        | IrFloat | IrF32 -> HoleFloat
        | IrTags tags ->
            // In the order the hole's type names them, where the type checker gave the hole one.
            let rec cases (ty: Ty) =
                match Unifier.unalias ty with
                | TyTag(name, payload) -> [ name, payload |> Option.map toIrType ]
                | TyUnion items -> items |> List.collect cases
                | TyVar _ -> []
                | _ -> cannot "interpolating a union with tags not known here"
            let ordered =
                match input.ExprTypes |> Map.tryFind hole.Id |> Option.map (substitute subst) with
                | Some ty when (match Unifier.unalias ty with TyTag _ | TyUnion _ -> true | _ -> false) -> cases ty
                | _ -> tags
            ordered
            |> List.distinctBy fst
            |> List.map (fun (name, payloadTy) ->
                match payloadTy with
                | None
                | Some (IrString | IrInt | IrBool) -> name, payloadTy
                | Some other -> cannot $"interpolating #{name} with a payload of type {IrType.show other}")
            |> HoleTag
        | other -> cannot $"interpolating a value of type {IrType.show other}"

    /// Allocates a record of `fields` (name and value, in the order they are stored), of type `ty`.
    member this.Record (scope: Scope) (fields: (string * Expression) list) (ty: IrType) (k: Expr -> Expr) : Expr =
        this.Atoms scope (List.map snd fields) (fun atoms -> k (Expr.make (MakeRecord(List.zip (List.map fst fields) atoms)) ty))

    /// A call through the function value `closure`, whose result is of type `ty`. A function whose input is a tuple
    /// takes the tuple's elements (see `IrType.tupleElements`): a tuple passed whole has them read from it. A function
    /// whose input is unit takes nothing, so `f()` and `f(())` are the same call (see `ValueParameters`). The members
    /// of the contexts the value requires follow, as the call at `node` finds them (see `CoreIrLowerMembers.memberAtoms`). A known function a
    /// specialisation stands in for with `closure` is called directly (see `CoreIrLowerKnown`).
    member this.CallValue (scope: Scope) (node: NodeId) (closure: Var) (args: Expression list) (ty: IrType) (k: Expr -> Expr) : Expr =
        // The arguments of a context member whose type has values boxed are held in locals, since converting them stores
        // (see `CoreIrLowerBoxes`).
        let boxing = CoreIrLowerBoxes.holdsBox closure.Type
        let atomsOf = if boxing then this.AtomsInLocals else this.Atoms
        let bindElement = if boxing then bindLocal "__element" else bindTemp
        let members, parameterTys =
            match closure.Type with
            | IrFunction(input, _, members) ->
                members, (IrType.tupleElements input |> Option.map (List.map snd) |> Option.defaultValue [ input ])
            | _ -> [], []
        let call atoms =
            CoreIrLowerMembers.coerceAll this.MemberTools scope atoms (if parameterTys.Length = atoms.Length then parameterTys else []) (fun atoms ->
                match knownOf closure, closure.Type with
                | Some (KnownFunction(wrapper, wrapped, at)), IrFunction(input, _, _) ->
                    directArguments bindTemp (parameterNames wrapper).Tail.Length input atoms (fun atoms -> k (Expr.make (Call(callee wrapped at, atoms)) ty))
                | _ ->
                    CoreIrLowerMembers.memberAtoms this.MemberTools scope (Some node) members (fun memberAtoms ->
                        // A context member's result with values boxed is converted to the type the call has it at.
                        match closure.Type with
                        | IrFunction(_, output, _) when CoreIrLowerBoxes.needsConversion output ty ->
                            bindTemp (Expr.make (CallClosure(closure, atoms @ memberAtoms)) output) (fun result ->
                                CoreIrLowerMembers.coerce this.MemberTools scope result ty (fun atom -> k (Expr.make (Atom atom) ty)))
                        | _ -> k (Expr.make (CallClosure(closure, atoms @ memberAtoms)) ty)))
        let elements =
            match closure.Type with
            | IrFunction(input, _, _) -> IrType.tupleElements input
            | _ -> None
        match elements, SyntaxTree.flattenCallArgs args with
        | _, flatArgs when (match closure.Type with IrFunction(IrUnit, _, _) -> true | _ -> false) ->
            // What the argument does is done first; its value, the unit, isn't passed.
            let rec run (remaining: Expression list) =
                match remaining with
                | [] -> call []
                | { Kind = UnitExpr } :: rest -> run rest
                | arg :: rest -> this.Complex scope arg (fun value -> let after = run rest in Expr.make (Do(value, after)) after.Type)
            run flatArgs
        | Some elements, [ tuple ] ->
            atomsOf scope [ tuple ] (fun atoms ->
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

    /// Lowers `exprs` in order to atoms, binding each value that is not one to a temporary. An operand that stores to a
    /// local on the way to its value (see `storesBeforeValue`) is lowered whole, its stores with it, into one
    /// temporary: it is computed where it is used, so nothing computed before it waits on the stack across its stores.
    member this.Atoms (scope: Scope) (exprs: Expression list) (k: Atom list -> Expr) : Expr =
        let rec go (acc: Atom list) (remaining: Expression list) =
            match remaining with
            | [] -> k (List.rev acc)
            | expr :: rest when storesBeforeValue expr -> bindTemp (this.Tail scope expr) (fun atom -> go (atom :: acc) rest)
            | expr :: rest ->
                this.Complex scope expr (fun value ->
                    match value.Kind with
                    | Atom atom -> go (atom :: acc) rest
                    | _ -> bindTemp value (fun atom -> go (atom :: acc) rest))
        go [] exprs

    /// `Atoms`, but binding each value that is not an atom to a local, so that code that stores can come between them
    /// and where they are used.
    member this.AtomsInLocals (scope: Scope) (exprs: Expression list) (k: Atom list -> Expr) : Expr =
        let rec go (acc: Atom list) (remaining: Expression list) =
            match remaining with
            | [] -> k (List.rev acc)
            | expr :: rest -> this.Complex scope expr (fun value -> settle value (fun atom -> go (atom :: acc) rest))
        go [] exprs

    /// Lowers a block, its value passed to `k`, a statement at a time (see `CoreIrLowerHoles`).
    member this.Block (scope: Scope) (statements: StatementKind list) (k: Expr -> Expr) : Expr =
        CoreIrLowerHoles.lowerBlock (fun scope statements -> this.Statement scope statements k) scope statements

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
            let statement =
                this.Complex scope rhs (fun value ->
                    let local = newVar localName Local value.Type
                    this.NoteDefinedKnown local rhs value
                    let members =
                        match scope.CapturedMembers |> Map.tryFind name with
                        | Some keys -> keys |> List.fold (fun acc key -> acc |> Map.add key local) scope.Members
                        | None -> scope.Members
                    restScope.Value <- { scope with Vars = scope.Vars |> Map.add name local; Members = members }
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
        // A `use` of a scoped context's instance, `use m` for a `memory` block's `m`, loads no members: it only says
        // whose cells a `mut` after it makes, which the type checker has settled.
        | UseStatement value :: rest when
            (match input.ExprTypes |> Map.tryFind value.Id |> Option.map Unifier.unalias with
             | Some (TyNominal(_, FunctionContexts.ScopeType _, _)) -> true
             | _ -> false)
            ->
            hole, Some (hole, scope, rest)
        | UseStatement _ :: _ -> cannot "this kind of `use`"
        | _ -> cannot "this kind of statement"

    /// Records the local `local`, which a `def` gives the value `rhs`, lowered to `value`, as known when `rhs` is a
    /// top-level function that is not generic and requires no contexts, named as a value (`def bump = addThree`, or what
    /// lambda lifting makes of `use Counter(bump = addThree)`), an int or bool literal, or a local known already. Such a
    /// local holds that value for as long as it is in scope (a cell's `def` is `mut(...)`, which is none of these), so a
    /// call through it is a call to the function, and a `use` that binds it to a context member makes the member known.
    member private this.NoteDefinedKnown (local: Var) (rhs: Expression) (value: Expr) =
        match rhs.Kind, value.Kind with
        | LiteralExpr(IntLit _ | BoolLit _), Atom(AInt _ | ASized _ | ABool _ as constant) ->
            definedKnown.[local.Id] <- KnownConstant(constant, value.Type)
        | IdentifierExpr _, Atom(AVar var) -> knownOf var |> Option.iter (fun knownMember -> definedKnown.[local.Id] <- knownMember)
        | ClosureSyntax.Closure(target, []), _ ->
            match input.Lifted |> Map.tryFind target with
            | Some (FunctionWrapper wrapped) when
                not (input.ContextParameters.ContainsKey wrapped)
                && (contextVariables wrapped).IsEmpty
                && input.DefinitionTypes |> Map.tryFind wrapped |> Option.exists (freeVars >> List.isEmpty)
                ->
                let _, at = wrappedAt input target None
                definedKnown.[local.Id] <- KnownFunction(target, wrapped, at)
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
        CoreIrLowerMatch.lowerMatch this.MatchTools scope expr values arms k

    /// What lowering a `match` needs from this function's lowering (see `CoreIrLowerMatch`).
    member private this.MatchTools : CoreIrLowerMatch.MatchTools =
        { NewVar = newVar
          UniqueName = uniqueName
          TypeOf = tyOf
          NominalTypes = input.NominalTypes
          Tail = this.Tail
          Complex = this.Complex }

    /// The start function: each top-level expression in turn, its value dropped. The start function returns nothing,
    /// so nothing in it is a tail call; its body ends in unit, which the emitter leaves out (see `emitStart`).
    member this.StartFunction () : Function =
        let scope = emptyScope
        // An expression that holds errors deferred to run time, and cannot be lowered because of them, reports them
        // when it is reached instead.
        let lowerExpression (expr: Expression) =
            try this.Tail scope expr
            with CannotLower _ when not (deferredMessages expr).IsEmpty -> Expr.make (Panic(deferredMessages expr)) IrError
        let body =
            List.foldBack (fun (expr: Expression) (rest: Expr) -> Expr.make (Do(lowerExpression expr, rest)) IrUnit) input.TopLevel (Expr.make (Atom AUnit) IrUnit)
        { Name = startFunctionName
          Source = startFunctionName
          TypeArguments = []
          Params = []
          Result = IrUnit
          Body = body }

    /// The definition `name` as a function called `functionName` that reports `messages`, errors deferred to run time,
    /// as soon as it is called: what a definition that cannot be lowered because of them is instead. It takes the
    /// parameters the definition does, context members included, as values of no known type.
    member this.Trap (name: string) (functionName: string) (typeArguments: IrType list) (messages: string list) : Function =
        let parameterNames, _ = parametersAndBody definitions.[name]
        let contextParameters = input.ContextParameters |> Map.tryFind name |> Option.map fst |> Option.defaultValue []
        let parameters =
            (parameterNames |> List.map (fun parameterName -> newVar (uniqueName parameterName) Param IrError))
            @ (contextParameters
               |> List.filter (fun parameter -> not (self.Known |> List.exists (fst >> (=) parameter.Key)))
               |> List.map (fun parameter -> newVar parameter.Name Param IrError))
            @ (variableMembers name subst |> List.map (fun (key, _) -> newVar (contextMemberParameter key) Param IrError))
        { Name = functionName
          Source = name
          TypeArguments = typeArguments
          Params = parameters
          Result = IrError
          Body = Expr.make (Panic messages) IrError }

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
        selfName <- functionName
        match lowering with
        | AsStart -> this.StartFunction ()
        | Adapting(givenTy, expectedTy) -> CoreIrLowerLifted.adapter this.LiftedTools functionName givenTy expectedTy
        | BoxReading ty -> CoreIrLowerBoxes.readerFunction newVar functionName ty
        | StandingIn ty -> CoreIrLowerInstances.standInFunction newVar functionName ty
        | ListConverting(given, expected) -> CoreIrLowerBoxes.listConverterFunction (CoreIrLowerMembers.boxTools this.MemberTools emptyScope) newVar functionName given expected
        | Printing printer -> CoreIrLowerDbg.printerFunction printerTools functionName printer
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
              Body = body }
        match lowering with
        | AsDefined ->
            let defTy =
                match input.ExprTypes |> Map.tryFind expr.Id with
                | Some ty -> substitute subst ty
                | None -> cannot "a definition without a type"
            let parameterTys, resultTy = parameterTypes parameterNames defTy isLambda
            let parameters = List.map2 (fun parameterName ty -> parameterOf parameterName (toIrType ty)) parameterNames parameterTys
            // The members of the contexts it requires follow, each a closure in a parameter of its own, but those it is
            // specialised on, each in a local that has the member's value only if the body uses it as one.
            let contextParameters, aliases = input.ContextParameters |> Map.tryFind name |> Option.defaultValue ([], Map.empty)
            let members =
                contextParameters
                |> List.map (fun parameter ->
                    let knownMember = self.Known |> List.tryFind (fst >> (=) parameter.Key) |> Option.map snd
                    match parameter.Type with
                    | Some ty ->
                        let var = newVar parameter.Name (if knownMember.IsSome then Local else Param) (toIrType (substitute subst ty))
                        knownMember |> Option.iter (fun knownMember -> known.[var.Id] <- (var, knownMember))
                        parameter.Key, var
                    | None -> cannot $"the context member {ContextMemberKey.text parameter.Key}, whose type is not known")
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
            // A function value it results in is adapted to the type it declares (see `CoreIrLowerMembers.coerce`).
            let result = toIrType resultTy
            let adapted (value: Expr) =
                match value.Type with
                | IrFunction _ when value.Type <> result -> bindTemp value (fun atom -> CoreIrLowerMembers.coerce this.MemberTools scope atom result (fun atom -> Expr.make (Atom atom) result))
                | _ -> value
            let parameters = parameters @ (members |> List.map snd |> List.filter (fun var -> var.Kind = Param))
            make parameters result (this.WithKnownValues (this.Complex scope body adapted))
        | LiftedCapturing(capturedTys, capturedLocals) ->
            CoreIrLowerLifted.liftedCapturing this.LiftedTools name parameterNames body scopeOf make capturedTys capturedLocals
        | WrapperCalling(wrapped, instanceTy, captured) ->
            CoreIrLowerLifted.wrapperCalling this.LiftedTools wrapped instanceTy captured parameterNames make
        | Adapting _ -> internalError "an adapter lowered as a definition"
        | BoxReading _ -> internalError "a box's function lowered as a definition"
        | StandingIn _ -> internalError "a stand-in lowered as a definition"
        | ListConverting _ -> internalError "a list's conversion lowered as a definition"
        | Printing _ -> internalError "a printer lowered as a definition"
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
          LiftedParameterTypes = this.LiftedParameterTypes
          Callee = callee
          BindingsAt = bindingsAt
          VariableMembers = variableMembers
          SetSelfMembers = fun members -> selfMembers <- members
          Tail = this.Tail }
