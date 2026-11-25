/// Where a record's field or a context's member is written by its name in a module, beyond what the type checker
/// records as references: a field in each record type, construction, read, update and pattern, and a member where an
/// instance of its context is written. Rename finds these for the names that are not symbols (a field), or not only
/// (a member).
module NyxCompiler.Refactorings.FieldSites

open Parser.Program
open NyxCompiler

/// The record a field belongs to, as far as the types the checker inferred tell.
type Record =
    /// A protected or private type's record, by the type's identity.
    | Nominal of NominalId
    /// A structural record, by the names of its named fields: two records with the same names are one type, so a
    /// value of one can be passed where the other is taken.
    | Shape of Set<string>
    /// A record that may have fields other than those known, `(x: a, ...)`, or a value whose type is a variable: a
    /// function that reads `r.x` of any record with an `x`.
    | Open
    /// Not a record, as a string whose `length` is read is not.
    | NotARecord

/// A field's name, `Name`, written at `Span`, of `Record`. `Shorthand` is the node of the tuple pattern it is written in
/// for a name alone there that takes apart the field of the same name (`x` in `(x, y)`).
type FieldSite = { Name: string; Span: SourceSpan; Record: Record; Shorthand: NodeId option }

/// A context member's name where an instance of its context is written, `log` in `use Log(log = ...)`, with the
/// member's symbol.
type MemberSite = { Member: Symbol; Span: SourceSpan }

let private isPositional (name: string) = name |> Seq.forall System.Char.IsDigit

/// The record a value of type `ty` is.
let recordOf (ty: Ty) : Record =
    match Unifier.unalias ty with
    | TyNominal(id, _, _, _) -> Nominal id
    | TyRecord(fields, _) -> Shape(fields.Keys |> Seq.filter (isPositional >> not) |> Set.ofSeq)
    | TyOpenRecord _
    | TyVar _ -> Open
    | _ -> NotARecord

/// The type of each field of a value of type `ty`, if it is a record.
let rec private fieldTypes (ty: Ty) : Map<string, Ty> option =
    match Unifier.unalias ty with
    | TyNominal(_, _, underlying, _) -> fieldTypes underlying
    | TyRecord(fields, _)
    | TyOpenRecord(fields, _) -> Some fields
    | _ -> None

/// The type of the payload of the tag `tag` in a value of type `ty`, if it has one.
let rec private payloadType (tag: string) (ty: Ty) : Ty option =
    match Unifier.unalias ty with
    | TyTag(name, payload) when name = tag -> payload
    | TyUnion items -> items |> List.tryPick (payloadType tag)
    | TyNominal(_, _, underlying, _) -> payloadType tag underlying
    | _ -> None

/// Where `holder` declares the name `name` as `declarer` (see `DeclaredNames`).
let private declaredAt (typed: TypedModule) (holder: NodeId) (declarer: Declarer) (name: string) =
    typed.Declared
    |> Map.tryFind (holder, declarer)
    |> Option.defaultValue []
    |> List.tryFind (fst >> (=) name)
    |> Option.map snd

/// The type definition the name `node` is written with refers to, with its symbol: `N` in `N(x = 4)`, `Log` in
/// `use Log(...)`, `Sum(int)` in `use Sum(int)(...)`.
let private typeDefinitionAt (typed: TypedModule) (node: NodeId) =
    WrittenReferences.ofModule typed
    |> Map.tryFind node
    |> Option.bind typed.Symbols.TryFind
    |> Option.filter (fun symbol -> symbol.Kind = TypeDefinition)
    |> Option.bind (Definition.typeDefinitionOf typed)

/// What a record written in an expression builds: a record, or an instance of a context.
type private Built =
    | ARecord of Record
    | AnInstance of TypeDefInfo

