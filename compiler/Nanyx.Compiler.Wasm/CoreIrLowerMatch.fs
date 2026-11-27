/// How the lowering (see `CoreIrLower`) lowers a `match`: its scrutinees each into a local, and each arm's patterns to
/// the core IR's (`Pattern` in `CoreIr.fs`).
module internal Transpiler.Wasm.CoreIrLowerMatch

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes
open Transpiler.Wasm.CoreIrLowerScope

/// A pattern the value it is matched against can never match.
exception private NeverMatches

/// What lowering a `match` needs from the function being lowered.
type MatchTools =
    { /// A variable of this name, kind and type, named as it is.
      NewVar: string -> VarKind -> IrType -> Var
      /// A parameter's or local's name: as written, made unique within the function.
      UniqueName: string -> string
      /// The type of an expression, at the function's type arguments.
      TypeOf: Expression -> IrType
      /// Lowers an expression in tail position.
      Tail: Scope -> Expression -> Expr
      /// Lowers an arm's body in tail position, adapted to the `match`'s type.
      Branch: Scope -> Expression -> IrType -> Expr
      /// Lowers an expression to a value that is not a `Let`, and passes that on.
      Complex: Scope -> Expression -> (Expr -> Expr) -> Expr
      /// The root the program names its files from (see `PackageModules.shownFile`).
      SourceRoot: string option }

/// Lowers the `match` `expr` of `values` against `arms`, its value passed to `k`.
let lowerMatch (tools: MatchTools) (scope: Scope) (expr: Expression) (values: Expression list) (arms: (Parser.Program.Pattern list * Expression) list) (k: Expr -> Expr) : Expr =
    let ty = tools.TypeOf expr
    let scrutinees =
        values |> List.mapi (fun index value -> tools.NewVar $"__match_tmp_{scope.MatchDepth + index}" MatchTemp (tools.TypeOf value))
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
                let candidate =
                    $"__payload_{sanitizeIdentifier name}"
                    |> freeName (fun candidate -> armScope.LivePayloads.Contains candidate || payloads.Contains candidate)
                payloads <- payloads.Add candidate
                let var = tools.NewVar candidate PatternBound valueTy
                armVars <- armVars |> Map.add name var
                PBind var
            | LiteralPattern(IntLit value) -> PInt(intBits value)
            | LiteralPattern(StringLit value) when valueTy = IrSized SizedInteger.U32 ->
                match Chars.scalarOf value with
                | Some scalar -> PInt(int64 scalar)
                | None -> internalError "a char pattern that is not one character, which the checker reports"
            | LiteralPattern(StringLit value) -> PString value
            | LiteralPattern(FloatLit value) -> PFloat value
            // `| { % 2 == 0 } ->`: the predicate is inlined, its parameter a local the value matched here is put
            // in each time the arm is tested. It sees what is in scope at the match, not what the arm binds.
            | GuardPattern(Kind (Lambda([ (parameter, _) ], predicateBody, _))) ->
                let var = tools.NewVar (tools.UniqueName "__guard") Scratch valueTy
                PGuard(var, tools.Tail { armScope with Vars = armScope.Vars |> Map.add parameter var } predicateBody)
            | ListPattern(elements, rest) ->
                let elementTy =
                    match IrType.unfold valueTy with
                    | IrList elementTy -> elementTy
                    | other -> cannot $"a list pattern on a value of type '{IrType.show other}'"
                PList(elements |> List.map (lowerPattern false scrutinee elementTy), rest |> Option.map (lowerPattern false scrutinee valueTy))
            | LiteralPattern(BoolLit value) -> PBool value
            | TagPattern(name, payload) ->
                let payloadTy =
                    match IrType.unfold valueTy with
                    | IrTags tags ->
                        match tags |> List.tryFind (fst >> (=) name) with
                        | Some (_, payloadTy) -> payloadTy
                        | None -> raise NeverMatches
                    | _ -> cannot $"the pattern `#{name}` on a value that is not a tag"
                match payload, payloadTy with
                | Some inner, Some innerTy -> PTag(name, Some (lowerPattern false scrutinee innerTy inner))
                | None, _ -> PTag(name, None)
                | Some _, None -> cannot $"a payload pattern on `#{name}`, which has none"
            // `(a, b)`, or `(a, ...)`, which leaves the other fields out.
            | TuplePattern(elements, hasRest) ->
                let fields =
                    match IrType.unfold valueTy with
                    | IrRecord fields when fields.Length = elements.Length || (hasRest && fields.Length >= elements.Length) -> fields
                    | other -> cannot $"a tuple pattern on a value of type '{IrType.show other}'"
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
                    | other -> cannot $"a record pattern on a value of type '{IrType.show other}'"
                PFields(members |> List.map (fun (name, member') ->
                    match fields |> List.tryFindIndex (fst >> (=) name) with
                    | Some slot -> slot, lowerPattern false scrutinee (snd fields.[slot]) member'
                    | None -> cannot $"a record pattern on the field '{name}', which the value does not have"))
            // `Money(p)`: a nominal value is its shape's value, which the patterns in its brackets match (see
            // `Pattern.constructorPayload`), as they match the value of a type that names a shape. A name gets a local
            // even at the top of an arm.
            | RecordPattern(_, patterns) -> lowerPattern false scrutinee valueTy (Parser.Program.Pattern.constructorPayload pattern patterns)
            // Every part matches the value matched here, so a name at the top of the arm is still the scrutinee.
            | AndPattern parts -> PAll(parts |> List.map (lowerPattern isTop scrutinee valueTy))
            | _ -> cannot "this kind of pattern"
        // An arm for a tag the value cannot have never matches, so it is left out.
        match (try Some (List.map2 (fun scrutinee pattern -> lowerPattern true scrutinee scrutinee.Type pattern) scrutinees patterns) with NeverMatches -> None) with
        | Some loweredPatterns ->
            let bodyScope = { armScope with Vars = armVars; LivePayloads = Set.union armScope.LivePayloads payloads }
            Some { Patterns = loweredPatterns; Body = tools.Branch bodyScope body ty }
        | None -> None
    // The scrutinees are evaluated in order, each into its local, before any arm is tested.
    let rec bindScrutinees (remaining: (Var * Expression) list) =
        match remaining with
        | [] -> k (Expr.make (Match(scrutinees, arms |> List.choose lowerArm)) ty)
        | (scrutinee, value) :: rest ->
            tools.Complex scope value (fun lowered ->
                let body = bindScrutinees rest
                Expr.make (Let(scrutinee, lowered, body)) body.Type)
    match values |> List.tryFindIndex (fun value -> tools.TypeOf value = IrError) with
    | Some unknown ->
        // A value of no known type cannot be matched. The values are evaluated in order up to it, for what they do
        // (it most likely stops the program itself, through the deferred error that left its type unknown), and
        // if the program is still running then, it stops there.
        let stop = Expr.make (Crash([ Interpolation.unknownScrutineeMessage tools.SourceRoot values.[unknown].Span ], true)) ty
        values
        |> List.take (unknown + 1)
        |> List.rev
        |> List.fold (fun (rest: Expr) value -> tools.Complex scope value (fun lowered -> Expr.make (Do(lowered, rest)) rest.Type)) stop
        |> k
    | None -> bindScrutinees (List.zip scrutinees values)
