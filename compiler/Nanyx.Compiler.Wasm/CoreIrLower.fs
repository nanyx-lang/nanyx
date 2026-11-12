/// Lowers the typed AST to the core IR (`CoreIr.fs`), specialising generic functions as it goes.
///
/// Each definition whose type is concrete is lowered as a root. A call to a generic definition asks for its
/// specialisation at the call's type arguments, which is lowered in turn (and may ask for more). A definition the IR
/// cannot lower yet is left to `CodeGen.fs`, which compiles it as it always has: see "Core IR" in
/// `docs/src/content/work-in-progress/memory-management.md` for this fallback.
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

/// A pattern the value it is matched against can never match.
exception private NeverMatches

/// A generic definition at some type arguments: what a specialisation is.
type internal SpecKey = string * IrType list

/// How a function is lowered.
type internal Lowering =
    /// As its definition says.
    | AsDefined
    /// As a lifted lambda whose closure captures values of these types, and these captured locals generic in type
    /// variables of their own (see `Scope.CapturedLocals`).
    | LiftedCapturing of IrType list * Map<string, Ty * (IrType list * int) list>
    /// As the wrapper of a top-level function, which it calls at this type, with the members of the contexts it
    /// requires: those of these keys and types, which the closure captures, and those its value's type requires, which
    /// each call passes.
    | WrapperCalling of string * Ty * (ContextMemberKey * IrType) list
    /// As a function value of the second type that calls one of the first, which its closure holds, with the members
    /// the first requires: each passed to it, or captured after the value where it was made (see `Coerce`).
    | Adapting of IrType * IrType
    /// As a function that writes values as text for `dbg` (see `CoreIrLowerDbg`).
    | Printing of CoreIrLowerDbg.Printer
    /// As the start function, from the module's top-level expressions.
    | AsStart


/// Lowers the closure of a local function generic in type variables of its own at some type arguments, given what they
/// bind, and passes it on.
type internal LocalClosure = Map<int, Ty> -> IrType list -> (Expr -> Expr) -> Expr

/// What names refer to in the expression being lowered.
type internal Scope =
    { Vars: Map<string, Var>
      /// How many scrutinees enclose the expression: the next `match` holds its own from this number on.
      MatchDepth: int
      /// The names of the pattern-bound locals of the arms that enclose the expression, which a nested arm must not
      /// reuse.
      LivePayloads: Set<string>
      /// How many interpolated strings' holes enclose the expression (see `Interpolation.holeLocalName`).
      InterpolationDepth: int
      /// The context members in scope, by key (and by the other names they can be called by): each a variable holding
      /// the member's closure.
      Members: Map<ContextMemberKey, Var>
      /// In a lifted lambda, the captured locals that hold context members (`__ctx_<key>`), each with the keys and other
      /// names the member is in scope by once the local is bound.
      CapturedMembers: Map<string, ContextMemberKey list>
      /// How many `for` loops enclose the expression, which names a loop's locals.
      ForDepth: int
      /// The closures of each local function generic in type variables of its own, by the variable its name is bound
      /// to: its type, a closure for each type arguments it is used at (see `Block`), and how to make it at others where
      /// that can be done.
      LocalInstances: Map<int, Ty * (IrType list * Var) list * LocalClosure option>
      /// In a lifted lambda, the captured locals that hold a local function generic in type variables of its own: its
      /// type, and the field of the environment that holds its closure at each type arguments it is used at here.
      CapturedLocals: Map<string, Ty * (IrType list * int) list> }

/// The arguments an adapter from a function value of type `givenTy` to one of `expectedTy` takes and passes on (see
/// `FunctionLowering.Coerce`): the elements of a tuple, none for unit, or else the one value. An expected input of unit
/// may be a type variable nothing constrained where the type was found (see `toIrType`), so the given input decides then.
let private adaptedValueTypes (givenTy: IrType) (expectedTy: IrType) : IrType list =
    let valuesOf (input: IrType) =
        match input, IrType.tupleElements input with
        | IrUnit, _ -> []
        | _, Some elements -> List.map snd elements
        | _, None -> [ input ]
    match givenTy, expectedTy with
    | IrFunction(given, _, _), IrFunction(IrUnit, _, _) -> valuesOf given
    | _, IrFunction(expected, _, _) -> valuesOf expected
    | _ -> []