/// What each record written in `syntax` builds, by its node: a type's constructor's argument builds a value of the type,
/// a context's an instance of it; the fields `with` updates and a literal with a spread build the record of the whole;
/// any other is a record of its own type, a call's named arguments among them.
let private builtRecords (typed: TypedModule) (syntax: Module) : (Expression * Built) list =
    let typeOf (expr: Expression) = typed.ExprTypes |> Map.tryFind expr.Id
    let expressions = Hover.allExpressions syntax
    let special =
        expressions
        |> List.collect (fun expr ->
            let wholeRecord (record: Expression) = [ record.Id, ARecord(typeOf expr |> Option.map recordOf |> Option.defaultValue Open) ]
            match expr.Kind with
            | WithSyntax.Call(_, updates) -> wholeRecord updates
            | FunctionCall(name, _, [ record ]) when name = SpreadSyntax.builtIn -> wholeRecord record
            | FunctionCall(_, _, [ Kind (RecordExpr _) as record ]) ->
                match typeDefinitionAt typed expr.Id with
                | Some info when info.IsContext -> [ record.Id, AnInstance info ]
                | Some _ -> wholeRecord record
                | None -> []
            | Block statements ->
                statements
                |> List.choose (fun statement ->
                    match statement.Kind with
                    | DefStatement(_, _, Some context, (Kind (RecordExpr _) as members)) ->
                        TypeExpr.within context
                        |> List.tryPick (fun typeExpr -> typeDefinitionAt typed typeExpr.Id)
                        |> Option.filter _.IsContext
                        |> Option.map (fun info -> members.Id, AnInstance info)
                    | _ -> None)
            | _ -> [])
        |> Map.ofList
    expressions
    |> List.choose (fun expr ->
        match expr.Kind with
        | RecordExpr _ ->
            match special |> Map.tryFind expr.Id with
            | Some built -> Some (expr, built)
            | None -> Some (expr, ARecord(typeOf expr |> Option.map recordOf |> Option.defaultValue Open))
        | _ -> None)

/// The names of the named fields of `record`, a record written in an expression, each with where it is written.
let private namedFields (typed: TypedModule) (record: Expression) =
    match record.Kind with
    | RecordExpr fields ->
        fields
        |> List.choose (function
            | NamedField(name, value) -> declaredAt typed value.Id NamedFieldValue name |> Option.map (fun span -> name, span)
            | PositionalField _ -> None)
    | _ -> []

/// The fields `pattern`, matched against a value of type `ty` if it is known, takes apart by name, and those of the
/// patterns inside it.
let rec private patternFields (typed: TypedModule) (ty: Ty option) (pattern: Pattern) : FieldSite list =
    let fields = ty |> Option.bind fieldTypes
    let record = ty |> Option.map recordOf |> Option.defaultValue Open
    let fieldType name = fields |> Option.bind (Map.tryFind name)
    match pattern.Kind with
    | RecordMemberPattern(members, _) ->
        members
        |> List.collect (fun (name, inner) ->
            let site =
                declaredAt typed inner.Id NamedFieldValue name
                |> Option.map (fun span -> { Name = name; Span = span; Record = record; Shorthand = None })
                |> Option.toList
            site @ patternFields typed (fieldType name) inner)
    | TuplePattern(elements, _) ->
        let names = fields |> Option.map (fun fields -> fields.Keys |> Set.ofSeq) |> Option.defaultValue Set.empty
        elements
        |> List.mapi (fun index element ->
            let field = Pattern.tupleElementField names index element
            let shorthand =
                if isPositional field then []
                else [ { Name = field; Span = element.Span; Record = record; Shorthand = Some pattern.Id } ]
            shorthand @ patternFields typed (fieldType field) element)
        |> List.concat
    | TagPattern(tag, Some payload) -> patternFields typed (ty |> Option.bind (payloadType tag)) payload
    | AndPattern patterns -> patterns |> List.collect (patternFields typed ty)
    | RecordPattern(_, patterns)
    | ListPattern(patterns, None) -> patterns |> List.collect (patternFields typed None)
    | ListPattern(patterns, Some rest) -> patterns @ [ rest ] |> List.collect (patternFields typed None)
    | ListSplatMiddle(before, after) -> before @ after |> List.collect (patternFields typed None)
    | ArgumentTagPattern(_, _, Some payload) -> patternFields typed None payload
    | TagPattern(_, None)
    | ArgumentTagPattern(_, _, None)
    | GuardPattern _
    | RangePattern _
    | LiteralPattern _
    | IdentifierPattern _
    | WildcardPattern
    | UnitPattern
    | ElsePattern -> []

/// The fields written in the record types of `typeExpr` and the types inside it. The body of a type definition is
/// `defined`'s record, if it is one: a protected or private type's, by its identity.
let private typeFields (typed: TypedModule) (defined: TypeDefInfo option) (typeExpr: TypeExpr) : FieldSite list =
    TypeExpr.within typeExpr
    |> List.collect (fun inner ->
        let record =
            match inner.Kind, defined with
            | (TypeRecord _ | TypeOpenRecord _), Some info when inner.Id = typeExpr.Id && info.IsNominal -> Some (Nominal(TypeDefInfo.nominalId info))
            | TypeRecord fields, _ ->
                fields
                |> List.choose (function TypeField(name, _, _, _) when not (isPositional name) -> Some name | _ -> None)
                |> Set.ofList
                |> Shape
                |> Some
            | TypeOpenRecord _, _ -> Some Open
            | _ -> None
        match record with
        | Some record ->
            typed.Declared
            |> Map.tryFind (inner.Id, RecordTypeFields)
            |> Option.defaultValue []
            |> List.map (fun (name, span) -> { Name = name; Span = span; Record = record; Shorthand = None })
        | None -> [])

/// The type definitions in `syntax`, each with the type it defines: at the top level, in module blocks and in blocks.
let private typeDefinitions (typed: TypedModule) (syntax: Module) : (TypeExpr * TypeDefInfo option) list =
    SyntaxTree.writtenTypes syntax
    |> List.map (fun written ->
        match written.As with
        | SyntaxTree.TypeBody name -> written.Type, typed.TypeDefs |> Map.tryFind name
        | _ -> written.Type, None)

/// Every place in the compiled root module `result` that writes a record's field by its name: in record types (not a
/// context's, whose fields are its members), records written in expressions (not a context's instance), field reads
/// and the patterns of `match` and `except`, each with the record it belongs to. Empty when it did not type-check.
let fields (result: CompileResult) : FieldSite list =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let typeOf (expr: Expression) = typed.ExprTypes |> Map.tryFind expr.Id
        let inTypes =
            typeDefinitions typed syntax
            |> List.filter (fun (_, info) -> not (info |> Option.exists _.IsContext))
            |> List.collect (fun (typeExpr, info) -> typeFields typed info typeExpr)
        let inRecords =
            builtRecords typed syntax
            |> List.collect (fun (record, built) ->
                match built with
                | ARecord owner -> namedFields typed record |> List.map (fun (name, span) -> { Name = name; Span = span; Record = owner; Shorthand = None })
                | AnInstance _ -> [])
        let inExpressions =
            Hover.allExpressions syntax
            |> List.collect (fun expr ->
                match expr.Kind with
                | MemberAccess(target, field, nameSpan) ->
                    let span = nameSpan |> Option.defaultValue { expr.Span with Start = expr.Span.End - field.Length; StartColumn = expr.Span.StartColumn + (expr.Span.End - expr.Span.Start) - field.Length }
                    [ { Name = field; Span = span; Record = typeOf target |> Option.map recordOf |> Option.defaultValue Open; Shorthand = None } ]
                | Match(scrutinees, arms) ->
                    arms
                    |> List.collect (fun (patterns, _) ->
                        List.zip (List.truncate patterns.Length scrutinees) (List.truncate scrutinees.Length patterns)
                        |> List.collect (fun (scrutinee, pattern) -> patternFields typed (typeOf scrutinee) pattern))
                | Except(value, arms) -> arms |> List.collect (fst >> List.collect (patternFields typed (typeOf value)))
                | _ -> [])
        inTypes @ inRecords @ inExpressions
        |> List.filter (fun site -> not (SourceSpan.isNone site.Span))
        |> List.distinctBy (fun site -> site.Span.Start, site.Name)
    | _ -> []

/// Every place in the compiled root module `result` where a context's member is written by name as an instance of the
/// context is written, `use Log(log = ...)` or `use Sum(int)(add = ...)`, with the member's symbol. Empty when it did not
/// type-check.
let members (result: CompileResult) : MemberSite list =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        builtRecords typed syntax
        |> List.collect (fun (record, built) ->
            match built with
            | AnInstance info ->
                namedFields typed record
                |> List.choose (fun (name, span) ->
                    info.Members |> Map.tryFind name |> Option.bind typed.Symbols.TryFind |> Option.map (fun symbol -> { Member = symbol; Span = span }))
            | ARecord _ -> [])
    | _ -> []