/// Lowers each function, asking for the specialisations its calls need through `request`.
type internal FunctionLowering(input: LowerInput, definitions: Map<string, Expression>, subst: Map<int, Ty>, self: SpecKey, request: SpecKey -> Map<int, Ty> -> Lowering -> string) =
    // The type arguments of the specialisation, and the variables of the generic locals lowered at the one use they have
    // (see `Block`).
    let mutable subst = subst
    let mutable nextId = 0
    let usedNames = System.Collections.Generic.HashSet<string>()

    let newVar (name: string) (kind: VarKind) (ty: IrType) =
        nextId <- nextId + 1
        { Id = nextId; Name = name; Kind = kind; Type = ty }

    /// A parameter's or local's name: as written, made unique within the function.
    let uniqueName (name: string) =
        let baseName = sanitizeIdentifier name
        let mutable candidate = baseName
        let mutable index = 1
        while usedNames.Contains candidate do
            candidate <- $"{baseName}_{index}"
            index <- index + 1
        usedNames.Add candidate |> ignore
        candidate

    let newTemp (ty: IrType) =
        let var = newVar "" Temp ty
        { var with Name = $"__t{var.Id}" }

    let toIrType = toIrTypeIn input.ContextLayouts

    /// What a `dbg` printer needs from this function's lowering (see `CoreIrLowerDbg`).
    let printerTools: CoreIrLowerDbg.PrinterTools =
        { NewVar = fun name kind ty -> newVar (uniqueName name) kind ty
          NewTemp = newTemp
          PrinterOf = fun printer -> request (CoreIrLowerDbg.key printer, []) Map.empty (Printing printer)
          ToIrType = toIrType }

    /// `ty` with each type variable it still has made unit: nothing constrains one where a value of `ty` is made, as in
    /// the instance passed for the elements of `[]`, so no value of it is ever met (see `toIrType`).
    let unconstrainedAsUnit (ty: Ty) =
        substitute (freeVars ty |> List.map (fun id -> id, TyPrimitive "unit") |> Map.ofList) ty

    /// The members of the contexts without parameters in `contexts`, in the order a function value takes them.
    let membersOf (contexts: Ty list) =
        match toIrType (TyFunc(TyPrimitive "unit", TyPrimitive "unit", contexts)) with
        | IrFunction(_, _, members) -> members
        | _ -> []

    /// The context variables of the definition `name`'s type (`[c]` in `[c] ([c] a -> b), a -> b`), by id.
    let contextVariables (name: string) =
        input.DefinitionTypes |> Map.tryFind name |> Option.map (FunctionContexts.variablesIn >> Map.keys >> List.ofSeq) |> Option.defaultValue []

    /// The members of the contexts the context variables of the definition `name` stand for where `bindings` binds them,
    /// which the definition takes after those of the contexts it declares, as each call passes them.
    let variableMembers (name: string) (bindings: Map<int, Ty>) =
        let declared =
            input.ContextParameters |> Map.tryFind name |> Option.map (fst >> List.map _.Key) |> Option.defaultValue [] |> Set.ofList
        contextVariables name
        |> List.collect (fun id -> bindings |> Map.tryFind id |> Option.map List.singleton |> Option.defaultValue [])
        |> membersOf
        |> List.filter (fst >> declared.Contains >> not)

    /// The type each context without parameters declares for its member of each key, where only one declares a member
    /// of that key.
    let declaredMemberTypes =
        input.ContextLayouts
        |> Map.values
        |> Seq.concat
        |> Seq.groupBy fst
        |> Seq.choose (fun (key, declared) ->
            match declared |> Seq.map snd |> Seq.distinct |> List.ofSeq with
            | [ memberTy ] -> Some (key, memberTy)
            | _ -> None)
        |> Map.ofSeq

    /// The contexts a function of type `fnTy` requires where it is called.
    let requiredContexts (fnTy: Ty) =
        match Unifier.unalias fnTy with
        | TyFunc(_, _, contexts) -> contexts
        | _ -> []

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

    /// The name to call `name` by at the instance type `instanceTy`: its own, or a specialisation's. A definition with
    /// context variables is specialised on the members of the contexts they stand for, unless they stand for none.
    let callee (name: string) (instanceTy: Ty) =
        let scheme =
            match input.DefinitionTypes |> Map.tryFind name with
            | Some scheme -> scheme
            | None -> cannot $"a call to '{name}', whose type is not known"
        let typeVars = freeVars scheme |> List.distinct |> List.sort
        let contextVars = contextVariables name
        if typeVars.IsEmpty && contextVars.IsEmpty then sanitizeIdentifier name
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
            if typeArguments.IsEmpty then sanitizeIdentifier name
            else request (name, typeArguments) bindings AsDefined

    let mk kind ty : Expr = { Kind = kind; Type = ty }

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
        mk (Let(temp, value, body)) body.Type

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
        | UnitExpr -> k (mk (Atom AUnit) IrUnit)
        | LiteralExpr(IntLit value) -> k (mk (Atom(AInt(int64 value))) IrInt)
        | LiteralExpr(BoolLit value) -> k (mk (Atom(ABool value)) IrBool)
        | LiteralExpr(StringLit value) -> k (mk (Atom(AString value)) IrString)
        | LiteralExpr(FloatLit value) -> k (mk (BoxFloat value) IrFloat)
        | IdentifierExpr name ->
            match scope.Vars |> Map.tryFind name with
            | Some var ->
                let var = this.LocalAt scope var (input.ExprTypes |> Map.tryFind expr.Id)
                k (mk (Atom(AVar var)) var.Type)
            // A context member in scope, named as a value (`` `0` ``, or a function member passed on).
            | None when scope.Members.ContainsKey (NamedMember name) ->
                let memberVar = scope.Members.[NamedMember name]
                k (mk (Atom(AVar memberVar)) memberVar.Type)
            | None when definitions.ContainsKey name && not (isFunction name) ->
                // A top-level value is compiled to a function of no parameters: evaluate it.
                let ty = tyOf expr
                k (mk (Call(callee name input.ExprTypes.[expr.Id], [])) ty)
            | None when definitions.ContainsKey name -> cannot $"the function '{name}' used as a value"
            | None -> cannot $"a reference to '{name}'"
        | TagExpr(name, None) -> k (mk (Atom(ATag name)) (tyOf expr))
        | TagExpr(name, Some payload) ->
            let ty = tyOf expr
            this.Atoms scope [ payload ] (fun atoms -> k (mk (MakeTag(name, atoms.Head)) ty))
        | BinaryOp("++", _, Kind (ListExpr items), rest) when not items.IsEmpty ->
            // `[a, b] ++ rest`, which is what `[a, b, ...rest]` is: a cell for each element, put in front of `rest`
            // rather than copied from a list of their own. The elements are evaluated first, then `rest`, as `++`
            // evaluates them.
            let ty = tyOf expr
            this.Atoms scope (items @ [ rest ]) (fun atoms ->
                let tail = List.last atoms
                let cons head tail = mk (CallRuntime(Helpers.listCons, [ head; tail ], true)) ty
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
            this.Atoms scope [ left; right ] (fun atoms -> k (mk (CallRuntime(helper, atoms, true)) ty))
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
                let equal = mk (CallRuntime(helper, atoms, true)) IrBool
                if op = "==" then k equal
                else bindTemp equal (fun result -> k (mk (Prim(Not, [ result ])) IrBool)))
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
                let order = mk (CallRuntime(Helpers.valueCompare, atoms, true)) IrInt
                bindTemp order (fun result -> k (mk (Prim(Binary op, [ result; AInt 0L ])) IrBool)))
        | BinaryOp(op, _, left, right) when tyOf left = IrFloat || tyOf right = IrFloat ->
            // Floats are boxed: the operands are unboxed, and an arithmetic result boxed.
            // `%` has no instruction: it calls a runtime helper (see `CoreIrEmit`).
            if op <> "%" && (wasmFloatInstruction op).IsNone then cannot $"the operator {op} on floats"
            let ty = tyOf expr
            this.Atoms scope [ left; right ] (fun atoms -> k (mk (Prim(FloatBinary op, atoms)) ty))
        | BinaryOp(op, _, left, right) ->
            if (wasmBinaryInstruction op).IsNone then cannot $"the operator {op}"
            for operand in [ left; right ] do
                match tyOf operand with
                | IrInt | IrSized _ | IrBool | IrUnit | IrError -> ()
                | other -> cannot $"the operator {op} on a value of type {IrType.show other}"
            let ty = tyOf expr
            this.Atoms scope [ left; right ] (fun atoms -> k (mk (Prim(Binary op, atoms)) ty))
        | FunctionCall("not", _, [ operand ]) when not (scope.Vars.ContainsKey "not") && not (definitions.ContainsKey "not") ->
            this.Atoms scope [ operand ] (fun atoms -> k (mk (Prim(Not, atoms)) IrBool))
        | FunctionCall(name, _, [ operand ]) when name = NegateSyntax.builtIn ->
            // `-x` on a built-in number (on any other type it calls a `negate`). An integer is `0 - x`, and a float
            // `-0.0 - x`, which is IEEE 754's negation: `-0.0 - 0.0` is `-0.0`, and `-0.0 - -0.0` is `0.0`.
            let ty = tyOf expr
            this.Atoms scope [ operand ] (fun atoms ->
                match ty with
                | IrFloat -> bindTemp (mk (BoxFloat -0.0) IrFloat) (fun zero -> k (mk (Prim(FloatBinary "-", zero :: atoms)) ty))
                | IrInt -> k (mk (Prim(Binary "-", AInt 0L :: atoms)) ty)
                | IrSized integer -> k (mk (Prim(Binary "-", ASized(integer, 0L) :: atoms)) ty)
                | other -> cannot $"the operator - on a value of type {IrType.show other}")
        | ForSyntax.Call(iterables, _, parameters, body) when parameters.Length = iterables.Length ->
            // `for xs, ys | arms`: the body runs for each step, with each iterable's element in a local the body
            // names by its parameter. A range counts from its start (to its end, if it has one); a list walks its cells.
            let depth = scope.ForDepth
            // The first stream's locals keep the names a loop over one iterable has always had.
            let suffix index = if index = 0 then "" else $"_{index}"
            // Each iterable's operands, and how to make its source and element type from their atoms.
            let source (iterable: Expression) : Expression list * (Atom list -> ForSource * IrType) =
                match iterable.Kind with
                | RangeSyntax.Call(first, Some last) -> [ first; last ], fun atoms -> OverRange(atoms.[0], atoms.[1]), IrInt
                | RangeSyntax.Call(first, None) -> [ first ], fun atoms -> OverRangeFrom atoms.Head, IrInt
                | _ ->
                    match tyOf iterable with
                    | IrList elementTy -> [ iterable ], fun atoms -> OverList atoms.Head, elementTy
                    | other -> cannot $"a `for` loop over a value of type {IrType.show other}"
            let walkers =
                iterables |> List.mapi (fun index _ ->
                    newVar $"__for_cursor_{depth}{suffix index}" ForLoop IrWord, newVar $"__for_end_{depth}{suffix index}" ForLoop IrInt)
            let sources = iterables |> List.map source
            this.Atoms scope (sources |> List.collect fst) (fun atoms ->
                let streams =
                    List.zip3 sources parameters walkers
                    |> List.mapFold (fun (remaining: Atom list) ((operands, make), (parameter, _), (cursor, finish)) ->
                        let source, elementTy = make (List.take operands.Length remaining)
                        let stream =
                            { Source = source
                              Cursor = cursor
                              End = finish
                              Element = newVar $"__for_{sanitizeIdentifier parameter}_{depth}" ForLoop elementTy }
                        (parameter, stream), List.skip operands.Length remaining) atoms
                    |> fst
                let vars = streams |> List.fold (fun vars (parameter, stream) -> vars |> Map.add parameter stream.Element) scope.Vars
                let bodyScope = { scope with Vars = vars; ForDepth = depth + 1 }
                let forLoop: ForLoop = { Streams = List.map snd streams; Depth = depth; Body = this.Tail bodyScope body }
                k (mk (ForEach forLoop) IrUnit))
        // A cell (see `CellSyntax`) no lambda captures is the local its def binds, and one a lambda captures is a box of
        // one field. A read is a copy of what the cell holds then, so that a later `:=` leaves the copy as it is.
        | MutSyntax.Call value -> this.Complex scope value k
        | FunctionCall(form, _, [ Kind (IdentifierExpr name) ]) when (form = CellSyntax.read || form = CellSyntax.unbox) && scope.Vars.ContainsKey name ->
            let cell = scope.Vars.[name]
            let value =
                if form = CellSyntax.read then mk (Atom(AVar cell)) cell.Type
                else
                    match cell.Type with
                    | IrRecord [ _, held ] -> mk (Field(AVar cell, 0)) held
                    | other -> internalError $"a box of type {IrType.show other}"
            let copy = newVar (uniqueName name) Local value.Type
            let body = k (mk (Atom(AVar copy)) value.Type)
            mk (Let(copy, value, body)) body.Type
        | AssignSyntax.Call(Kind (IdentifierExpr name), value) when scope.Vars.ContainsKey name ->
            this.Atoms scope [ value ] (fun atoms -> k (mk (Assign(scope.Vars.[name], atoms.Head)) IrUnit))
        | FunctionCall(form, _, [ value ]) when form = CellSyntax.box ->
            this.Atoms scope [ value ] (fun atoms -> k (mk (MakeRecord [ "0", atoms.Head ]) (IrRecord [ "0", atomType atoms.Head ])))
        | FunctionCall(form, _, [ Kind (IdentifierExpr name); value ]) when form = CellSyntax.setBox && scope.Vars.ContainsKey name ->
            this.Atoms scope [ value ] (fun atoms -> k (mk (SetField(scope.Vars.[name], 0, atoms.Head)) IrUnit))
        | FunctionCall("__panic", _, [ Kind (LiteralExpr(StringLit message)) ]) ->
            // One the type checker added (the last arm of a match that does not handle every case) has no type.
            let ty = if input.ExprTypes.ContainsKey expr.Id then tyOf expr else IrError
            k (mk (Panic [ message ]) ty)
        | FunctionCall("dbg", _, args) when not (scope.Vars.ContainsKey "dbg") && not (definitions.ContainsKey "dbg") ->
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
                let rest = k (mk (Atom(AVar held)) held.Type)
                let printed = mk (Let(text, mk (Call(printer, [ AVar held ])) IrString, mk (Dbg(AVar text)) IrUnit)) IrUnit
                mk (Let(held, lowered, mk (Do(printed, rest)) rest.Type)) rest.Type)
        | FunctionCall("__with", _, [ record; Kind (RecordExpr updates) ]) ->
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
                k (mk (UpdateRecord(atoms.Head, updated)) ty))
        // The hole function called outside a string, at a built-in type: the value as a hole would write it.
        | FunctionCall(_, _, [ value ]) when input.BuiltInHoles.Contains expr.Id ->
            this.Complex scope { expr with Kind = InterpolatedString [ StringExpr value ] } k
        | FunctionCall(name, _, args) when input.NominalWrappers.Contains name && not (scope.Vars.ContainsKey name) ->
            // `$Money(pence)`: a nominal value is its shape's value.
            match flattenCallArgs args with
            | [ single ] -> this.Complex scope single k
            | several -> this.Record scope (several |> List.mapi (fun index value -> string index, value)) (tyOf expr) k
        | FunctionCall(name, _, args) when input.RecordConstructors.Contains name && not (scope.Vars.ContainsKey name) ->
            match flattenCallArgs args with
            | [ Kind (RecordExpr fields) ] -> this.Record scope (recordExprFields fields |> sortFields) (tyOf expr) k
            | _ -> cannot $"the type constructor {name} without a record of its fields"
        | FunctionCall(name, _, args) when scope.Members.ContainsKey (NamedMember name) ->
            // A context member in scope: a function value like any other.
            let ty = tyOf expr
            this.CallValue scope scope.Members.[NamedMember name] args ty k
        | FunctionCall(name, _, [ value ]) when Intrinsics.reinterpretations.Contains name && not (scope.Vars.ContainsKey name) ->
            // A map and the list of its entries are held the same way: the value is its argument.
            this.Complex scope value k
        | FunctionCall(name, _, args) when Intrinsics.isIntrinsic name && not (scope.Vars.ContainsKey name) ->
            // A compiler intrinsic (only the standard library can call these, and a built-in property such as
            // `s.length` is one): the runtime helper of the same name.
            let ty = tyOf expr
            this.Atoms scope (flattenCallArgs args) (fun atoms ->
                k (mk (CallRuntime(name, atoms, not (Intrinsics.returnsUnit name))) ty))
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
                let key = target, (if specialised then [ toIrType instanceTy ] else [])
                this.ContextMembers scope wrapped expr.Id instanceTy passed (fun captured ->
                    let name = request key subst (WrapperCalling(wrapped, instanceTy, captured |> List.map (fun (memberKey, atom) -> memberKey, atomType atom)))
                    k (mk (MakeClosure(name, arity, List.map snd captured)) ty))
            | None -> cannot $"a closure over '{target}', which lambda lifting did not make"
        | FunctionCall(name, _, args) when scope.Vars.ContainsKey name ->
            // A call through a function value in a parameter or local.
            let ty = tyOf expr
            match this.LocalAt scope scope.Vars.[name] (input.CalleeTypes |> Map.tryFind expr.Id) with
            | { Type = IrFunction _ } as var -> this.CallValue scope var args ty k
            | var -> cannot $"a call to '{name}', a value of type {IrType.show var.Type}"
        | FunctionCall(name, _, args) when definitions.ContainsKey name && not (isFunction name) && not (flattenCallArgs args).IsEmpty ->
            // A call through a function value a top-level value holds. The value is evaluated once, into a local, which
            // the call reads twice: as the environment, and for the table slot.
            let ty = tyOf expr
            let instanceTy =
                match input.CalleeTypes |> Map.tryFind expr.Id with
                | Some ty -> ty
                | None -> cannot $"a call to '{name}' whose type is not known"
            let value = mk (Call(callee name instanceTy, [])) (toIrType instanceTy)
            let closure = newVar (uniqueName name) Local value.Type
            let body = this.CallValue scope closure args ty k
            mk (Let(closure, value, body)) body.Type
        | FunctionCall(name, _, args) when input.Lifted.ContainsKey name ->
            // A `rec` local function calling itself, directly, with its environment passed on.
            if name <> fst self then cannot $"a call to the lifted function '{name}' from another function"
            let ty = tyOf expr
            this.Atoms scope (flattenCallArgs args) (fun atoms ->
                this.MemberAtoms scope selfMembers (fun members -> k (mk (Call(selfName, atoms @ members)) ty)))
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
                        flattenCallArgs args
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
            let target = callee name instanceTy
            let ty = tyOf expr
            let parameters = parameterNames name
            // A function value given for a parameter whose type requires other contexts is adapted to it (see `Coerce`).
            let parameterTys =
                try this.ParameterTypes parameters (substitute subst instanceTy) true |> fst |> List.map toIrType
                with CannotLower _ -> []
            // The context members the callee requires follow its arguments.
            let call atoms =
                this.CoerceAll scope atoms (if parameterTys.Length = atoms.Length then parameterTys else []) (fun atoms ->
                    this.ContextMembers scope name expr.Id instanceTy Set.empty (fun members ->
                        k (mk (Call(target, atoms @ List.map snd members)) ty)))
            // The arguments as the callee takes them, in the shapes the old path accepts.
            match flattenCallArgs args with
            | [ Kind (IdentifierExpr record) ] when parameters.Length > 1 && scope.Vars.ContainsKey record ->
                // A record passed to a function of several parameters: each parameter takes a field.
                let var = scope.Vars.[record]
                match var.Type with
                | IrRecord fields when fields.Length = parameters.Length ->
                    let rec loadFields (slot: int) (acc: Atom list) =
                        if slot = fields.Length then call (List.rev acc)
                        else bindTemp (mk (Field(AVar var, slot)) (snd fields.[slot])) (fun atom -> loadFields (slot + 1) (atom :: acc))
                    loadFields 0 []
                | _ -> cannot $"a call to '{name}' with one argument for its {parameters.Length} parameters"
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
            // Each element that is a function value is adapted to the list's element type (see `Coerce`).
            let ty = tyOf expr
            let elementTys =
                match ty with
                | IrList elementTy -> items |> List.map (fun _ -> elementTy)
                | _ -> []
            this.Atoms scope items (fun atoms -> this.CoerceAll scope atoms elementTys (fun atoms -> k (mk (MakeList atoms) ty)))
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
            this.Atoms scope [ record ] (fun atoms -> k (mk (Field(atoms.Head, slot)) ty))
        | IfExpr(cond, ifTrue, ifFalse) ->
            let ty = tyOf expr
            this.Atoms scope [ cond ] (fun atoms ->
                k (mk (If(atoms.Head, this.Tail scope ifTrue, this.Tail scope ifFalse)) ty))
        | Block statements -> this.Block scope (statements |> List.map _.Kind) k
        | ExpressionKind.Match(values, arms) -> this.Match scope expr values arms k
        | ContextMemberCall(contextType, memberName, args) ->
            // `C(T).f(args)`: the member of that instance of the context, which must be in scope.
            let key = contextMemberKey (Some contextType) memberName
            let ty = tyOf expr
            match scope.Members |> Map.tryFind key with
            | Some memberVar -> this.CallValue scope memberVar args ty k
            | None -> cannot $"the context member {ContextMemberKey.text key}, which is not in scope"
        | ContextMemberAccess(contextType, memberName) ->
            // `C(T).m`: the member of that instance of the context as a value.
            let key = contextMemberKey (Some contextType) memberName
            match scope.Members |> Map.tryFind key with
            | Some memberVar -> k (mk (Atom(AVar memberVar)) memberVar.Type)
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
            let stop = mk (Panic [ Interpolation.unknownHoleMessage unknown.Span ]) IrString
            (before @ [ unknown ])
            |> List.rev
            |> List.fold (fun (rest: Expr) hole -> this.Complex insideHole hole (fun value -> mk (Do(value, rest)) rest.Type)) stop
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
                    k (mk (Interpolate irParts) IrString)
                // The unit value is written `()`, as a derived `toString` writes it. It is still evaluated, for what it does:
                // the instance passed for the elements of `[]` writes one (see `unconstrainedAsUnit`).
                | hole :: rest when tyOf hole = IrUnit ->
                    let body = evaluateHoles (index + 1) rest (None :: evaluated)
                    this.Complex insideHole hole (fun value -> mk (Do(value, body)) body.Type)
                | hole :: rest ->
                    let holeType = this.HoleType scope hole
                    // A float is formatted to a string as its hole is evaluated, since that allocates.
                    let local = newVar (Interpolation.holeLocalName depth index) (InterpolationHole(depth, index)) (if holeType = HoleFloat then IrString else tyOf hole)
                    let store (value: Expr) =
                        let body = evaluateHoles (index + 1) rest (Some (local, holeType) :: evaluated)
                        mk (Let(local, value, body)) body.Type
                    if holeType = HoleFloat then
                        this.Atoms insideHole [ hole ] (fun atoms -> store (mk (CallRuntime(Helpers.floatToString, atoms, true)) IrString))
                    else this.Complex insideHole hole store
            evaluateHoles 0 holes []
        | Lambda _ -> cannot "a lambda"
        | _ -> cannot "this kind of expression"

    /// The members of the contexts `name` requires, where it is called, or named as a value, at the node `node` and the
    /// type `instanceTy`, each with its key: those of the contexts it declares, each found where the type checker found
    /// it (see `LowerInput.ContextArguments`), then those of the contexts its context variables stand for, found in
    /// scope. They are passed on to `k` in the order the function takes them, but for those of the keys in `passed`,
    /// which the callers of the function's value pass instead.
    member private this.ContextMembers (scope: Scope) (name: string) (node: NodeId) (instanceTy: Ty) (passed: Set<ContextMemberKey>) (k: (ContextMemberKey * Atom) list -> Expr) : Expr =
        let contextParameters = input.ContextParameters |> Map.tryFind name |> Option.map fst |> Option.defaultValue []
        let bindings = bindingsAt name instanceTy
        // The type of each member here, where the function's type gives it.
        let memberTys =
            contextParameters |> List.map (fun parameter -> parameter.Type |> Option.map (substitute bindings >> substitute subst))
        let variables = variableMembers name bindings |> List.filter (fst >> passed.Contains >> not)
        match input.ContextArguments name node (scope.Members |> Map.keys |> Set.ofSeq) passed with
        | Error reason -> cannot reason
        | Ok arguments ->
            let rec pass (remaining: (ContextMemberKey * ContextArgument * Ty option) list) (acc: (ContextMemberKey * Atom) list) =
                let next key (atom: Atom) (memberTy: Ty option) rest =
                    match memberTy with
                    | Some ty -> this.Coerce scope atom (toIrType ty) (fun atom -> pass rest ((key, atom) :: acc))
                    | None -> pass rest ((key, atom) :: acc)
                match remaining with
                | [] -> this.MemberAtoms scope variables (fun atoms -> k (List.rev acc @ List.zip (List.map fst variables) atoms))
                | (_, PassedByCaller, _) :: rest -> pass rest acc
                | (key, MemberInScope inScope, memberTy) :: rest -> next key (AVar scope.Members.[inScope]) memberTy rest
                | (key, ClosureOverWrapper wrapper, memberTy) :: rest ->
                    let closure = this.WrapperClosure wrapper memberTy
                    bindTemp closure (fun atom -> next key atom memberTy rest)
                | (key, DerivedClosure(wrapper, at), memberTy) :: rest ->
                    this.DerivedClosure scope wrapper at memberTy (fun atom -> next key atom memberTy rest)
                | (key, DefinitionValue definition, _) :: rest ->
                    // A member that is a value (`Point.`0``): the top-level value, evaluated.
                    let definitionTy =
                        match input.DefinitionTypes |> Map.tryFind definition with
                        | Some ty when (freeVars ty).IsEmpty -> ty
                        | _ -> cannot $"passing the generic value '{definition}' as a context member"
                    let value = mk (Call(callee definition definitionTy, [])) (toIrType definitionTy)
                    bindTemp value (fun atom -> pass rest ((key, atom) :: acc))
            if arguments.Length <> memberTys.Length then
                internalError $"{arguments.Length} context members found for the {memberTys.Length} '{name}' requires"
            pass (List.zip3 (contextParameters |> List.map _.Key) arguments memberTys) []

    /// The member of each of `members` (a key and the type the member is wanted at), as the scope has it, or else the
    /// ambient instance's, each adapted to its type (see `Coerce`).
    member this.MemberAtoms (scope: Scope) (members: (ContextMemberKey * IrType) list) (k: Atom list -> Expr) : Expr =
        let rec go (remaining: (ContextMemberKey * IrType) list) (acc: Atom list) =
            match remaining with
            | [] -> k (List.rev acc)
            | (key, ty) :: rest ->
                let found (atom: Atom) = this.Coerce scope atom ty (fun atom -> go rest (atom :: acc))
                match scope.Members |> Map.tryFind key, input.AmbientMembers |> Map.tryFind key with
                | Some var, _ -> found (AVar var)
                | None, Some wrapper -> bindTemp (this.WrapperClosure wrapper None) found
                | None, None -> cannot $"the context member {ContextMemberKey.text key}, which is not in scope"
        go members []

    /// `atoms`, each adapted to the type in `tys` in its place (see `Coerce`); as they are when `tys` is empty.
    member this.CoerceAll (scope: Scope) (atoms: Atom list) (tys: IrType list) (k: Atom list -> Expr) : Expr =
        let rec go (remaining: (Atom * IrType) list) (acc: Atom list) =
            match remaining with
            | [] -> k (List.rev acc)
            | (atom, ty) :: rest -> this.Coerce scope atom ty (fun atom -> go rest (atom :: acc))
        if tys.IsEmpty then k atoms else go (List.zip atoms tys) []

    /// `atom`, a function value, as one of type `expected` where it requires other contexts' members where it is called:
    /// a closure over an adapter that holds it and calls it with the members it requires, those `expected` requires
    /// passed on, and the others captured here. A value whose type requires them all is left as it is.
    member this.Coerce (scope: Scope) (atom: Atom) (expected: IrType) (k: Atom -> Expr) : Expr =
        match atomType atom, expected with
        | IrFunction(_, _, given) as givenTy, IrFunction(_, _, wanted) when given <> wanted ->
            let missing = given |> List.filter (fun (key, _) -> not (wanted |> List.exists (fst >> (=) key)))
            this.MemberAtoms scope missing (fun captured ->
                let name = request ($"__adapt_{IrType.mangle givenTy}_to_{IrType.mangle expected}", []) subst (Adapting(givenTy, expected))
                let arity = 1 + (adaptedValueTypes givenTy expected).Length + wanted.Length
                bindTemp (mk (MakeClosure(name, arity, atom :: captured)) expected) k)
        | _ -> k atom

    /// A closure over the wrapper `wrapper` of a top-level function: at the function's own type, or, for a generic
    /// function, at `memberTy`, the type of the context member it is passed as. A union's rest that nothing closes adds
    /// no tags (see `toIrType`): `ToString(#red | r)` is passed at `#red`.
    member this.WrapperClosure (wrapper: string) (memberTy: Ty option) : Expr =
        let wrapped =
            match input.Lifted |> Map.tryFind wrapper with
            | Some (FunctionWrapper wrapped) -> wrapped
            | _ -> internalError $"'{wrapper}' is not a function's wrapper"
        // A member that requires contexts of its own would need them found where the instance is.
        if input.ContextParameters.ContainsKey wrapped then cannot $"a context member '{wrapped}', which requires contexts"
        let rec withoutOpenRests (ty: Ty) =
            match ty with
            | TyUnion items -> TyUnion(items |> List.filter (function TyVar _ -> false | _ -> true) |> List.map withoutOpenRests)
            | TyFunc(input, output, _) -> TyFunc(withoutOpenRests input, withoutOpenRests output, [])
            | TyRecord fields -> TyRecord(fields |> Map.map (fun _ -> withoutOpenRests))
            | TyTag(name, payload) -> TyTag(name, payload |> Option.map withoutOpenRests)
            | TyApply(name, args) -> TyApply(name, args |> List.map withoutOpenRests)
            | TyNominal(nominal, underlying, isPrivate) -> TyNominal(nominal, withoutOpenRests underlying, isPrivate)
            | TyAlias(name, args, underlying) -> TyAlias(name, args |> List.map withoutOpenRests, withoutOpenRests underlying)
            | TyVar _
            | TyPrimitive _
            | TyOpenRecord _ -> ty
        let key, wrappedTy =
            match input.DefinitionTypes |> Map.tryFind wrapped, memberTy |> Option.map (withoutOpenRests >> unconstrainedAsUnit) with
            | Some ty, _ when (freeVars ty).IsEmpty -> (wrapper, []), ty
            | Some _, Some ty -> (wrapper, [ toIrType ty ]), ty
            | _ -> cannot $"passing the generic function '{wrapped}' as a context member"
        let name = request key subst (WrapperCalling(wrapped, wrappedTy, []))
        mk (MakeClosure(name, IrType.closureArity (parameterNames wrapper) (toIrType wrappedTy), [])) (toIrType wrappedTy)

    /// A closure over the wrapper `wrapper` of a definition derived from the parts of a type (see `DerivedClosure`), at
    /// `memberTy`, the type of the context member it is passed as: it captures the members of the contexts the
    /// definition requires, found as for a call to it at the node `at`.
    member private this.DerivedClosure (scope: Scope) (wrapper: string) (at: NodeId) (memberTy: Ty option) (k: Atom -> Expr) : Expr =
        let wrapped =
            match input.Lifted |> Map.tryFind wrapper with
            | Some (FunctionWrapper wrapped) -> wrapped
            | _ -> internalError $"'{wrapper}' is not a function's wrapper"
        // A member whose type the context's annotation does not give (`ToString(#x(a) | r)`) is at the definition's own.
        let memberTy =
            match memberTy, input.DefinitionTypes |> Map.tryFind wrapped with
            | Some memberTy, _ -> unconstrainedAsUnit memberTy
            | None, Some (TyFunc(input, output, _)) -> unconstrainedAsUnit (TyFunc(input, output, []))
            | None, _ -> internalError $"the derived definition '{wrapped}' has no function type"
        let ty = toIrType memberTy
        this.ContextMembers scope wrapped at memberTy Set.empty (fun captured ->
            let name = request (wrapper, [ ty ]) subst (WrapperCalling(wrapped, memberTy, captured |> List.map (fun (key, atom) -> key, atomType atom)))
            bindTemp (mk (MakeClosure(name, IrType.closureArity (parameterNames wrapper) ty, List.map snd captured)) ty) k)

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
            this.CapturedInstances scope target bindings captured atoms (fun atoms capturedLocals ->
                let capturedTys = atoms |> List.map atomType
                let name = request (target, snd self @ typeArguments) subst (LiftedCapturing(capturedTys, capturedLocals))
                k (mk (MakeClosure(name, arity, atoms)) ty)))

    /// `atoms`, what a closure over the lifted lambda `target` captures (`captured`, lowered), with each local function
    /// generic in variables of its own among them captured at each type arguments the lambda uses it at, where
    /// `bindings` binds the variables of the closure's type: the first in its own field, the others after everything
    /// else, as the lambda's `Scope.CapturedLocals` says.
    member private this.CapturedInstances (scope: Scope) (target: string) (bindings: Map<int, Ty>) (captured: Expression list) (atoms: Atom list) (k: Atom list -> Map<string, Ty * (IrType list * int) list> -> Expr) : Expr =
        let generic =
            List.zip captured atoms
            |> List.indexed
            |> List.choose (fun (index, (capture, atom)) ->
                match capture.Kind, atom with
                | IdentifierExpr name, AVar var when scope.LocalInstances.ContainsKey var.Id ->
                    let localTy, _, _ = scope.LocalInstances.[var.Id]
                    match this.LocalUses bindings localTy name definitions.[target] with
                    | [] -> None
                    | used -> Some (index, name, var, localTy, used)
                | _ -> None)
        let rec instancesOf remaining acc =
            match remaining with
            | [] ->
                let generic = List.rev acc
                let capturedLocals, others =
                    generic
                    |> List.fold (fun (locals: Map<string, _>, others: Var list) (index, name, localTy, instances) ->
                        let fields = instances |> List.mapi (fun i (typeArguments, _) -> typeArguments, (if i = 0 then index + 1 else atoms.Length + others.Length + i))
                        locals |> Map.add name (localTy, fields), others @ (List.tail instances |> List.map snd)) (Map.empty, [])
                let firsts = generic |> List.map (fun (index, _, _, instances) -> index, snd (List.head instances)) |> Map.ofList
                let atoms = (atoms |> List.mapi (fun index atom -> firsts |> Map.tryFind index |> Option.map AVar |> Option.defaultValue atom)) @ (others |> List.map AVar)
                k atoms capturedLocals
            | (index, name, var, localTy, used) :: others ->
                this.LocalInstanceVars scope name var used (fun vars ->
                    instancesOf others ((index, name, localTy, List.zip (List.map fst used) vars) :: acc))
        instancesOf generic []

    /// The closure of `var`, the local function `name` generic in variables of its own, at each type arguments in `used`
    /// (with what they bind): the one bound where it is defined, or else one made here, for a use in a lambda whose
    /// own type arguments are only known where its closure is made.
    member private this.LocalInstanceVars (scope: Scope) (name: string) (var: Var) (used: (IrType list * Map<int, Ty>) list) (k: Var list -> Expr) : Expr =
        let _, instances, make = scope.LocalInstances.[var.Id]
        let rec go remaining acc =
            match remaining with
            | [] -> k (List.rev acc)
            | (typeArguments, bindings) :: others ->
                match instances |> List.tryFind (fst >> (=) typeArguments), make with
                | Some (_, instance), _ -> go others (instance :: acc)
                | None, Some make ->
                    let localName = uniqueName name
                    make bindings typeArguments (fun value ->
                        let local = newVar localName Local value.Type
                        let body = go others (local :: acc)
                        mk (Let(local, value, body)) body.Type)
                | None, None -> go others (var :: acc)
        go used []

    /// The type arguments, and what they bind, of each use in `expr` of `name`, a local function of type `localTy`
    /// generic in variables of its own, where `bindings` binds the variables of the function `expr` is in as it says:
    /// none where a use's type is not known. A use in a lambda that captures it counts (see
    /// `ClosureSyntax.usesThroughClosures`).
    member private this.LocalUses (bindings: Map<int, Ty>) (localTy: Ty) (name: string) (expr: Expression) : (IrType list * Map<int, Ty>) list =
        ClosureSyntax.usesThroughClosures (fun target -> definitions |> Map.tryFind target) name expr
        |> List.map (fun reference ->
            let instanceTy =
                match reference.Kind with
                | IdentifierExpr _ -> input.ExprTypes |> Map.tryFind reference.Id
                | _ -> input.CalleeTypes |> Map.tryFind reference.Id
            this.LocalTypeArguments localTy (instanceTy |> Option.map (substitute bindings)) |> Option.defaultValue ([], Map.empty))
        |> List.distinctBy fst

    /// The type arguments the local function of type `localTy`, generic in variables of its own, is used at where the
    /// use has the type `instanceTy`, and what they bind; none where that is not known.
    member private this.LocalTypeArguments (localTy: Ty) (instanceTy: Ty option) : (IrType list * Map<int, Ty>) option =
        instanceTy
        |> Option.bind (fun instanceTy ->
            try
                let bindings = bindScheme Map.empty localTy (substitute subst instanceTy)
                let typeArguments =
                    freeVars localTy
                    |> List.distinct
                    |> List.sort
                    |> List.map (fun id -> bindings |> Map.tryFind id |> Option.map toIrType |> Option.defaultValue IrUnit)
                Some (typeArguments, bindings)
            with CannotLower _ -> None)

    /// The variable to read for `var` where it is used at the type `instanceTy`: `var` itself, unless it holds a local
    /// function generic in variables of its own, which has a closure for each type arguments it is used at.
    member private this.LocalAt (scope: Scope) (var: Var) (instanceTy: Ty option) : Var =
        match scope.LocalInstances |> Map.tryFind var.Id with
        | Some (localTy, instances, _) ->
            let typeArguments = this.LocalTypeArguments localTy instanceTy |> Option.map fst |> Option.defaultValue []
            instances |> List.tryFind (fst >> (=) typeArguments) |> Option.map snd |> Option.defaultValue var
        | None -> var

    /// Binds `name`, a local function of type `localTy` generic in variables of its own, around `rest`, the statements
    /// after it: a closure for each type arguments the function is used at in `rest`, the closure `rhs` over the lifted
    /// lambda `target` lowered at them, so that what it does with a value of one of those variables (compares it, drops
    /// it) is what the value's type does. A use in a lambda that captures it counts, and the lambda captures the closure
    /// at each type arguments it uses it at. A use whose type is not known reads the closure lowered at none, with each
    /// of those variables unit.
    member private this.GenericLocal (scope: Scope) (name: string) (rhs: Expression) (target: string) (captured: Expression list) (localTy: Ty) (rest: StatementKind list) (k: Expr -> Expr) : Expr =
        let instances = this.LocalUses Map.empty localTy name (Expression.synthetic (Block(rest |> List.map Statement.synthetic)))
        let instances = if instances.IsEmpty then [ [], Map.empty ] else instances
        let make bindings typeArguments = this.Closure scope rhs target captured bindings typeArguments
        let closures = instances |> List.map (fun (typeArguments, bindings) -> typeArguments, make bindings typeArguments)
        this.LocalInstancesAround scope name localTy closures (Some make) rest k

    /// Binds `name`, a local function of type `localTy` generic in variables of its own, around `rest`, the statements
    /// after it, to its closure at each type arguments it is used at there, which `closures` lowers; `make` lowers it at
    /// others, where it can be.
    member private this.LocalInstancesAround (scope: Scope) (name: string) (localTy: Ty) (closures: (IrType list * ((Expr -> Expr) -> Expr)) list) (make: LocalClosure option) (rest: StatementKind list) (k: Expr -> Expr) : Expr =
        let rec bind (remaining: (IrType list * ((Expr -> Expr) -> Expr)) list) (bound: (IrType list * Var) list) =
            match remaining with
            | [] ->
                let var = bound |> List.tryFind (fst >> List.isEmpty) |> Option.defaultValue (List.head bound) |> snd
                this.Block { scope with Vars = scope.Vars |> Map.add name var; LocalInstances = scope.LocalInstances |> Map.add var.Id (localTy, bound, make) } rest k
            | (typeArguments, closure) :: others ->
                let localName = uniqueName name
                closure (fun value ->
                    let local = newVar localName Local value.Type
                    let body = bind others ((typeArguments, local) :: bound)
                    mk (Let(local, value, body)) body.Type)
        bind closures []

    /// How the hole `hole` of an interpolated string is printed, from its type. A tag's cases are in the order its type
    /// names them, which is the order they are tested in.
    member this.HoleType (scope: Scope) (hole: Expression) : HoleType =
        match this.TypeIn scope hole with
        | IrString -> HoleString
        | IrInt -> HoleInt
        | IrSized integer -> HoleSized integer
        | IrBool -> HoleBool
        | IrFloat -> HoleFloat
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
        this.Atoms scope (List.map snd fields) (fun atoms -> k (mk (MakeRecord(List.zip (List.map fst fields) atoms)) ty))

    /// A call through the function value `closure`, whose result is of type `ty`. A function whose input is a tuple
    /// takes the tuple's elements (see `IrType.tupleElements`): a tuple passed whole has them read from it. The members
    /// of the contexts the value requires follow, as they are in scope here.
    member this.CallValue (scope: Scope) (closure: Var) (args: Expression list) (ty: IrType) (k: Expr -> Expr) : Expr =
        let members, parameterTys =
            match closure.Type with
            | IrFunction(input, _, members) ->
                members, (IrType.tupleElements input |> Option.map (List.map snd) |> Option.defaultValue [ input ])
            | _ -> [], []
        let call atoms =
            this.CoerceAll scope atoms (if parameterTys.Length = atoms.Length then parameterTys else []) (fun atoms ->
                this.MemberAtoms scope members (fun memberAtoms -> k (mk (CallClosure(closure, atoms @ memberAtoms)) ty)))
        let elements =
            match closure.Type with
            | IrFunction(input, _, _) -> IrType.tupleElements input
            | _ -> None
        match elements, flattenCallArgs args with
        | Some elements, [ tuple ] ->
            this.Atoms scope [ tuple ] (fun atoms ->
                let rec loadElements (remaining: (int * IrType) list) (acc: Atom list) =
                    match remaining with
                    | [] -> call (List.rev acc)
                    | (slot, elementTy) :: rest ->
                        bindTemp (mk (Field(atoms.Head, slot)) elementTy) (fun atom -> loadElements rest (atom :: acc))
                loadElements elements [])
        | _, flatArgs -> this.Atoms scope flatArgs call

    /// The parameters a lifted function takes for its parameters `names` of types `tys` (its environment aside), and
    /// how to bind those names around its body. A lone parameter whose type is a tuple takes the tuple's elements, which
    /// the body then has as one tuple again (see `IrType.tupleElements`).
    member private this.ValueParameters (names: string list) (tys: IrType list) : Var list * (Expr -> Expr) * Var list =
        let tupleElements =
            match tys with
            | [ ty ] -> IrType.tupleElements ty
            | _ -> None
        match names, tys, tupleElements with
        | [ name ], [ tupleTy ], Some elementTys ->
            let elements = elementTys |> List.mapi (fun index (_, ty) -> newVar (uniqueName $"{name}_{index}") Param ty)
            let tuple = newVar (uniqueName name) Local tupleTy
            // Each element in its slot of the tuple, under its position's name.
            let fieldValues =
                List.zip elementTys elements
                |> List.mapi (fun index ((slot, _), element) -> slot, (string index, AVar element))
                |> List.sortBy fst
                |> List.map snd
            let bind (body: Expr) = mk (Let(tuple, mk (MakeRecord fieldValues) tupleTy, body)) body.Type
            elements, bind, [ tuple ]
        | _ ->
            let parameters = List.map2 (fun name ty -> newVar (uniqueName name) Param ty) names tys
            parameters, id, parameters

    /// Lowers `expr` in tail position: the whole of what is left to do.
    member this.Tail (scope: Scope) (expr: Expression) : Expr = this.Complex scope expr id

    /// Lowers `exprs` in order to atoms, binding each value that is not one to a temporary. An operand that stores to a
    /// local on the way to its value (see `storesBeforeValue`) is lowered whole, its stores with it, into one
    /// temporary: it is computed where it is used, as the old path computes it, so nothing computed before it waits on
    /// the stack across its stores.
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

    member this.Block (scope: Scope) (statements: StatementKind list) (k: Expr -> Expr) : Expr =
        match statements with
        | [] -> cannot "an empty block"
        | [ ExprStatement last ] -> this.Complex scope last k
        | DefStatement(_, _, _, Kind (Lambda _)) :: _ -> cannot "a local function"
        | DefStatement(_, name, _, (Kind (ClosureSyntax.Closure(target, captured)) as rhs)) :: rest
            when (genericLocalType target rhs).IsSome ->
            this.GenericLocal scope name rhs target captured (genericLocalType target rhs).Value rest k
        | DefStatement(_, name, _, Kind (MemberAccess(Kind (IdentifierExpr environment), _, _))) :: rest
            when environment = ClosureSyntax.envParameter && scope.CapturedLocals.ContainsKey name ->
            // A captured local function generic in variables of its own, whose closure at each type arguments it is
            // used at here is in a field of the environment.
            let localTy, fields = scope.CapturedLocals.[name]
            let read (field: int) =
                this.Complex scope (Expression.synthetic (MemberAccess(Expression.synthetic (IdentifierExpr environment), string field, None)))
            this.LocalInstancesAround scope name localTy (fields |> List.map (fun (typeArguments, field) -> typeArguments, read field)) None rest k
        | DefStatement(_, name, _, rhs) :: rest ->
            // A local value generic in variables of its own, such as the builder's member a workflow keyword reads
            // (`def m = proc.try`, then `m(args)`), is lowered once. Where it has one use, it is lowered at that use's
            // type arguments, so that what it reads is specialised as it is used rather than with its variables unit.
            // The contexts its function types require stay as they are: a member is passed them where it is called.
            match input.ExprTypes |> Map.tryFind rhs.Id |> Option.map (substitute subst) with
            | Some localTy when not (freeVars localTy).IsEmpty ->
                match this.LocalUses Map.empty localTy name (Expression.synthetic (Block(rest |> List.map Statement.synthetic))) with
                | [ (_, bindings) ] ->
                    let values = freeVars localTy |> Set.ofList
                    subst <- bindings |> Map.filter (fun id _ -> values.Contains id) |> Map.fold (fun acc id ty -> acc |> Map.add id ty) subst
                | _ -> ()
            | _ -> ()
            // The local is named before its value is lowered, so that locals are declared in the order they are
            // written. Its type is its value's, which a local lambda lifting made has as well.
            let localName = uniqueName name
            this.Complex scope rhs (fun value ->
                let local = newVar localName Local value.Type
                let members =
                    match scope.CapturedMembers |> Map.tryFind name with
                    | Some keys -> keys |> List.fold (fun acc key -> acc |> Map.add key local) scope.Members
                    | None -> scope.Members
                let body = this.Block { scope with Vars = scope.Vars |> Map.add name local; Members = members } rest k
                mk (Let(local, value, body)) body.Type)
        | ExprStatement first :: rest ->
            this.Complex scope first (fun value ->
                let body = this.Block scope rest k
                mk (Do(value, body)) body.Type)
        | UseStatement(Kind (RecordExpr fields)) :: rest ->
            // Lambda lifting has evaluated each binding into a local; the members are those locals. A function that
            // requires contexts its member's type does not is adapted to the type, with their members as they are here,
            // before the `use` (see `Coerce`), and the local holds the adapted closure from here on.
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
                    this.Block { scope with Members = members; Vars = vars } rest k
                | (key, local) :: others ->
                    match declaredMemberTypes |> Map.tryFind key with
                    | Some memberTy when vars.[local] = scope.Vars.[local] ->
                        this.Coerce scope (AVar vars.[local]) (toIrType memberTy) (function
                            | AVar var when var = vars.[local] -> adapt others vars
                            | adapted ->
                                let var = newVar (uniqueName local) Local (atomType adapted)
                                let body = adapt others (vars |> Map.add local var)
                                mk (Let(var, mk (Atom adapted) var.Type, body)) body.Type)
                    | _ -> adapt others vars
            adapt bindings scope.Vars
        | UseStatement _ :: _ -> cannot "this kind of `use`"
        | _ -> cannot "this kind of statement"

    member this.Match (scope: Scope) (expr: Expression) (values: Expression list) (arms: (Parser.Program.Pattern list * Expression) list) (k: Expr -> Expr) : Expr =
        let ty = tyOf expr
        let scrutinees =
            values |> List.mapi (fun index value -> newVar $"__match_tmp_{scope.MatchDepth + index}" MatchTemp (tyOf value))
        let armScope = { scope with MatchDepth = scope.MatchDepth + values.Length }
        let lowerArm (patterns: Parser.Program.Pattern list, body: Expression) =
            if patterns.Length <> values.Length then cannot "a match arm with a different number of patterns"
            let mutable armVars = armScope.Vars
            let mutable payloads = Set.empty
            let rec lowerPattern (isTop: bool) (scrutinee: Var) (valueTy: IrType) (pattern: Parser.Program.Pattern) =
                match pattern.Kind with
                | WildcardPattern
                | UnitPattern
                | ElsePattern -> PWildcard
                // A name for the whole scrutinee is the scrutinee's local.
                | IdentifierPattern name when isTop ->
                    armVars <- armVars |> Map.add name scrutinee
                    PWildcard
                | IdentifierPattern name ->
                    let baseName = $"__payload_{sanitizeIdentifier name}"
                    let mutable candidate = baseName
                    let mutable index = 1
                    while armScope.LivePayloads.Contains candidate || payloads.Contains candidate do
                        candidate <- $"{baseName}_{index}"
                        index <- index + 1
                    payloads <- payloads.Add candidate
                    let var = newVar candidate PatternBound valueTy
                    armVars <- armVars |> Map.add name var
                    PBind var
                | LiteralPattern(IntLit value) -> PInt(int64 value)
                | LiteralPattern(StringLit value) -> PString value
                // `| { % 2 == 0 } ->`: the predicate is inlined, its parameter a local the value matched here is put
                // in each time the arm is tested. It sees what is in scope at the match, not what the arm binds.
                | GuardPattern(Kind (Lambda([ (parameter, _) ], predicateBody, _))) ->
                    let var = newVar (uniqueName "__guard") Scratch valueTy
                    PGuard(var, this.Tail { armScope with Vars = armScope.Vars |> Map.add parameter var } predicateBody)
                | ListPattern(elements, rest) ->
                    let elementTy =
                        match IrType.unfold valueTy with
                        | IrList elementTy -> elementTy
                        | other -> cannot $"a list pattern on a value of type {IrType.show other}"
                    PList(elements |> List.map (lowerPattern false scrutinee elementTy), rest |> Option.map (lowerPattern false scrutinee valueTy))
                | LiteralPattern(BoolLit value) -> PBool value
                | TagPattern(name, payload) ->
                    let payloadTy =
                        match IrType.unfold valueTy with
                        | IrTags tags ->
                            match tags |> List.tryFind (fst >> (=) name) with
                            | Some (_, payloadTy) -> payloadTy
                            | None -> raise NeverMatches
                        | _ -> cannot $"the pattern #{name} on a value that is not a tag"
                    match payload, payloadTy with
                    | Some inner, Some innerTy -> PTag(name, Some (lowerPattern false scrutinee innerTy inner))
                    | None, _ -> PTag(name, None)
                    | Some _, None -> cannot $"a payload pattern on #{name}, which has none"
                | TuplePattern elements ->
                    let fields =
                        match IrType.unfold valueTy with
                        | IrRecord fields when fields.Length = elements.Length -> fields
                        | other -> cannot $"a tuple pattern on a value of type {IrType.show other}"
                    let names = fields |> List.map fst |> Set.ofList
                    PFields(elements |> List.mapi (fun index element ->
                        let field = Pattern.tupleElementField names index element
                        let slot = fields |> List.findIndex (fst >> (=) field)
                        slot, lowerPattern false scrutinee (snd fields.[slot]) element))
                // `(status = #some(v), ...)`: each member matches the field it names; the fields it leaves out are not
                // looked at.
                | RecordMemberPattern(members, _) ->
                    let fields =
                        match IrType.unfold valueTy with
                        | IrRecord fields -> fields
                        | other -> cannot $"a record pattern on a value of type {IrType.show other}"
                    PFields(members |> List.map (fun (name, member') ->
                        match fields |> List.tryFindIndex (fst >> (=) name) with
                        | Some slot -> slot, lowerPattern false scrutinee (snd fields.[slot]) member'
                        | None -> cannot $"a record pattern on the field '{name}', which the value does not have"))
                // `$Money(p)`: a nominal value is its shape's value, which the inner pattern matches. Its name gets a
                // local even at the top of an arm, as the old path gives it one.
                | RecordPattern(typeName, [ inner ]) when (typeName.Split('.') |> Array.last).StartsWith "$" ->
                    lowerPattern false scrutinee valueTy inner
                // Every part matches the value matched here, so a name at the top of the arm is still the scrutinee.
                | AndPattern parts -> PAll(parts |> List.map (lowerPattern isTop scrutinee valueTy))
                | _ -> cannot "this kind of pattern"
            // An arm for a tag the value cannot have never matches, so it is left out.
            match (try Some (List.map2 (fun scrutinee pattern -> lowerPattern true scrutinee scrutinee.Type pattern) scrutinees patterns) with NeverMatches -> None) with
            | Some loweredPatterns ->
                let bodyScope = { armScope with Vars = armVars; LivePayloads = Set.union armScope.LivePayloads payloads }
                Some { Patterns = loweredPatterns; Body = this.Tail bodyScope body }
            | None -> None
        // The scrutinees are evaluated in order, each into its local, before any arm is tested.
        let rec bindScrutinees (remaining: (Var * Expression) list) =
            match remaining with
            | [] -> k (mk (Match(scrutinees, arms |> List.choose lowerArm)) ty)
            | (scrutinee, value) :: rest ->
                this.Complex scope value (fun lowered ->
                    let body = bindScrutinees rest
                    mk (Let(scrutinee, lowered, body)) body.Type)
        match values |> List.tryFindIndex (fun value -> tyOf value = IrError) with
        | Some unknown ->
            // A value of no known type cannot be matched. The values are evaluated in order up to it, for what they do
            // (it most likely stops the program itself, through the deferred error that left its type unknown), and
            // if the program is still running then, it stops there.
            let stop = mk (Panic [ Interpolation.unknownScrutineeMessage values.[unknown].Span ]) ty
            values
            |> List.take (unknown + 1)
            |> List.rev
            |> List.fold (fun (rest: Expr) value -> this.Complex scope value (fun lowered -> mk (Do(lowered, rest)) rest.Type)) stop
            |> k
        | None -> bindScrutinees (List.zip scrutinees values)

    /// The start function: each top-level expression in turn, its value dropped. The start function returns nothing,
    /// so nothing in it is a tail call; its body ends in unit, which the emitter leaves out (see `emitStart`).
    member this.StartFunction () : Function =
        let scope = emptyScope
        // An expression that holds errors deferred to run time, and cannot be lowered because of them, reports them
        // when it is reached instead.
        let lowerExpression (expr: Expression) =
            try this.Tail scope expr
            with CannotLower _ when not (deferredMessages expr).IsEmpty -> mk (Panic(deferredMessages expr)) IrError
        let body =
            List.foldBack (fun (expr: Expression) (rest: Expr) -> mk (Do(lowerExpression expr, rest)) IrUnit) input.TopLevel (mk (Atom AUnit) IrUnit)
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
            @ (contextParameters |> List.map (fun parameter -> newVar parameter.Name Param IrError))
            @ (variableMembers name subst |> List.map (fun (key, _) -> newVar (contextMemberParameter key) Param IrError))
        { Name = functionName
          Source = name
          TypeArguments = typeArguments
          Params = parameters
          Result = IrError
          Body = mk (Panic messages) IrError }

    /// The types of the parameters of the lifted lambda `name` (its environment aside), whose type where it was written
    /// is `lambdaTy`, and of its result. A lambda bound to a context member in a `use` may have parameters the type
    /// checker left open; lambda lifting annotates them with the types the member declares.
    member this.LiftedParameterTypes (name: string) (lambdaTy: Ty) : Ty list * Ty =
        let parameters =
            match definitions.[name].Kind with
            | Lambda(parameters, _, _) -> parameters.Tail
            | _ -> []
        let parameterTys, resultTy = this.ParameterTypes (List.map fst parameters) lambdaTy true
        let annotated =
            List.map2 (fun (ty: Ty) (_, annotation) ->
                match annotation |> Option.bind (typeOfAnnotation input.NamedTypes) with
                | Some annotatedTy when not (freeVars (substitute subst ty)).IsEmpty -> annotatedTy
                | _ -> ty) parameterTys parameters
        annotated, resultTy

    /// The types of the parameters `names` of a function of type `fnTy`, and of its result. Several parameters are the
    /// fields of the input, each named by its position or by its own name when the type names it
    /// (`(int, step: int) -> int`).
    member private this.ParameterTypes (names: string list) (fnTy: Ty) (isLambda: bool) : Ty list * Ty =
        match names, Unifier.unalias fnTy with
        | [], TyFunc(_, result, _) when isLambda -> [], result
        | [], _ -> [], fnTy
        | [ _ ], TyFunc(input, result, _) -> [ input ], result
        | several, TyFunc(input, result, _) ->
            match Unifier.unalias input with
            | TyRecord fields when fields.Count = several.Length ->
                several
                |> List.mapi (fun index parameterName ->
                    match fields |> Map.tryFind (string index), fields |> Map.tryFind parameterName with
                    | Some ty, _
                    | None, Some ty -> ty
                    | None, None -> cannot $"a function whose parameter {parameterName} is not among its input's fields"), result
            | _ -> cannot "a function whose parameters are not a tuple"
        | _ -> cannot "a definition whose type is not a function"

    /// Lowers the definition `name` to a function called `functionName`, as `lowering` says.
    member this.Function (lowering: Lowering) (name: string) (functionName: string) (typeArguments: IrType list) : Function =
        selfName <- functionName
        match lowering with
        | AsStart -> this.StartFunction ()
        | Adapting(givenTy, expectedTy) -> this.Adapter functionName givenTy expectedTy
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
        | AsStart -> this.StartFunction ()
        | AsDefined ->
            let defTy =
                match input.ExprTypes |> Map.tryFind expr.Id with
                | Some ty -> substitute subst ty
                | None -> cannot "a definition without a type"
            let parameterTys, resultTy = this.ParameterTypes parameterNames defTy isLambda
            let parameters = List.map2 (fun parameterName ty -> parameterOf parameterName (toIrType ty)) parameterNames parameterTys
            // The members of the contexts it requires follow, each a closure in a parameter of its own.
            let contextParameters, aliases = input.ContextParameters |> Map.tryFind name |> Option.defaultValue ([], Map.empty)
            let members =
                contextParameters
                |> List.map (fun parameter ->
                    match parameter.Type with
                    | Some ty -> parameter.Key, newVar parameter.Name Param (toIrType (substitute subst ty))
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
            // A function value it results in is adapted to the type it declares (see `Coerce`).
            let result = toIrType resultTy
            let adapted (value: Expr) =
                match value.Type with
                | IrFunction _ when value.Type <> result -> bindTemp value (fun atom -> this.Coerce scope atom result (fun atom -> mk (Atom atom) result))
                | _ -> value
            make (parameters @ List.map snd members) result (this.Complex scope body adapted)
        | LiftedCapturing(capturedTys, capturedLocals) ->
            // The environment first: the function's table slot, then what it captures. The rest are the lambda's
            // parameters, typed as the lambda was where it was written.
            let lambda, owner =
                match input.Lifted |> Map.tryFind name with
                | Some (LiftedLambda(lambda, _, owner)) -> lambda, owner
                | _ -> cannot $"'{name}' is not a lifted lambda"
            let lambdaTy =
                match input.ExprTypes |> Map.tryFind lambda.Id with
                | Some ty -> substitute subst ty
                | None -> cannot "a lambda without a type"
            let environment = IrRecord(("0", IrWord) :: (capturedTys |> List.mapi (fun index ty -> string (index + 1), ty)))
            let parameterTys, resultTy = this.LiftedParameterTypes name lambdaTy
            let environmentParameter = parameterOf parameterNames.Head environment
            let valueParameters, bind, named = this.ValueParameters parameterNames.Tail (List.map toIrType parameterTys)
            // The owner's context members are bound from the environment with what else it captures.
            let capturedMembers =
                let contextParameters, aliases = input.ContextParameters |> Map.tryFind owner |> Option.defaultValue ([], Map.empty)
                contextParameters
                |> List.map (fun parameter ->
                    let otherNames = aliases |> Map.filter (fun _ key -> key = parameter.Key) |> Map.keys |> Seq.map NamedMember |> List.ofSeq
                    parameter.Name, parameter.Key :: otherNames)
                |> Map.ofList
            // The members of the contexts the lambda requires where it is called follow its other parameters.
            // They stand for the owner's members of those keys, which it may still capture to make a closure that does
            // not take them.
            let members =
                membersOf (requiredContexts lambdaTy) |> List.map (fun (key, ty) -> key, newVar (uniqueName (contextMemberParameter key)) Param ty)
            selfMembers <- members |> List.map (fun (key, var) -> key, var.Type)
            let passed = members |> List.map fst |> Set.ofList
            let capturedMembers = capturedMembers |> Map.map (fun _ keys -> keys |> List.filter (passed.Contains >> not))
            let scope = { scopeOf (environmentParameter :: named) with CapturedMembers = capturedMembers; CapturedLocals = capturedLocals }
            let scope =
                { scope with
                    Members = members |> List.fold (fun acc (key, var) -> acc |> Map.add key var) scope.Members
                    Vars = members |> List.fold (fun acc (key, var) -> acc |> Map.add (contextMemberParameter key) var) scope.Vars }
            make (environmentParameter :: valueParameters @ List.map snd members) (toIrType resultTy) (bind (this.Tail scope body))
        | WrapperCalling(wrapped, instanceTy, captured) ->
            // Takes an environment, which holds after its table slot the members of the contexts the function requires
            // that the closure captured, and after its other parameters those its value's type requires; and calls the
            // function with the rest and them all, in the order it takes them.
            let parameterTys, resultTy = this.ParameterTypes parameterNames.Tail instanceTy true
            let environment = IrRecord(("0", IrWord) :: (captured |> List.mapi (fun index (_, ty) -> string (index + 1), ty)))
            let environmentParameter = parameterOf parameterNames.Head environment
            let valueParameters, bind, named = this.ValueParameters parameterNames.Tail (List.map toIrType parameterTys)
            let passed =
                match toIrType instanceTy with
                | IrFunction(_, _, members) -> members |> List.map (fun (key, ty) -> key, newVar (contextMemberParameter key) Param ty)
                | _ -> []
            let declared = input.ContextParameters |> Map.tryFind wrapped |> Option.map (fst >> List.map _.Key) |> Option.defaultValue []
            let order = declared @ (variableMembers wrapped (bindingsAt wrapped instanceTy) |> List.map fst)
            let rec call (remaining: ContextMemberKey list) (members: Atom list) =
                match remaining with
                | [] -> mk (Call(callee wrapped instanceTy, (named |> List.map AVar) @ List.rev members)) (toIrType resultTy)
                | key :: rest ->
                    match captured |> List.tryFindIndex (fst >> (=) key), passed |> List.tryFind (fst >> (=) key) with
                    | Some index, _ ->
                        bindTemp (mk (Field(AVar environmentParameter, index + 1)) (snd captured.[index])) (fun atom -> call rest (atom :: members))
                    | None, Some (_, var) -> call rest (AVar var :: members)
                    | None, None -> internalError $"the context member {ContextMemberKey.text key} of '{wrapped}' is neither captured nor passed"
            make (environmentParameter :: valueParameters @ List.map snd passed) (toIrType resultTy) (bind (call order []))
        | Adapting _ -> internalError "an adapter lowered as a definition"
        | Printing _ -> internalError "a printer lowered as a definition"

    /// The adapter `functionName` that lets a function value of type `givenTy` be called as one of `expectedTy` (see
    /// `Coerce`): it takes an environment that holds the value, then the members `givenTy` requires that `expectedTy`
    /// does not, and its arguments and the members `expectedTy` requires; and calls the value with the arguments and
    /// the members it requires.
    member private this.Adapter (functionName: string) (givenTy: IrType) (expectedTy: IrType) : Function =
        let given, result, wanted =
            match givenTy, expectedTy with
            | IrFunction(_, _, given), IrFunction(_, result, wanted) -> given, result, wanted
            | _ -> internalError $"an adapter from {IrType.show givenTy} to {IrType.show expectedTy}"
        let missing = given |> List.filter (fun (key, _) -> not (wanted |> List.exists (fst >> (=) key)))
        let environment = newVar ClosureSyntax.envParameter Param (IrRecord(("0", IrWord) :: ("1", givenTy) :: (missing |> List.mapi (fun index (_, ty) -> string (index + 2), ty))))
        let valueTys = adaptedValueTypes givenTy expectedTy
        let values = valueTys |> List.mapi (fun index ty -> newVar $"__arg{index + 1}" Param ty)
        let passed = wanted |> List.map (fun (key, ty) -> key, newVar (contextMemberParameter key) Param ty)
        let scope = { emptyScope with Members = passed |> Map.ofList }
        let adapted = newVar "__adapted" Local givenTy
        let rec call (remaining: (ContextMemberKey * IrType) list) (members: Atom list) =
            match remaining with
            | [] -> mk (CallClosure(adapted, (values |> List.map AVar) @ List.rev members)) result
            | (key, ty) :: rest ->
                match missing |> List.tryFindIndex (fst >> (=) key) with
                | Some index -> bindTemp (mk (Field(AVar environment, index + 2)) ty) (fun atom -> call rest (atom :: members))
                | None -> this.Coerce scope (AVar scope.Members.[key]) ty (fun atom -> call rest (atom :: members))
        let body = mk (Let(adapted, mk (Field(AVar environment, 1)) givenTy, call given [])) result
        { Name = functionName
          Source = functionName
          TypeArguments = []
          Params = environment :: values @ List.map snd passed
          Result = result
          Body = body }
